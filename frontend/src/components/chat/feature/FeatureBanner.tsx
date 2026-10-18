import { useEffect, useState, type ReactNode } from 'react';
import { C, FS, R, SP, FONT } from '../../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { ArrowRight, ChevronDown } from 'lucide-react';
import { useFeature, FLAGS } from '../../../lib/featureFlags';
import { useFeaturesIndex, featureOf, type FeatureOfEntry } from '../../../lib/featuresIndex';
import { useFeatureRuns } from '../../../lib/featureRuns';
import { useBackNavFrom, getBackNav, clearBackNav, recordBackNav } from '../../../lib/featureBackNav';
import { openChatById } from '../../../lib/openChat';
import { openTaskInSection } from '../../../lib/tasks';
import { featureDisplayState } from '../../../lib/chatGroups';
import { FeatureGlyph, type FeatureGlyph as GlyphKind } from './FeatureGlyphs';
import { FeatureRail } from './FeatureRail';
import { FeatureModeBody } from './FeatureModeBody';
import { dateOnly } from './featureTime';
import type { FeatureIndexFeature, FeatureChatLink, FeatureChatKind } from '../../../types/featureIndex';

// Плашка фичи в чате (макет features-overview.html, .fs; записка
// features-overview-proposal.md, «Общее для всех вариантов: плашка фичи»).
//
// Сценарий: «я открыл чат Дениса и не понимаю, где фича и где главный чат».
// Плашка отвечает: глиф состояния · название фичи · мини-рельса · статус ·
// счётчик · роль ЭТОГО чата в фиче · кнопка перехода в ключевой чат.
//
// Фронт НИЧЕГО не считает: источник правды — индекс фич (lib/featuresIndex.ts,
// бэк считает bucket/attention/rail/summary/ключевой чат). Компонент только
// раскладывает строку фичи в глиф/тон/статус/счётчик/роль.
//
// Показывается под ДВУМЯ флагами: turn-plan-feature-mode (поверхность режима
// «Исполнение фичи») и features-overview (загрузка индекса). Dark launch:
// оба выключены — плашки нет, лента как раньше.
//
// Тон фона (записка Майи): ждёт вас/к мержу — C.infoBg, упала — C.dangerBg,
// закрытая/смержена — C.bgInset (серая, второй план), идёт/доработка — C.bgCard
// с рамкой C.borderLight.
//
// Ф4 (волна 1 «Обзор фич»):
//   • В КЛЮЧЕВОМ чате вместо перехода «Ключевой чат →» — «Конвейер ▾»: раскрывает
//     под плашкой тело режима «Исполнение фичи» (FeatureModeBody) + легенду; данные
//     конвейера — из стора featureRuns по фиче.
//   • «← назад» (слева) — только ПОСЛЕ перехода из плашки чата фичи в ключевой чат:
//     возвращает в исходный чат одним кликом. При обычном открытии чата — нет.
//   • Мультифича (чат связан с ≥ 2 фичами): «фича X · ещё N» с выбором следующей;
//     порядок — как отдал бэк (по силе связи), фронт не сортирует и не считает.

// Тон фона плашки.
type BannerTone = 'wait' | 'stop' | 'closed' | 'run';

// Кнопка справа: 'key' — в ключевой чат (openChatById), 'feature' — открыть
// фичу в трекере (openTaskInSection), когда живого ключевого чата нет;
// 'pipeline' — «Конвейер ▾» в ключевом чате (раскрывает FeatureModeBody).
interface BannerButton {
  label: string;
  kind: 'key' | 'feature' | 'pipeline';
}

// Модель плашки: чистая функция от строки фичи + связи чата. Тестируется
// без рендера (SSR не нужен для логики).
export interface FeatureBannerModel {
  tone: BannerTone;
  glyph: GlyphKind;
  status: string;
  counter: string;
  // Роль этого чата: «ключевой» (сам ключевой) или «этот чат: Ш2 · бэк».
  role: string;
  isKey: boolean;
  button: BannerButton;
}

// Подпись роли чата по виду связи (для curator/orchestrator без stepRef/lane).
const KIND_LABEL: Record<FeatureChatKind, string> = {
  key: 'ключевой',
  curator: 'куратор',
  orchestrator: 'оркестратор',
  executor: 'исполнитель',
};

