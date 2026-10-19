import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { AlertTriangle, Inbox, RotateCw } from 'lucide-react';
import { C, R, FS } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { useFeaturePage, refetchFeaturePage, refetchFeaturePageAll } from '../../lib/featurePage';
import { getBackNav, clearBackNav } from '../../lib/featureBackNav';
import { useFeaturesIndex } from '../../lib/featuresIndex';
import { api } from '../../lib/api';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import { showToast } from '../../lib/toast';
import { featureDisplayState, featureQuickAction, featureStatusTag, formatDuration } from '../../lib/chatGroups';
import { plural } from '../../lib/plural';
import { dateOnly } from '../chat/feature/featureTime';
import { eventTimeLabel } from './featurePageBody';
import { FeatureGlyph } from '../chat/feature/FeatureGlyphs';
import { Notice } from '../ui/Notice';
import { EmptyState } from '../ui/EmptyState';
import { Button } from '../ui/Button';
import { FeaturePageHeader, type FeatureStatusTag } from './FeaturePageHeader';
import { FeaturePageBody } from './FeaturePageBody';
import type { FeatureGlyph as GlyphKind } from '../chat/feature/FeatureGlyphs';
import type { FeatureIndexFeature } from '../../types/featureIndex';
import type { Task } from '../../types';

// Куда ведёт крошка «← …»:
//   chat — в исходный чат (указатель back-nav «чат → фича»);
//   key  — в ключевой чат фичи (указателя нет, но ключевой чат есть);
//   list — в список (указателя и ключевого чата нет).
// WorkspacePage исполняет выбор: закрывает страницу и ведёт в target.
export type FeaturePageBackTarget =
  | { kind: 'chat'; chatId: string }
  | { kind: 'key'; chatId: string }
  | { kind: 'list' };

export interface FeaturePageProps {
  // Id эпика (маршрут #/project/{pid}/feature/{fid}).
  featureId: string;
  // Проект чата (область запроса индекса); null — чат вне проектов.
  projectId: string | null;
  isMobile?: boolean;
  // Исполнитель «← назад»: закрывает страницу и ведёт в target.
  onBack: (target: FeaturePageBackTarget) => void;
  // Названия чатов (M4) — из уже загруженного SessionList'ом списка сессий проекта
  // (без новых запросов). Нет названия (чат вне проекта / список ещё не пришёл) —
  // нейтральная подпись «Чат»; GUID наружу не попадает.
  chatNames?: Record<string, string>;
  // Персоны чатов (sessionId → personaId) — из того же списка сессий: аватары 18
  // в строках чатов (макет §5). Нет персоны — нейтральный круг.
  chatPersonas?: Record<string, string>;
}

// «Сколько» фичи для шапки (макет §5.1): открытая — «идёт {возраст}» (от createdAt),
// закрытая — «шла {длительность}» (summary.durationMinutes). Чистая функция: now —
// «сейчас» в мс (стабильные тесты; не задано — Date.now()).
function featureAgeLabel(f: FeatureIndexFeature | null, now?: number): string | null {
  if (!f) return null;
  if (f.bucket === 'closed') {
    const dur = f.summary?.durationMinutes;
    return dur != null ? `шла ${formatDuration(dur)}` : null;
  }
  const created = f.createdAt;
  if (!created) return null;
  const t = new Date(created).getTime();
  if (Number.isNaN(t)) return null;
  const min = Math.max(0, Math.round(((now ?? Date.now()) - t) / 60000));
  return min > 0 ? `идёт ${formatDuration(min)}` : 'идёт';
}

// Скелет загрузки (макет §5.7): 6 полос 14 px разной ширины (bgSelected, R 6, gap 10,
// пульс 1,4 с) + блок 120 (marginTop 6). При prefers-reduced-motion пульс выключен
// (инжект в FeaturePageHeader).
const SKELETON_WIDTHS = [70, 92, 84, 60, 88, 76];
function LoadingSkeleton() {
  return (
    <div data-testid="feature-page-skeleton" style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      {SKELETON_WIDTHS.map((w, i) => (
        <div
          key={i}
          className="cc-feature-page-skeleton"
          style={{ height: 14, width: `${w}%`, background: C.bgSelected, borderRadius: R.sm }}
        />
      ))}
      <div className="cc-feature-page-skeleton" style={{ height: 120, marginTop: 6, background: C.bgSelected, borderRadius: R.sm }} />
    </div>
  );
}

