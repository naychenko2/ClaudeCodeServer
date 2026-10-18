import { useEffect, useMemo, useState } from 'react';
import { Route } from 'lucide-react';
import { C, FONT, FS } from '../../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../ui/icons';
import { Button, Modal } from '../../ui';
import { api } from '../../../lib/api';
import { FeatureGlyph } from './FeatureGlyphs';
import { FeatureRunView } from './FeatureRunView';
import type { FeatureBannerModel } from './FeatureBanner';
import type { FeatureChatLink, FeatureIndexFeature, FeaturesIndex } from '../../../types/featureIndex';
import type { FeatureRun } from '../../../types/featureRun';
import type { Session } from '../../../types';

// Шторка фичи (мобила, 360 px; макет features-overview.html, .sheet/.scrim, sheetStrip):
// тап по плашке её открывает. Состав (в порядке макета):
//   • шапка — глиф + название фичи + счётчик;
//   • «Этот чат: {роль}» — роль этого чата в фиче;
//   • кнопка «В ключевой чат фичи» (когда этот чат — не ключевой);
//   • конвейер — карточка фичи (FeatureRunView: тревога, «Сейчас», 7 стадий, легенда);
//   • «Чаты фичи · N» — чаты фичи из индекса (chats): имена — из списка собственных
//     чатов, «чужие» (их в списке нет) не показываем.
//
// Шторка — это Modal: на мобиле он сам bottom-sheet (drag-handle, крестик, Escape,
// тап по подложке) — штатный паттерн продукта (docs/design/guidelines.md: «модалки на
// мобиле — шторкой»).
//
// Имена чатов: индекс несёт только id сессий (chats: Record<sessionId, links[]>),
// поэтому шторка запрашивает список чатов пользователя (сессии проекта, либо глобальные
// чаты, если чат вне проекта) и показывает только найденные в нём. Запрос — один на
// открытие; сбой не фатален (список пуст, остальная шторка жива).

// Сила связи (контракт: key > curator > orchestrator > executor) — порядок списка
// чатов. Ключевой чат сортируется отдельно, по id (keySessionId).
const KIND_ORDER: Record<FeatureChatLink['kind'], number> = {
  key: 0, curator: 1, orchestrator: 2, executor: 3,
};
// Роли задач в порядке конвейера (макет ROLE_ORDER):
// cut < plan < step < rework < review < qa < final < merge.
const TASK_ROLE_ORDER: Record<string, number> = {
  cut: 0, plan: 1, step: 2, rework: 3, review: 4, qa: 5, final: 6, merge: 7,
};

// Ключ сортировки связи «чат × фича»: сила, затем (у executor) роль задачи.
export function linkRank(link: FeatureChatLink): number {
  const base = KIND_ORDER[link.kind] ?? 3;
  if (link.kind === 'executor' && link.taskRole != null) {
    return base * 10 + (TASK_ROLE_ORDER[link.taskRole] ?? 8);
  }
  return base * 10;
}

// Чаты фичи из индекса: все пары (id, link), у которых link.featureId совпадает,
// в порядке linkRank (ключевой чат сортирует вызывающий, по id).
export function featureChatLinks(index: FeaturesIndex, featureId: string): { id: string; link: FeatureChatLink }[] {
  const out: { id: string; link: FeatureChatLink }[] = [];
  for (const [id, links] of Object.entries(index.chats)) {
    for (const link of links) {
      if (link.featureId === featureId) out.push({ id, link });
    }
  }
  out.sort((a, b) => linkRank(a.link) - linkRank(b.link));
  return out;
}

// Подпись роли строки чата: stepRef · lane (исполнитель с шагом), иначе — вид связи.
export function chatRoleLabel(link: FeatureChatLink): string {
  const parts = [link.stepRef, link.lane].filter(Boolean) as string[];
  if (parts.length) return parts.join(' · ');
  switch (link.kind) {
    case 'curator': return 'куратор';
    case 'orchestrator': return 'оркестратор';
    case 'executor': return 'исполнитель';
    default: return 'ключевой';
  }
}

// Чистое: строки «Чаты фичи» — id из индекса (featureChatLinks), имена — только из
// списка собственных чатов пользователя (sessions); «чужие» (id не в sessions) не
// показываем. Ключевой чат (feature.keySessionId) — первым, остальные — в порядке linkRank.
// Чистая функция — тестируется без DOM (SSR-рендер шторки держит список в загрузке).
export function sheetChatRows(
  index: FeaturesIndex,
  feature: FeatureIndexFeature,
  sessions: Session[],
): { session: Session; link: FeatureChatLink }[] {
  const byId = new Map(sessions.map(s => [s.id, s]));
  const rows: { session: Session; link: FeatureChatLink }[] = [];
  for (const { id, link } of featureChatLinks(index, feature.featureId)) {
    const session = byId.get(id);
    if (session) rows.push({ session, link });
  }
  rows.sort((a, b) =>
    (a.session.id === feature.keySessionId ? 0 : 1) - (b.session.id === feature.keySessionId ? 0 : 1));
  return rows;
}