// Чистая модель: строка фичи + связь «этот чат × фича» → что рисует плашка.
// sessionId — id текущего чата: «этот чат ключевой» = (sessionId === keySessionId)
// (ADR-031 §1: бэк вид key в chats не отдаёт).
export function featureBannerModel(
  feature: FeatureIndexFeature,
  link: FeatureChatLink,
  sessionId: string,
): FeatureBannerModel {
  const st = featureDisplayState(feature);
  const isKey = feature.keySessionId != null && sessionId === feature.keySessionId;

  // Тон фона: тревога (wait/stop) → цветной, закрыта/смержена → серая, иначе нейтр.
  let tone: BannerTone;
  if (st.alarm === 'wait') tone = 'wait';
  else if (st.alarm === 'stop') tone = 'stop';
  else if (st.kind === 'closed' || st.kind === 'done') tone = 'closed';
  else tone = 'run';

  // Статус: у смерженной — «смержена <дата>» (локальное время, featureTime),
  // у закрытой без мержа — «закрыта», иначе статус из featureDisplayState.
  let status: string;
  if (st.kind === 'done') {
    const d = dateOnly(feature.summary?.mergedAt ?? null);
    status = d ? `смержена ${d}` : 'смержена';
  } else if (st.kind === 'closed') {
    status = 'закрыта';
  } else {
    status = st.status;
  }

  // Счётчик: принятые/все шаги + число возвратов (↺N).
  const counter = feature.loops > 0
    ? `${feature.stepsAccepted}/${feature.stepsTotal} ↺${feature.loops}`
    : `${feature.stepsAccepted}/${feature.stepsTotal}`;

  // Роль этого чата: ключевой — «ключевой», иначе «этот чат: Ш2 · бэк»
  // (stepRef · lane из связи; у curator/orchestrator без шага — роль по виду).
  let role: string;
  if (isKey) {
    role = 'ключевой';
  } else {
    const parts = [link.stepRef, link.lane].filter(Boolean) as string[];
    role = parts.length ? parts.join(' · ') : (KIND_LABEL[link.kind] ?? 'участник');
  }

  // Кнопка: в КЛЮЧЕВОМ чате — «Конвейер ▾» (раскрывает тело фичи); в другом
  // чате фичи — есть живой ключевой чат → «Ключевой чат →» (openChatById, сам
  // переключает проект), живого нет (keySessionId = null) → «Открыть фичу»
  // (openTaskInSection на эпик, без перехода в чат).
  let button: BannerButton;
  if (isKey) {
    button = { label: 'Конвейер', kind: 'pipeline' };
  } else if (feature.keySessionId != null) {
    button = { label: 'Ключевой чат', kind: 'key' };
  } else {
    button = { label: 'Открыть фичу', kind: 'feature' };
  }

  return { tone, glyph: st.kind, status, counter, role, isKey, button };
}

// «← назад»: роль исходного чата в ЭТОЙ фиче (stepRef · lane; без шага — роль по виду).
// Пусто — исходный чат не принадлежит этой фиче (переход не из её плашки).
export function backRoleLabel(sourceChatId: string, featureId: string): string {
  const entry = featureOf(sourceChatId).find(e => e.feature.featureId === featureId);
  if (!entry) return '';
  const parts = [entry.link.stepRef, entry.link.lane].filter(Boolean) as string[];
  return parts.length ? parts.join(' · ') : (KIND_LABEL[entry.link.kind] ?? 'участник');
}

// Мультифича: число ДРУГИХ фич (entries.length − 1). Фронт не сортирует и не
// считает порядок — только показывает, сколько ещё фич у чата (порядок — от бэка).
export function moreFeaturesCount(entries: FeatureOfEntry[]): number {
  return Math.max(0, entries.length - 1);
}

// Тон фона → стиль подложки/рамки плашки.
function toneStyle(tone: BannerTone): React.CSSProperties {
  switch (tone) {
    case 'wait':
      return { background: C.infoBg, border: '1px solid transparent' };
    case 'stop':
      return { background: C.dangerBg, border: '1px solid transparent' };
    case 'closed':
      return { background: C.bgInset, border: '1px solid transparent' };
    case 'run':
    default:
      return { background: C.bgCard, border: `1px solid ${C.borderLight}` };
  }
}