// Страница фичи (волна 2 «Обзор фич», шаг Ф2.4): шапка (глиф, название, статус, ветка,
// возраст, «M задач», ⋯, главное действие) + блок тревоги + состояния страницы.
//
// Компонент самодостаточен: данные — из стора useFeaturePage (Ф2.2), название/ветка/
// задачи/тревога — из индекса фич (фолбэк — эпик страницы). Крошка читает указатель
// возврата (featureBackNav). Тело — одно из пяти состояний: loading | ok | notFound |
// disabled | error (макет §5.7).
export function FeaturePage({ featureId, projectId, isMobile = false, onBack, chatNames, chatPersonas }: FeaturePageProps) {
  // Данные страницы (run + events) — из стора; здесь для фолбэка заголовка и 404/503.
  const page = useFeaturePage(featureId);
  const featuresOn = useFeature(FLAGS.featuresOverview);
  const index = useFeaturesIndex(projectId, featuresOn);
  const feature = index.features.find(f => f.featureId === featureId) ?? null;

  // Название: из индекса, фолбэк — эпик страницы, затем нейтральное. Контракт:
  // UI обязан пережить отсутствие любого поля ответа — эпик без title не роняет страницу.
  const title = feature?.title ?? (page.status === 'ok' ? (page.page.run.epic?.title ?? 'Фича') : 'Фича');

  // Глиф + тег статуса: из индекса (display state); нет фичи в индексе — нейтральные.
  const display = feature ? featureDisplayState(feature) : null;
  const glyph: GlyphKind = display ? display.kind : 'run';
  const statusTag: FeatureStatusTag | null = feature ? featureStatusTag(feature) : null;

  // Ветка, «M задач», «идёт/шла …».
  const branch = feature?.branch ?? null;
  const taskCount = feature?.taskCount ?? null;
  const ageLabel = featureAgeLabel(feature);

  // Крошка: куда ведёт и как подписана. Из чата — в исходный чат («К чату «…»»);
  // из списка/диплинк — в список: мобила «К списку», десктоп «Назад» (макет §6).
  const back = getBackNav();
  const fromChat = back && back.to.kind === 'feature' && back.to.id === featureId ? back.from : null;
  const [fromTitle, setFromTitle] = useState<string | null>(null);
  useEffect(() => {
    if (!fromChat) { setFromTitle(null); return; }
    let cancelled = false;
    void api.chats.get(fromChat)
      .then(s => { if (!cancelled) setFromTitle(s.name ?? null); })
      .catch(() => { if (!cancelled) setFromTitle(null); });
    return () => { cancelled = true; };
  }, [fromChat]);

  let target: FeaturePageBackTarget;
  let label: string;
  if (fromChat) {
    target = { kind: 'chat', chatId: fromChat };
    label = fromTitle ? `К чату «${fromTitle}»` : 'К чату';
  } else if (isMobile) {
    target = { kind: 'list' };
    label = 'К списку';
  } else {
    target = { kind: 'list' };
    label = 'Назад';
  }
  const goBack = () => { onBack(target); clearBackNav(); };

  // ⋯: «Скопировать ветку» — clipboard + тост. «Открыть эпик в «Задачах»» убран:
  // он только показывал тост (обещание перехода, которого нет — переход будет в Ф4.1).
  const copyBranch = () => {
    if (!branch) return;
    void navigator.clipboard?.writeText(branch)
      .then(() => showToast('Ветка скопирована', branch, 'success'))
      .catch(() => showToast('Ветка', 'Не удалось скопировать ветку', 'info'));
  };
  // Главное действие:
  //   «к мержу» (attention.kind merge) → «Мерж» — POST /features/{id}/merge (создаёт и
  //     запускает задачу мержа; 409 — причина, успех — задача мержа);
  //   «Ключевой чат» (нет тревоги, есть ключевой чат) → ключевой чат;
  //   «Ответить» (attention.kind wait, есть чат тревоги) → чат тревоги.
  // Мёртвых кнопок нет: «Мерж» — всегда (состояние «к мержу»); «Ключевой чат»/«Ответить»
  // — только когда есть чат (иначе мёртвое действие, M1).
  const isMerge = feature?.attention?.kind === 'merge';
  const quickAction = feature ? featureQuickAction(feature) : null;
  let mainAction: string | null = null;
  if (isMerge) mainAction = 'Мерж';
  else if (quickAction === 'Ключевой чат') mainAction = 'Ключевой чат';
  else if (quickAction === 'Ответить' && feature?.attention?.sessionId) mainAction = 'Ответить';

  // Мерж: merging — запрос в полёте (блокирует кнопку); mergeError — причина 409/сбоя;
  // mergedTask — задача мержа после успеха. mergingRef — синхронная защита от двойного
  // клика (setState батчится: второй клик в том же тике не видит merging=true).
  const [merging, setMerging] = useState(false);
  const [mergeError, setMergeError] = useState<string | null>(null);
  const [mergedTask, setMergedTask] = useState<Task | null>(null);
  const mergingRef = useRef(false);
  useEffect(() => {
    setMerging(false);
    setMergeError(null);
    setMergedTask(null);
    mergingRef.current = false;
  }, [featureId]);
  const doMerge = () => {
    if (!feature || mergingRef.current) return;
    mergingRef.current = true;
    setMerging(true);
    setMergeError(null);
    void api.tasks.mergeFeature(feature.featureId)
      .then(task => { setMergedTask(task); })
      .catch((e: Error & { status?: number; body?: unknown }) => {
        // 409: причина из ответа (body.error); иначе — сообщение ошибки.
        const reason = (e.body as { error?: string } | undefined)?.error ?? e.message;
        setMergeError(reason);
      })
      .finally(() => { mergingRef.current = false; setMerging(false); });
  };
  const onMainAction = () => {
    if (!feature) return;
    if (mainAction === 'Мерж') {
      doMerge();
      return;
    }
    if (mainAction === 'Ключевой чат') {
      if (feature.keySessionId) onBack({ kind: 'key', chatId: feature.keySessionId });
      return;
    }
    // «Ответить» — чат тревоги (M1: не ключевой чат).
    if (feature.attention?.sessionId) onBack({ kind: 'chat', chatId: feature.attention.sessionId });
  };

  // Тело: одно из пяти состояний (макет §5.7).
  let body: ReactNode;
  if (page.status === 'loading') {
    body = <LoadingSkeleton />;
  } else if (page.status === 'error') {
    body = (
      <Notice
        tone="danger"
        icon={AlertTriangle}
        title="Не удалось загрузить фичу."
        action={
          // m1: на 360 у всех ui/Button страницы minHeight 40 (тач-цель, §6).
          <Button size="xs" variant="ghost" style={{ background: C.bgWhite, ...(isMobile ? { minHeight: 40 } : {}) }}
            onClick={() => refetchFeaturePage(featureId)}>
            <RotateCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            Повторить
          </Button>
        }
      >
        Сервер не ответил; список и чаты работают как обычно.
      </Notice>
    );
  } else if (page.status === 'notFound') {
    body = (
      <EmptyState
        compact
        icon={<Inbox size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        iconRadius={R.full}
        title="Фичи больше нет"
        titleSize={FS.lg}
        subtitle="Эпик удалён в «Задачах». Чаты остались на месте и снова живут без фичи."
        action={
          <Button size="sm" variant="ghost" style={isMobile ? { minHeight: 40 } : undefined}
            onClick={() => onBack({ kind: 'list' })}>
            К списку чатов
          </Button>
        }
      />
    );
  } else if (page.status === 'disabled') {
    body = (
      <Notice tone="info" icon={Inbox} title="Подсистема задач выключена">
        Страница фичи недоступна, чаты работают как обычно.
      </Notice>
    );
  } else {
    // ok: блок тревоги/итог + конвейер/шаги/чаты/задачи/лента (макет §5, Ф2.5).
    // Лента нормализуется здесь (контракт: UI обязан пережить отсутствие любого поля
    // ответа): бэк не отдал events/eventsTotal — пустой список и 0.
    const events = page.page.events ?? [];
    const eventsTotal = page.page.eventsTotal ?? 0;
    // Открыть чат со страницы (M1/M3): та же механика Ф2.3, что у крошки — onBack
    // в чат (исполняет WorkspacePage: чат открывается, возврат — на страницу фичи).
    const openChat = (chatId: string) => onBack({ kind: 'chat', chatId });
    body = (
      <>
        <AlarmBlock feature={feature} onOpenChat={openChat} isMobile={isMobile} />
        <FeaturePageBody
          run={page.page.run}
          index={index}
          feature={feature}
          chatNames={chatNames}
          chatPersonas={chatPersonas}
          events={events}
          eventsTotal={eventsTotal}
          onShowAll={() => refetchFeaturePageAll(featureId)}
          onOpenChat={openChat}
          isMobile={isMobile}
        />
      </>
    );
  }

  return (
    <div
      data-testid="feature-page"
      style={{ flex: 1, display: 'flex', flexDirection: 'column', overflow: 'hidden', background: C.bgMain }}
    >
      <FeaturePageHeader
        title={title}
        glyph={glyph}
        statusTag={statusTag}
        branch={branch}
        ageLabel={ageLabel}
        taskCount={taskCount}
        mainAction={mainAction}
        backLabel={label}
        onBack={goBack}
        onCopyBranch={copyBranch}
        onMainAction={onMainAction}
        merging={merging}
        mergeError={mergeError}
        mergedTask={mergedTask}
        isMobile={isMobile}
        loading={page.status === 'loading'}
      />
      {/* Тело: прокрутка, поля 14 16 20 (макет §5.4). */}
      <div style={{ flex: 1, overflow: 'auto', minHeight: 0, padding: '14px 16px 20px' }}>
        {body}
      </div>
    </div>
  );
}

// Блок тревоги/итог (макет §5.3): «ждёт вас»/«к мержу» (infoBg), «стоит» (dangerBg),
// «смержена» (successBg), «без мержа» (bgSelected). Глиф 14, выделение 700, кнопка-действие.
// Нет фичи в индексе или нет тревоги/итога — блок не рисуется (тело заполнит Ф2.5).
// Кнопка ведёт в чат тревоги (attention.sessionId, M1) — не в ключевой чат.
function AlarmBlock({ feature, onOpenChat, isMobile = false }: {
  feature: FeatureIndexFeature | null;
  onOpenChat: (chatId: string) => void;
  isMobile?: boolean;
}) {
  if (!feature) return null;
  const att = feature.attention;
  const closed = feature.bucket === 'closed';
  const merged = closed && feature.summary?.mergedAt != null;

  // Закрытая: итог «смержена {дата} · за {длительность} · {M} задач» или «без мержа».
  if (closed) {
    const bg = merged ? C.successBg : C.bgSelected;
    const hi = merged ? C.successText : C.textHeading;
    // m5: «1 задача / 2–4 задачи / 5+ задач» (lib/plural), не «3 задач».
    const text = merged
      ? `Смержена ${dateOnly(feature.summary?.mergedAt) ?? ''} · за ${formatDuration(feature.summary?.durationMinutes ?? 0)}${feature.taskCount != null ? ` · ${feature.taskCount} ${plural(feature.taskCount, 'задача', 'задачи', 'задач')}` : ''}`
      : `Закрыта без мержа${feature.summary?.mergedAt ? ` ${dateOnly(feature.summary.mergedAt)}` : ''}`;
    return (
      <div data-testid="feature-page-alarm" style={{ display: 'flex', alignItems: 'center', gap: 10, margin: '0 0 16px', padding: '10px 12px', borderRadius: R.lg, background: bg, fontSize: FS.base, color: C.textPrimary }}>
        <span style={{ display: 'flex', flexShrink: 0 }}><FeatureGlyph kind={merged ? 'done' : 'closed'} size={14} /></span>
        <span style={{ flex: 1, minWidth: 0 }}><b style={{ color: hi, fontWeight: 700 }}>{text}</b></span>
      </div>
    );
  }

  // Тревога: «ждёт вас»/«к мержу» (infoBg) или «стоит» (dangerBg).
  if (!att) return null;
  const isStop = att.kind === 'stopped';
  const isMerge = att.kind === 'merge';
  const bg = isStop ? C.dangerBg : C.infoBg;
  const glyphKind: GlyphKind = isStop ? 'stopped' : 'wait';
  // «{с}» — с какого момента (attention.since), «{причина}» — attention.reason.
  // m1: ЕДИНЫЙ формат даты с лентой (eventTimeLabel: сегодня «ЧЧ:ММ», иначе «dd.mm ЧЧ:ММ»,
  // локальный пояс, не сырой ISO); пробел перед датой — внутри since (trim не срезает его).
  const since = att.since && eventTimeLabel(att.since) ? ` ${eventTimeLabel(att.since)}` : '';
  const reason = att.reason ? `: ${att.reason}` : '';
  // m3: тревога «к мержу» — текст «К мержу» (не «Ждёт вашего решения»): главное действие
  // и макет §5.3 различают «ждёт вас»/«к мержу».
  const head = isStop ? `Исполнитель молчит${since}` : isMerge ? `К мержу${since}` : `Ждёт вашего решения${since}`;

  return (
    <div data-testid="feature-page-alarm" style={{ display: 'flex', gap: 9, alignItems: 'flex-start', margin: '0 0 16px', padding: '10px 12px', borderRadius: R.lg, background: bg, fontSize: FS.base, color: C.textPrimary }}>
      <span style={{ display: 'flex', flexShrink: 0, paddingTop: 2 }}><FeatureGlyph kind={glyphKind} size={14} /></span>
      <div style={{ flex: 1, minWidth: 0 }}>
        <div><b style={{ color: C.textHeading, fontWeight: 700 }}>{head}</b>{reason}</div>
        {/* Кнопка — только когда есть чат тревоги (attention.sessionId): без него она мёртвая (M1). */}
        {att.sessionId && (
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginTop: 8 }}>
            <Button size="xs" variant={isStop ? 'ghost' : 'primary'} style={isStop ? { background: C.bgWhite, ...(isMobile ? { minHeight: 40 } : {}) } : (isMobile ? { minHeight: 40 } : undefined)} onClick={() => onOpenChat(att.sessionId!)}>
              {isStop ? 'Открыть его чат' : 'Ответить'}
            </Button>
          </div>
        )}
      </div>
    </div>
  );
}