interface FeatureBannerSheetProps {
  // Строка фичи из индекса (название, keySessionId, rail).
  feature: FeatureIndexFeature;
  // Модель плашки (глиф/статус/счётчик/роль/кнопка) — та же, что рисует плашка.
  // «Этот чат: {role}» берёт model.role (роль текущего чата уже посчитана плашкой).
  model: FeatureBannerModel;
  // Проект чата (null — чат вне проектов): область запроса списка чатов.
  projectId: string | null;
  // Индекс фич (источник чатов фичи: chats[sessionId][]).
  index: FeaturesIndex;
  // Агрегат фичи (конвейер); null — ещё не загружен (конвейер не рисуется).
  run: FeatureRun | null;
  // Переход в ключевой чат (задаёт плашка: recordBackNav + openChatById).
  onGoKey: () => void;
  // Закрыть шторку (крестик/Escape/подложка Modal).
  onClose: () => void;
}

export function FeatureBannerSheet({ feature, model, projectId, index, run, onGoKey, onClose }: FeatureBannerSheetProps) {
  // Список собственных чатов (для имён чатов фичи): сессии проекта либо
  // глобальные чаты, если чат вне проекта. null — ещё грузится.
  const [sessions, setSessions] = useState<Session[] | null>(null);
  useEffect(() => {
    let cancelled = false;
    const p = projectId ? api.sessions.list(projectId) : api.chats.list();
    void p
      .then(list => { if (!cancelled) setSessions(list); })
      .catch(() => { if (!cancelled) setSessions([]); });
    return () => { cancelled = true; };
  }, [projectId]);

  // Чаты фичи: id из индекса, имена — только из списка пользователя («чужие» не
  // показываем); ключевой чат первым, остальные — в порядке linkRank (sheetChatRows).
  // null — список ещё грузится (useEffect-фетч не прошёл).
  const chatRows = useMemo(
    () => (sessions ? sheetChatRows(index, feature, sessions) : null),
    [sessions, index, feature],
  );

  return (
    <Modal onClose={onClose}>
      {/* Шапка: глиф + название + счётчик (макет: первая строка .sheet .in) */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, minWidth: 0 }}>
        <FeatureGlyph kind={model.glyph} size={14} />
        <span
          title={feature.title}
          style={{
            fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 700, color: C.textHeading,
            flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
          }}
        >
          {feature.title}
        </span>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, flexShrink: 0 }}>
          {model.counter}
        </span>
      </div>

      {/* Этот чат: роль в фиче (макет: «Этот чат: <role>») */}
      <div
        data-testid="feature-sheet-role"
        style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: FS.sm, color: C.textSecondary, minWidth: 0 }}
      >
        Этот чат:
        <span style={{
          fontFamily: FONT.mono, fontSize: FS.xs,
          // n2: у ключевого вес 700 (макет .role.key) — совпадает с чипом плашки.
          fontWeight: model.isKey ? 700 : 400,
          color: model.isKey ? C.accent : C.textSecondary,
          background: model.isKey ? C.accentLight : C.bgSelected,
          borderRadius: 6, padding: '0 5px', whiteSpace: 'nowrap',
        }}>
          {model.role}
        </span>
      </div>

      {/* В ключевой чат фичи: только когда этот чат — не ключевой и живой ключевой есть.
          Архивный ключевой (keyArchived) помечен в подписи — человек не попадает в
          архив без предупреждения (R3, минор 1). */}
      {!model.isKey && model.button.kind === 'key' && (
        <Button
          variant="primary"
          size="md"
          fullWidth
          leftIcon={<Route size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
          onClick={onGoKey}
        >
          {feature.keyArchived ? 'В ключевой чат фичи (в архиве)' : 'В ключевой чат фичи'}
        </Button>
      )}

      {/* Конвейер: карточка фичи (тревога, «Сейчас», 7 стадий, легенда) */}
      {run && (
        <div data-testid="feature-sheet-pipeline">
          <FeatureRunView run={run} />
        </div>
      )}

      {/* Чаты фичи: свои (из списка пользователя), «чужие» не показываем */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
        <div style={{ fontSize: FS.xxs, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase', color: C.textMuted, margin: '2px 0 4px' }}>
          Чаты фичи · {chatRows ? chatRows.length : feature.chatCount}
        </div>
        {chatRows == null ? (
          <div style={{ fontSize: FS.sm, color: C.textMuted }}>Загружаем список…</div>
        ) : chatRows.length === 0 ? (
          <div style={{ fontSize: FS.sm, color: C.textMuted }}>Чатов пока нет</div>
        ) : (
          chatRows.map(({ session, link }) => {
            const isKey = session.id === feature.keySessionId;
            return (
              <div
                key={session.id}
                data-testid="feature-sheet-chat"
                style={{
                  display: 'flex', alignItems: 'center', gap: 8,
                  borderRadius: 8, padding: '8px 10px',
                  background: isKey ? C.bgSelected : 'transparent',
                  minWidth: 0,
                }}
              >
                <span
                  title={session.name ?? undefined}
                  style={{ flex: 1, minWidth: 0, fontSize: FS.base, fontWeight: isKey ? 700 : 500, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}
                >
                  {session.name ?? session.id}
                </span>
                <span style={{
                  fontFamily: FONT.mono, fontSize: FS.xs, flexShrink: 0,
                  // n2: у ключевого вес 700 (макет .role.key) — совпадает с чипом плашки.
                  fontWeight: isKey ? 700 : 400,
                  color: isKey ? C.accent : C.textSecondary,
                  background: isKey ? C.accentLight : C.bgSelected,
                  borderRadius: 6, padding: '0 5px', whiteSpace: 'nowrap',
                }}>
                  {isKey ? 'ключевой' : chatRoleLabel(link)}
                </span>
              </div>
            );
          })
        )}
      </div>
    </Modal>
  );
}