// Локальная ghost-кнопка (вид = Button variant=ghost size=sm): общий Button не
// прокидывает data-testid/aria-expanded, а они нужны для тестов и a11y.
function GhostButton({ testId, onClick, children, ariaExpanded, style }: {
  testId?: string;
  onClick: () => void;
  children: ReactNode;
  ariaExpanded?: boolean;
  style?: React.CSSProperties;
}) {
  return (
    <button
      data-testid={testId}
      aria-expanded={ariaExpanded}
      onClick={onClick}
      style={{
        display: 'flex', alignItems: 'center', gap: 4,
        background: 'transparent', color: C.textSecondary, border: `1px solid ${C.border}`,
        borderRadius: R.lg, padding: '7px 13px', fontSize: FS.md, minHeight: 32,
        fontWeight: 600, fontFamily: FONT.sans, cursor: 'pointer', flexShrink: 0,
        ...style,
      }}
    >
      {children}
    </button>
  );
}

interface FeatureBannerProps {
  // Id текущего чата (для featureOf и «этот чат ключевой»).
  sessionId: string;
  // Проект чата — область запроса индекса (null — чат вне проектов).
  projectId: string | null;
  isMobile?: boolean;
}

// Плашка фичи в слоте баннеров ChatPanel. Возвращает null, когда плашки нет
// (флаги выключены или чат не принадлежит ни одной фиче).
export function FeatureBanner({ sessionId, projectId, isMobile = false }: FeatureBannerProps) {
  // Гейт: оба флага (режим «Исполнение фичи» + «Обзор фич»).
  const featureMode = useFeature(FLAGS.turnPlanFeatureMode);
  const featuresOverview = useFeature(FLAGS.featuresOverview);
  const enabled = featureMode && featuresOverview;
  // Индекс фич проекта (реактивная подписка; fetch идемпотентен, дедуплицируется).
  useFeaturesIndex(projectId, enabled);
  // Агрегаты фич чата (для «Конвейер ▾» в ключевом чате): по sessionId, гейт тем же флагом.
  const runs = useFeatureRuns(sessionId, featureMode);
  // «← назад»: исходный чат (в памяти вкладки); null, пока перехода из плашки не было.
  const backFrom = useBackNavFrom(sessionId);

  // Мультифича: какая фича показана (индекс в featureOf, порядок — от бэка).
  const [selIdx, setSelIdx] = useState(0);
  // «Конвейер ▾»: раскрытие (локальное, сбрасывается при смене чата/фичи).
  const [fxOpen, setFxOpen] = useState(false);

  // Фичи, к которым принадлежит чат; первая — сильнейшая связь (порядок — от бэка).
  const entries = featureOf(sessionId);
  // Кламп, если фич стало меньше; сброс выбора/раскрытия при смене чата.
  const idx = Math.min(selIdx, Math.max(0, entries.length - 1));

  // Сброс «← назад» при обычном открытии чата (не из плашки): записанный переход
  // ведёт в ДРУГОЙ чат, а не в этот — мажет S.prev = null (макет).
  useEffect(() => {
    const back = getBackNav();
    if (back && back.to !== sessionId) clearBackNav();
  }, [sessionId]);

  // Сброс раскрытия/выбора при смене чата (иначе новое чат наследовало бы состояние).
  useEffect(() => { setSelIdx(0); setFxOpen(false); }, [sessionId]);

  if (!enabled || entries.length === 0) return null;

  const { feature, link } = entries[idx];
  const model = featureBannerModel(feature, link, sessionId);
  const isKey = model.isKey;
  const more = moreFeaturesCount(entries);

  // «← назад»: только в ключевом чате, после перехода из плашки, и исходный чат
  // принадлежит ЭТОЙ фиче (иначе роль пуста → кнопки нет).
  const backLabel = isKey && backFrom ? backRoleLabel(backFrom, feature.featureId) : '';
  const goBack = () => {
    if (backFrom) void openChatById(backFrom);
    clearBackNav();
  };
  // «Ключевой чат →»: запоминаем, откуда пришли (для «← назад» в ключевом чате).
  const goKey = () => {
    if (!feature.keySessionId) return;
    recordBackNav(sessionId, feature.keySessionId);
    void openChatById(feature.keySessionId);
  };
  // «Конвейер ▾»: раскрывает/сворачивает тело фичи под плашкой.
  const toggleFx = () => setFxOpen(o => !o);
  // Мультифича: выбор следующей фичи (цикл по порядку от бэка).
  const cycleFeature = () => setSelIdx(i => (i + 1) % entries.length);

  // Тело «Конвейер ▾»: FeatureModeBody (режим «Исполнение фичи») + легенда (внутри
  // FeatureRunView). Агрегат — из стора featureRuns по фиче (runs[0] = эта фича).
  const run = runs.find(r => r.featureId === feature.featureId) ?? null;

  return (
    <div
      role="region"
      aria-label="Фича этого чата"
      data-testid="feature-banner"
      data-banner-tone={model.tone}
      style={{
        ...toneStyle(model.tone),
        display: 'flex',
        flexDirection: 'column',
        borderRadius: R.lg,
        padding: isMobile ? SP.sm : `10px ${SP.lg}px`,
        margin: `${SP.sm}px ${isMobile ? 16 : 16}px 0`,
        boxSizing: 'border-box',
        fontFamily: FONT.sans,
      }}
    >
      {/* Строка плашки */}
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        {/* «← назад»: слева, только после перехода из плашки (ключевой чат). */}
        {backLabel && (
          <GhostButton testId="feature-back" onClick={goBack}>
            ← {backLabel}
          </GhostButton>
        )}
        {/* Глиф состояния фичи */}
        <FeatureGlyph kind={model.glyph} size={13} />
        {/* Название фичи: одна строка, обрезка многоточием */}
        <span
          title={feature.title}
          style={{
            fontSize: FS.md, fontWeight: 700, color: C.textHeading,
            whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
            minWidth: 40, maxWidth: '40%',
          }}
        >
          {feature.title}
        </span>
        {/* Мультифича: «· ещё N» (N = другие фичи) — выбор следующей. */}
        {more > 0 && (
          <GhostButton testId="feature-more" onClick={cycleFeature}>
            · ещё {more}
          </GhostButton>
        )}
        {/* Мини-рельса (7 сегментов) */}
        <FeatureRail rail={feature.rail} closed={feature.bucket === 'closed'} />
        {/* Статус */}
        <span style={{ fontSize: FS.sm, color: C.textSecondary, whiteSpace: 'nowrap' }}>
          {model.status}
        </span>
        {/* Счётчик шагов + возвраты */}
        <span style={{
          fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap',
          fontFamily: FONT.mono, flexShrink: 0,
        }}>
          {model.counter}
        </span>
        {/* Роль этого чата: «ключевой» или «· этот чат: Ш2 · бэк» */}
        <span style={{ fontSize: FS.xs, color: C.textSecondary, whiteSpace: 'nowrap', flexShrink: 0 }}>
          {model.isKey ? model.role : `· этот чат: ${model.role}`}
        </span>
        {/* Кнопка: «Конвейер ▾» (ключевой) / «Ключевой чат →» / «Открыть фичу» */}
        <GhostButton
          testId={model.button.kind === 'pipeline' ? 'feature-pipeline' : 'feature-go'}
          ariaExpanded={model.button.kind === 'pipeline' ? fxOpen : undefined}
          style={{ marginLeft: 'auto' }}
          onClick={() => {
            if (model.button.kind === 'pipeline') toggleFx();
            else if (model.button.kind === 'key') goKey();
            else openTaskInSection({ id: feature.featureId, projectId: feature.projectId ?? undefined });
          }}
        >
          {model.button.label}
          {model.button.kind === 'pipeline'
            ? <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ marginLeft: 4, transform: fxOpen ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
            : <ArrowRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ marginLeft: 4 }} />}
        </GhostButton>
      </div>

      {/* «Конвейер ▾» (ключевой чат): тело режима «Исполнение фичи» + легенда. */}
      {isKey && fxOpen && run && (
        <div
          data-testid="feature-pipeline-body"
          style={{ marginTop: SP.sm, borderTop: `1px solid ${C.borderLight}`, paddingTop: SP.sm }}
        >
          <FeatureModeBody
            sessionId={sessionId}
            todos={[]}
            runs={[run]}
            isMobile={isMobile}
            variant="card"
            headerPad="0"
            bodyPad="0"
            headerBorder="none"
          />
        </div>
      )}
    </div>
  );
}
