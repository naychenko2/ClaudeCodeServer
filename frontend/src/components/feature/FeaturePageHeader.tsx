import { useEffect, useState } from 'react';
import type { CSSProperties, FocusEvent, ReactNode } from 'react';
import { ArrowRight, GitBranch, ListChecks, MessageCircle, Pause, Play } from 'lucide-react';
import { C, R, SHADOW, FS, SP, FONT, MODAL_W } from '../../lib/design';
import { stepProgressFromRun, type StepProgress } from '../../lib/featureProgress';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { ToolbarOverflowMenu, type OverflowItem } from '../ToolbarOverflowMenu';
import { Button } from '../ui/Button';
import { Modal } from '../ui/Modal';
import { FeatureGlyph } from '../chat/feature/FeatureGlyphs';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { formatDuration, MERGE_PENDING_STATUS, type FeatureStatusTag } from '../../lib/chatGroups';
import { ensurePersonasLoaded, getPersonaById, usePersonasVersion } from '../../lib/personas';
import { openChatById } from '../../lib/openChat';
import { openTaskInSection } from '../../lib/tasks';
import { recordFeatureReturn } from '../../lib/featureBackNav';
import { buildNavMarks, type NavMarks } from '../../lib/featureNavMarks';
import { showToast } from '../../lib/toast';
import type { FeatureRun, FeatureState, FeatureAttention, FeatureNow } from '../../types/featureRun';
import { api } from '../../lib/api';
import type { FeatureIndexFeature } from '../../types/featureIndex';
import { taskCounters } from './featurePageBody';
import { NavTargetButton } from './NavTargetButton';

// === Тег статуса (макет §3) ===
//
// Единая точка правды — run.state (11 значений, бэк Б1 + W4-HOLD-B1): из него бэк
// выводит bucket строки индекса и порядок; тег с ними не расходится.
// Таблица макета — 8 подписей: `idle` («ничего не идёт, не ждёт, не закрыта»)
// показывается как `not_started` — для пользователя фича ещё не началась.
// `stageName` — имя активной стадии (тег «идёт · {этап}»); null — просто «идёт».
// Тон `loop` («Доработка») run.state не возвращает — только фолбэк по индексу.
// `paused`/`draft` (W4-HOLD-F2, feature-hold-1.md §«Теги»): при паузе тон активной
// стадии на бэке остаётся «run» — тег обязан показать «на паузе», а не «идёт»
// (пункт вердикта ревью); тон hold — bgInset + кромка border, НЕ тревожный.
export type FeatureRunTagTone = 'notStarted' | 'run' | 'wait' | 'stop' | 'merge' | 'loop' | 'merged' | 'closed' | 'hold' | 'draft';
export interface FeatureRunTag {
  label: string;
  tone: FeatureRunTagTone;
  // Точка 6 (C.info) у «ждёт вас» / «ждёт согласия на мерж».
  dot?: boolean;
}

export function featureRunStatusTag(state: FeatureState, stageName?: string | null): FeatureRunTag {
  switch (state) {
    case 'not_started':
    case 'idle':
      return { label: 'не начата', tone: 'notStarted' };
    case 'running':
      return { label: stageName ? `идёт · ${stageName}` : 'идёт', tone: 'run' };
    case 'waiting':
      return { label: 'ждёт вас', tone: 'wait', dot: true };
    case 'stopped':
      return { label: 'стоит', tone: 'stop' };
    case 'paused':
      // Тон активной стадии при паузе на бэке остаётся «run» — stageName сюда не
      // подмешиваем: при паузе нет «идёт», есть «на паузе» (вердикт ревью W4-HOLD-F2).
      return { label: 'на паузе', tone: 'hold' };
    case 'draft':
      return { label: 'черновик', tone: 'draft' };
    case 'merge_pending':
      return { label: MERGE_PENDING_STATUS, tone: 'merge', dot: true };
    case 'merging':
      return { label: 'мерж · идёт', tone: 'run' };
    case 'merged':
      return { label: 'смержена', tone: 'merged' };
    case 'closed':
      return { label: 'закрыта без мержа', tone: 'closed' };
  }
}

// Старый тег индекса (featureStatusTag, 7 тонов) → тег страницы: тоны совпадают
// один в один (loop — только фолбэк по индексу, run.state его не возвращает).
export function toRunTag(t: FeatureStatusTag): FeatureRunTag {
  return { label: t.label, tone: t.tone as FeatureRunTagTone };
}

// Тон → вид (макет §3): не начата/закрыта — bgSelected/textSecondary; идёт —
// bgSelected/textHeading; ждёт/мерж — infoBg/textPrimary (+точка); стоит —
// dangerBg/dangerText + inset-кромка; доработка — warningBg/warningText;
// смержена — successBg/successText. «На паузе» — bgInset + inset-кромка border
// (плашка «утоплена», не тревожная); «черновик» — прозрачный фон, пунктир
// textMuted (не C.dashed: в тёмной теме сливается с фоном, замечание M1).
const TAG_STYLE: Record<FeatureRunTagTone, CSSProperties> = {
  notStarted: { background: C.bgSelected, color: C.textSecondary },
  run:      { background: C.bgSelected, color: C.textHeading },
  wait:     { background: C.infoBg, color: C.textPrimary },
  stop:     { background: C.dangerBg, color: C.dangerText, boxShadow: `inset 0 0 0 1px ${C.danger}` },
  merge:    { background: C.infoBg, color: C.textPrimary },
  loop:     { background: C.warningBg, color: C.warningText },
  merged:   { background: C.successBg, color: C.successText },
  closed:   { background: C.bgSelected, color: C.textSecondary },
  hold:     { background: C.bgInset, color: C.textPrimary, boxShadow: `inset 0 0 0 1px ${C.border}` },
  draft:    { background: 'transparent', color: C.textSecondary, border: `1px dashed ${C.textMuted}` },
};

// Тег: 11/600, поля 1 7, R.sm, строка 16 (макет §1). Точка — у «ждёт вас»/«к мержу».
// «На паузе» — глиф «‖» 10 px currentColor перед подписью (макет feature-hold-1);
// «черновик» — поля 0 6: пунктирная рамка занимает место, её компенсируют полем.
export function StatusTag({ tag, testId }: { tag: FeatureRunTag; testId?: string }) {
  return (
    <span
      data-testid={testId}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 0,
        fontSize: FS.xs, fontWeight: 600, fontFamily: FONT.sans, lineHeight: '16px',
        ...(tag.tone === 'draft' ? { padding: `0 6px` } : { padding: `${SP.xxs}px ${SP.xs}px` }),
        borderRadius: R.sm, whiteSpace: 'nowrap',
        ...TAG_STYLE[tag.tone],
      }}
    >
      {tag.tone === 'hold' && (
        <span aria-hidden style={{ display: 'inline-flex', color: 'currentColor' }}>
          <Pause size={10} strokeWidth={ICON_STROKE} />
        </span>
      )}
      {tag.dot && (
        <span aria-hidden style={{ width: 6, height: 6, borderRadius: R.full, background: C.info, flexShrink: 0 }} />
      )}
      {tag.label}
    </span>
  );
}

// === Главное действие (макет §1/§2/§11) ===
//
// wait → «Ответить {имя}» (в чат), stopped → «Чат {имя}» (в чат), merge → «Мерж…»
// (действие Ф6: кнопка открывает общую модалку подтверждения, POST /merge).
// Имени нет (персона не резолвится) — нейтральная подпись. Тревоги нет → null.
export interface FeaturePrimaryAction {
  kind: 'chat' | 'merge';
  label: string;
  // Чат, который открывает действие; null — чата нет.
  chatId: string | null;
}

export function featurePrimaryAction(a: FeatureAttention | null | undefined, personaName?: string | null): FeaturePrimaryAction | null {
  if (!a) return null;
  if (a.kind === 'merge') return { kind: 'merge', label: 'Мерж…', chatId: a.linkedSessionId ?? null };
  const name = personaName ?? null;
  if (a.kind === 'stopped') {
    return { kind: 'chat', label: name ? `Чат ${name}` : 'Открыть чат', chatId: a.linkedSessionId ?? null };
  }
  return { kind: 'chat', label: name ? `Ответить ${name}` : 'Ответить', chatId: a.linkedSessionId ?? null };
}

// Имя персоны тревоги (null — не резолвится).
function attentionPersonaName(a: FeatureAttention | null | undefined): string | null {
  if (!a?.personaId) return null;
  return getPersonaById(a.personaId)?.name ?? null;
}

// === Мета-строка под шапкой (макет §1) ===
//
// Тег (только 360: на 1280 он в шапке) · ветка (клик копирует, тост) · возраст ·
// число задач. Поля 4 12, перенос; 12 textSecondary; ветка — mono 11 + GitBranch 12,
// наведение bgSelected, на 360 minHeight 40.
export interface FeaturePageHeaderProps {
  // null — данных страницы ещё нет (loading): строится по индексу.
  run: FeatureRun | null;
  // Строка индекса (ветка, createdAt, taskCount, ключевой чат); null — фичи нет в индексе.
  feature: FeatureIndexFeature | null;
  isMobile: boolean;
  // Тег страницы (считает FeaturePage: run.state, фолбэк — индекс).
  tag: FeatureRunTag | null;
}

// Сегмент честного прогресса (Ф-pr): число (mono, bold) + подпись. `always` — показывать
// даже при 0 («общий»/«принято» — всегда видны).
function Seg({ label, value, always = false }: { label: string; value: number; always?: boolean }) {
  if (value === 0 && !always) return null;
  return (
    <span
      data-testid={`feature-page-seg-${label}`}
      style={{
        display: 'inline-flex', alignItems: 'baseline', gap: 4,
        fontSize: FS.xs, color: C.textSecondary,
        background: C.bgSelected, borderRadius: R.sm, padding: `${SP.xxs}px ${SP.xs}px`,
        whiteSpace: 'nowrap',
      }}
    >
      <b style={{ color: C.textHeading, fontWeight: 700, fontFamily: FONT.mono }}>{value}</b>
      <span style={{ fontFamily: FONT.sans }}>{label}</span>
    </span>
  );
}

// Честный прогресс в шапке (Ф-pr): 4 основных сегмента (общий/принято/на ревью/в работе)
// + 2 вторичных (не заведено/снято).
// Сегмент с 0 скрыт, кроме «общего» и «принято» (всегда).
function StepProgressSegments({ p }: { p: StepProgress }) {
  const hasSecondary = p.notCreated > 0 || p.withdrawn > 0;
  return (
    <div
      data-testid="feature-page-steps"
      style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs, minWidth: 0 }}
    >
      <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap' }}>
        <Seg label="общий" value={p.total} always />
        <Seg label="принято" value={p.accepted} always />
        <Seg label="на ревью" value={p.review} />
        <Seg label="в работе" value={p.work} />
      </div>
      {hasSecondary && (
        <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap' }}>
          <Seg label="не заведено" value={p.notCreated} />
          <Seg label="снято" value={p.withdrawn} />
        </div>
      )}
    </div>
  );
}

// === Чип «ведёт ·» (макет pt1, W4-1F) ===
//
// Оркестратор фичи (поле индекса orchestratorPersonaId, W4-1B) — кому писать. Чип
// сам и есть вход в ключевой чат: отдельной ссылки «Ключевой чат» нет (пункт ⋯
// «Открыть ключевой чат» убран — переход дублировался).
// Состояния (макет pt1): чат есть — кнопка, клик открывает keySessionId с плашкой
// возврата к фиче (как у «чат · задача»); keyArchived — пометка «в архиве»;
// чата нет — статичный чип (пунктирная рамка, без стрелки); персоны нет — элемента нет.
//
// Контрол — ui.Button (железное правило дизайн-системы): база — ghostFilled + pill;
// состояния наведения/нажатия/фокуса — React-состояние этой компоненты, стиль считается
// от него. ui.Button не форвардит mouse/focus-обработчики, поэтому они живут на
// обёртке-спане (прецедент: чип плана в TaskCreatedView). Фокус по pt1 — кольцо
// SHADOW.focus + accent-рамка, не только кольцо.
function LeadChip({ feature, isMobile }: { feature: FeatureIndexFeature; isMobile: boolean }) {
  const personaId = feature.orchestratorPersonaId ?? null;
  // getPersonaById сам по себе не реактивен (lib/personas.ts:75): подписка на версию
  // стора + запрос загрузки. Состояние одно: персона не резолвится (нет id в индексе
  // или стор ещё не догружен) — элемента нет. Чип НЕ различает «не загрузилась» и
  // «нет оркестратора» — оба случая пустые; подписка лишь гарантирует, что чип
  // появится после догрузки (прецедент: NotificationAvatar, TaskPersonaBadge).
  usePersonasVersion();
  useEffect(() => { void ensurePersonasLoaded(); }, []);
  // Состояния (макет pt1): наведение — bgSelected + кромка accentMuted; нажатие —
  // accentLight; фокус — кольцо + accent-рамка, только с клавиатуры (:focus-visible),
  // иначе рамка залипает после тапа. Значок-стрелка красится только по наведению.
  // Состояние — локальное React-состояние (до ранних return: порядок хуков не должен
  // плыть), стиль ui.Button считается от него; обработчики — на обёртке, потому что
  // ui.Button их не форвардит.
  const [hover, setHover] = useState(false);
  const [pressed, setPressed] = useState(false);
  const [focused, setFocused] = useState(false);
  const persona = personaId ? getPersonaById(personaId) : null;
  // Персона не задана или не резолвится — элемента нет (состояние «Оркестратора нет»).
  if (!persona) return null;
  const keyChat = feature.keySessionId ?? null;
  const name = persona.name;

  // Каркас чипа (макет pt1): pill 24 px (на 360 — 40), поля 8/3 (десктоп) и 12/8
  // (мобила), gap 6, 12 sans — числа по макету .who, вне 4px-шкалы. Ширину не
  // фиксируем: если чип длиннее строки, он сжимается, а имя реется (ellipsis) —
  // длинное имя не лезет за 360. fontWeight и выравнивание перебивают дефолты
  // ui.Button: чип — подпись, не кнопка с центровкой.
  const shell: CSSProperties = {
    display: 'inline-flex', alignItems: 'center', gap: 6,
    height: isMobile ? 40 : 24,
    padding: isMobile ? `0 ${SP.md}px 0 ${SP.sm}px` : `0 ${SP.sm}px 0 3px`,
    borderRadius: R.max, fontSize: FS.sm, color: C.textPrimary, fontFamily: FONT.sans,
    fontWeight: 400, justifyContent: 'flex-start',
  };

  if (!keyChat) {
    // Чата нет — чип не кнопка: пунктирная рамка, без значка-стрелки.
    return (
      <span
        data-testid="feature-page-lead"
        title="Ключевого чата у фичи нет"
        style={{ ...shell, border: `1px dashed ${C.dashed}`, background: 'transparent' }}
      >
        <PersonaAvatar persona={persona} size={18} />
        <span style={{ color: C.textMuted, flexShrink: 0 }}>ведёт</span>
        <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{name}</span>
      </span>
    );
  }

  return (
    // Обёртка — flex-контейнер без фиксации ширины: если чип длиннее строки, он
    // сжимается, и имя уходит в ellipsis (прецедент: FeatureProdCardsView).
    <span
      style={{ display: 'inline-flex', minWidth: 0 }}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => { setHover(false); setPressed(false); }}
      // Нажатие — по pointer-событиям: тач и мышь дают их одинаково, на тапе
      // состояние видно сразу (прецедент: FeatureProdCardsView, ResizeHandle).
      onPointerDown={() => setPressed(true)}
      onPointerUp={() => setPressed(false)}
      // Сход пальца с чипа: браузер посылает pointercancel — без него pressed залипает.
      onPointerCancel={() => setPressed(false)}
      // Фокус — только с клавиатуры (:focus-visible): после клика/тапа рамка и кольцо
      // не залипают (прецедент: FeatureProdCardsView:113).
      onFocus={(e: FocusEvent<HTMLSpanElement>) => setFocused(e.currentTarget.matches(':focus-visible'))}
      onBlur={() => setFocused(false)}
    >
      <Button
        variant="ghostFilled"
        pill
        size="xs"
        dataTestId="feature-page-lead"
        ariaLabel={`Открыть ключевой чат${feature.keyArchived ? ' (в архиве)' : ''} · ведёт ${name}`}
        title={`Ключевой чат фичи${feature.keyArchived ? ' · в архиве' : ''}\nклик — открыть чат`}
        onClick={() => {
          recordFeatureReturn(feature.featureId, { kind: 'chat', id: keyChat }, feature.title ?? null);
          void openChatById(keyChat);
        }}
        // Рамка — longhand (borderColor): база 1px solid из ghostFilled остаётся,
        // цвет меняется состоянием; shorthand затёр бы саму толщину рамки.
        style={{
          ...shell,
          background: pressed ? C.accentLight : hover ? C.bgSelected : C.bgWhite,
          borderColor: focused ? C.accent : hover ? C.accentMuted : C.border,
          boxShadow: focused ? SHADOW.focus : 'none',
          outline: 'none',
        }}
      >
        <PersonaAvatar persona={persona} size={18} />
        <span style={{ color: C.textMuted, flexShrink: 0 }}>ведёт</span>
        <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{name}</span>
        {feature.keyArchived && (
          <span style={{ color: C.textMuted, fontSize: FS.xs, flexShrink: 0 }}>в архиве</span>
        )}
        <span aria-hidden data-testid="feature-page-lead-icon" style={{ display: 'inline-flex', flexShrink: 0, color: hover ? C.accent : C.textMuted }}>
          <MessageCircle size={12} strokeWidth={ICON_STROKE} />
          <ArrowRight size={12} strokeWidth={ICON_STROKE} />
        </span>
      </Button>
    </span>
  );
}

export function FeaturePageHeader({ run, feature, isMobile, tag }: FeaturePageHeaderProps) {
  const branch = feature?.branch ?? null;

  // Копировать ветку: clipboard + тост (макет: «клик копирует, тост»).
  const [copied, setCopied] = useState(false);
  const copyBranch = () => {
    if (!branch) return;
    if (typeof navigator !== 'undefined' && navigator.clipboard) {
      void navigator.clipboard.writeText(branch).catch(() => {});
    }
    showToast('Ветка скопирована', branch);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  // Возраст: смержена/закрыта — «шла {итог}» (summary.durationMinutes, бэк считает);
  // не начата — «создана {N} назад»; иначе — «идёт {от создания до сейчас}».
  const createdAt = feature?.createdAt ?? run?.epic?.createdAt ?? null;
  let age: string | null = null;
  if (createdAt) {
    const created = new Date(createdAt).getTime();
    if (Number.isFinite(created)) {
      if (run?.state === 'merged' || run?.state === 'closed') {
        const d = run?.summary?.durationMinutes;
        age = d != null ? `шла ${formatDuration(d)}` : null;
      } else if (run?.state === 'paused' || run?.state === 'draft') {
        // Пауза/черновик: «идёт» — неправда, работа стоит по решению человека
        // (feature-hold-1.md): возраст — от Hold.Since, не от создания.
        const since = run?.hold?.since ? new Date(run.hold.since).getTime() : NaN;
        age = Number.isFinite(since)
          ? `${run.state === 'paused' ? 'на паузе' : 'черновик'} ${formatDuration(Math.max(1, Math.round((Date.now() - since) / 60000)))}`
          : null;
      } else if (run?.state === 'not_started' || run?.state === 'idle') {
        age = `создана ${formatDuration(Math.max(1, Math.round((Date.now() - created) / 60000)))} назад`;
      } else {
        age = `идёт ${formatDuration(Math.max(1, Math.round((Date.now() - created) / 60000)))}`;
      }
    }
  }

  // «N задач» — ОДИН источник со строками вкладки «Задачи»: taskCounters(run).total
  // (честное число из run, не taskCount из индекса — индекс может расходиться с run).
  // run=null (loading) — прочерк: число из индекса (taskCount) показало бы значение,
  // которое изменится, когда run придёт.
  const total = run ? taskCounters(run).total : null;
  const tasks = total != null
    ? (total > 0 ? `${total} ${pluralTasks(total)}` : 'задач нет')
    : '—';
  // Честный прогресс (Ф-pr): из run.stepProgress; null — run нет (loading) или старые данные.
  const progress = run ? stepProgressFromRun(run) : null;

  const branchBtn = branch && (
    <button
      type="button"
      data-testid="feature-page-branch"
      onClick={copyBranch}
      title={copied ? 'Ветка скопирована' : 'Скопировать ветку'}
      aria-label={`Скопировать ветку ${branch}`}
      onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
      onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: SP.xs, flex: '0 1 auto', minWidth: 0,
        height: isMobile ? 40 : 18, padding: `0 ${SP.xs}px`, margin: isMobile ? 0 : `-${SP.xxs}px -${SP.xs}px`,
        border: 'none', borderRadius: R.sm, background: 'transparent', cursor: 'pointer',
        fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary,
      }}
    >
      <GitBranch size={12} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{branch}</span>
    </button>
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      <div
        data-testid="feature-page-meta"
        style={{
          display: 'flex', flexWrap: 'wrap', alignItems: 'center',
          gap: `${SP.xs}px ${SP.md}px`, fontSize: FS.sm, color: C.textSecondary, fontFamily: FONT.sans,
        }}
      >
        {isMobile && tag && <StatusTag tag={tag} testId="feature-page-status" />}
        {branchBtn}
        {age && <span>{age}</span>}
        <span data-testid="feature-page-taskcount">{tasks}</span>
        {feature && <LeadChip feature={feature} isMobile={isMobile} />}
      </div>
      {progress && <StepProgressSegments p={progress} />}
    </div>
  );
}

// Склонение «задача/задачи/задач» (экспортируется для теста).
export function pluralTasks(n: number): string {
  const d = n % 10, h = n % 100;
  if (d === 1 && h !== 11) return 'задача';
  if (d >= 2 && d <= 4 && (h < 10 || h >= 20)) return 'задачи';
  return 'задач';
}

// === Пауза и черновик (W4-HOLD-F2, макет feature-hold-1) ===
//
// Ручки бэка: POST/DELETE /api/tasks/features/{id}/hold (W4-HOLD-B2). Кнопка «Пауза»
// разрешена при состояниях работы; «где кнопки нет»: merging, merged, closed, draft,
// paused. Пункт «Вернуть в черновик» — только когда бэк отдаёт признак «шагов не
// запускалось»; до него пункта нет. Страница обновляется событием агрегата (стор
// featurePage слушает taskChanged) — оптимистично тег не меняем.
const HOLD_ALLOWED: ReadonlySet<FeatureState> = new Set(['running', 'waiting', 'stopped', 'merge_pending', 'not_started', 'idle']);

// Текст тоста 409: причина из ответа бэка (body.error); пустая — фолбэк из спецификации.
function holdErrorText(e: unknown, state: FeatureState): string {
  const err = e as Error & { status?: number; body?: unknown };
  if (err.status === 409) {
    const body = err.body as { error?: string } | undefined;
    if (body?.error) return body.error;
    if (state === 'merging') return 'Мерж уже идёт — его не останавливают посреди';
    if (state === 'draft') return 'У фичи уже запускались шаги — в черновик не вернуть';
    return 'Состояние фичи изменилось — удержание не поставилось';
  }
  return `Не получилось: ${err.message || 'сервер не ответил'}`;
}

// Склонение «шаг/шага/шагов».
function pluralSteps(n: number): string {
  const d = n % 10, h = n % 100;
  if (d === 1 && h !== 11) return 'шаг';
  if (d >= 2 && d <= 4 && (h < 10 || h >= 20)) return 'шага';
  return 'шагов';
}

// Подсказка пункта ⋯ (макет): «Прервёт N идущих шагов»; для одного — «1 идущий шаг».
function runningHint(n: number): string {
  return n === 1 ? 'Прервёт 1 идущий шаг' : `Прервёт ${n} идущих ${pluralSteps(n)}`;
}

// Строка «{Персона} · {stepRef}» из run.now — единственный доступный источник
// идущих/прерванных шагов: отдельный список прерванных бэк ещё не отдаёт (макет §«Чего нет в API»).
function nowLine(n: FeatureNow): string {
  const name = n.personaId ? getPersonaById(n.personaId)?.name ?? null : null;
  if (name && n.stepRef) return `${name} · ${n.stepRef}`;
  return name ?? n.stepRef ?? n.title;
}

// Постановка паузы (reason manual — пауза человека; «черновик» фронтом не ставится).
async function doHoldPause(fid: string, state: FeatureState, nowCount: number): Promise<void> {
  try {
    await api.tasks.setFeatureHold(fid, 'manual');
    showToast('Фича на паузе', nowCount > 0 ? `Прервано ${nowCount} ${pluralSteps(nowCount)}. Снять паузу — на странице фичи.` : 'Снять паузу — на странице фичи.');
  } catch (e) {
    showToast('Не получилось', holdErrorText(e, state));
  }
}

// Снятие удержания: «Снять паузу» (paused) и «Запустить фичу» (draft) — обе ручки
// DELETE hold, без подтверждения (макет: «Снять паузу» и «Запустить фичу» — без модалки).
// Ответ — {resumed, expired} (вердикт R-HOLD-H): resumed — задачи, которым пауза
// мешала запуститься и теперь разрешены (не очередь автостарта), их и называем, без
// числа (R-HOLD-H2); expired не показываем. Метод хранилища ClearHold сам ничего не
// возобновляет (ClearHold_DoesNotAutoResumeAnything), но ручка DELETE сразу после него
// зовёт ResumeFeatureTurnsAfterHold (TasksController.cs:941) — прерванные ходы
// продолжаются; текст тоста остаётся честным для гейт-множества.
async function doHoldClear(fid: string, state: FeatureState): Promise<void> {
  try {
    // Тело ответа может быть пустым (Ok() без контента — request отдаёт undefined,
    // offline.ts:447): деструктуризация undefined бросила бы TypeError.
    const { resumed } = (await api.tasks.clearFeatureHold(fid)) ?? {};
    const resumedIds = resumed ?? []; // страховка: ответ без тела
    if (state === 'draft') {
      // Бэк не обещает немедного старта конвейера — честный текст до решения
      // по дефекту bec00347 (карточка «фича запущена»).
      showToast('Черновик снят', 'Шаги запустит куратор.');
    } else if (resumedIds.length > 0) {
      showToast('Фича снята с паузы', 'Задачи, которым пауза мешала, снова разрешены.');
    } else {
      showToast('Пауза снята', '');
    }
  } catch (e) {
    showToast('Не получилось', holdErrorText(e, state));
  }
}

// Модалка подтверждения паузы (макет §«Подтверждение паузы»): MODAL_W.confirm,
// кнопки ghost «Отмена» + primary «Поставить на паузу» (не danger: действие обратимо).
// Фокус при открытии — на «Отмена»; Esc закрывает (штатно у Modal).
function HoldConfirmModal({ now, busy, onClose, onConfirm }: { now: FeatureNow[]; busy: boolean; onClose: () => void; onConfirm: () => void }) {
  useEffect(() => {
    const el = typeof document !== 'undefined' ? document.querySelector('[data-testid="feature-page-hold-cancel"]') : null;
    (el as { focus?: () => void } | null)?.focus?.();
  }, []);
  return (
    <Modal
      width={MODAL_W.confirm}
      title="Поставить фичу на паузу?"
      onClose={onClose}
      footer={
        <span style={{ display: 'flex', gap: SP.xs }}>
          <Button size="sm" variant="ghost" dataTestId="feature-page-hold-cancel" onClick={onClose}>Отмена</Button>
          <Button size="sm" variant="primary" loading={busy} dataTestId="feature-page-hold-confirm" onClick={onConfirm}>
            {busy ? 'Ставлю паузу…' : 'Поставить на паузу'}
          </Button>
        </span>
      }
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        <div style={{ fontSize: FS.base, color: C.textPrimary }}>
          Идущие шаги прервутся сразу: <b>{now.map(nowLine).join(' и ')}</b>
        </div>
        <div style={{ fontSize: FS.sm, color: C.textSecondary }}>
          Файлы в worktree останутся как есть. Новые шаги не запустятся, пока вы не снимете паузу, — тогда прерванные продолжатся с того же места.
        </div>
      </div>
    </Modal>
  );
}

// === Шапка (Toolbar): тег (1280) · «Пауза» (1280) · главное действие (1280) · меню ⋯ ===
//
// На 360 в шапке только ⋯ (40): тег уходит в мету, главное действие — в блок
// тревоги (макет §11). ⋯ — общий ToolbarOverflowMenu (десктоп — дропдаун,
// мобила — шторка, пункты 44): «Скопировать ветку», «Открыть эпик в «Задачах»»,
// «Открыть ключевой чат».
export interface FeaturePageToolbarProps {
  // null — данных страницы ещё нет (loading): строится по индексу.
  run: FeatureRun | null;
  feature: FeatureIndexFeature | null;
  isMobile: boolean;
  tag: FeatureRunTag | null;
  // «Мерж…»: открыть общую модалку подтверждения (Ф6); undefined — без действия.
  onMerge?: () => void;
}

export function FeaturePageToolbar({ run, feature, isMobile, tag, onMerge }: FeaturePageToolbarProps) {
  const branch = feature?.branch ?? null;
  const keyChat = feature?.keySessionId ?? null;
  const epic = run?.epic ?? null;

  // Копировать ветку (тот же тост, что в мете).
  const copyBranch = () => {
    if (!branch) return;
    if (typeof navigator !== 'undefined' && navigator.clipboard) {
      void navigator.clipboard.writeText(branch).catch(() => {});
    }
    showToast('Ветка скопирована', branch);
  };

  // Пауза (W4-HOLD-F2): «Пауза» — при состояниях работы; при paused главное действие
  // — «Снять паузу»; при draft — «Запустить фичу» (та же DELETE-ручка). На 360
  // «Пауза» — первый пункт шторки ⋯, а не кнопка в ряду (макет A).
  const state = run?.state;
  const fid = run?.featureId ?? feature?.featureId ?? '';
  const now = run?.now ?? [];
  const canPause = state != null && HOLD_ALLOWED.has(state);
  const paused = state === 'paused';
  const draft = state === 'draft';
  const [holdBusy, setHoldBusy] = useState(false);
  const [holdModal, setHoldModal] = useState(false);

  const runPause = () => {
    setHoldBusy(true);
    void doHoldPause(fid, state!, now.length).finally(() => {
      setHoldBusy(false);
      setHoldModal(false);
    });
  };
  const pauseClick = () => {
    if (holdBusy) return;
    // Идущие ходы — сначала подтверждение (макет §«Подтверждение паузы»); их нет —
    // сразу запрос и тост.
    if (now.length > 0) { setHoldModal(true); return; }
    runPause();
  };
  const clearClick = () => {
    if (holdBusy) return;
    setHoldBusy(true);
    void doHoldClear(fid, state!).finally(() => setHoldBusy(false));
  };

  // Главное действие: wait/stopped → в чат исполнителя; merge → «Мерж…» (Ф6: общая
  // модалка, POST /merge); не начата → «Открыть ключевой чат» (ghost, макет §1).
  // При паузе/черновике главное действие — снятие удержания: тревогу бэк гасит, но
  // старыми данными UI обязан пережить и то, и другое — hold важнее.
  const attention = run?.attention ?? null;
  const primary = featurePrimaryAction(attention, attentionPersonaName(attention));
  const notStarted = run?.state === 'not_started';
  // «Мерж…» — только когда state === 'merge_pending'; никогда при merging/merged/closed.
  const mergePending = run?.state === 'merge_pending';
  const effectivePrimary = (primary?.kind === 'merge' && !mergePending) ? null : primary;
  const holdAction: 'unpause' | 'start' | null = paused ? 'unpause' : draft ? 'start' : null;
  const showPrimary = !isMobile && (holdAction != null || effectivePrimary != null || (notStarted && keyChat != null));
  const onPrimary = () => {
    if (holdAction) { clearClick(); return; }
    if (effectivePrimary?.kind === 'chat' && effectivePrimary.chatId) {
      void openChatById(effectivePrimary.chatId);
      return;
    }
    if (effectivePrimary?.kind === 'merge') {
      onMerge?.();
      return;
    }
    if (notStarted && keyChat) void openChatById(keyChat);
  };

  // Пункты ⋯: только существующие (ветки/эпика/ключевого чата нет — пункта нет).
  const items: OverflowItem[] = [];
  if (isMobile && canPause) {
    // «Поставить на паузу…» — первый пункт шторки (макет A); подсказка про идущие
    // шаги — только если что-то идёт (N = 0 — строки нет).
    items.push({
      key: 'hold',
      icon: <Pause size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
      label: 'Поставить на паузу…',
      sublabel: now.length > 0 ? runningHint(now.length) : undefined,
      onClick: pauseClick,
    });
  }
  if (branch) {
    items.push({
      key: 'branch',
      icon: <GitBranch size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
      label: 'Скопировать ветку',
      onClick: copyBranch,
    });
  }
  if (epic) {
    items.push({
      key: 'epic',
      icon: <ListChecks size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
      label: 'Открыть эпик в «Задачах»',
      onClick: () => openTaskInSection({ id: epic.id, projectId: epic.projectId ?? undefined }),
    });
  }
  // Пункта «Открыть ключевой чат» здесь нет (W4-1F): вход в ключевой чат — чип
  // «ведёт ·» в мета-строке, отдельная ссылка не дублируется.

  return (
    <>
      {!isMobile && tag && <StatusTag tag={tag} testId="feature-page-status" />}
      {canPause && !isMobile && (
        <span data-testid="feature-page-pause-wrap" style={{ display: 'inline-flex', flexShrink: 0 }}>
          <Button
            size="sm"
            variant="secondary"
            leftIcon={<Pause size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
            loading={holdBusy}
            dataTestId="feature-page-pause"
            title="Остановить идущие шаги фичи"
            onClick={pauseClick}
          >
            {holdBusy ? 'Ставлю паузу…' : 'Пауза'}
          </Button>
        </span>
      )}
      {showPrimary && (
        <span data-testid="feature-page-main-action" style={{ display: 'inline-flex', flexShrink: 0 }}>
          <Button
            size="sm"
            variant={holdAction ? 'primary' : effectivePrimary ? 'primary' : 'ghost'}
            leftIcon={holdAction ? <Play size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> : undefined}
            loading={holdBusy}
            dataTestId={holdAction ? 'feature-page-unpause' : undefined}
            onClick={onPrimary}
          >
            {holdAction === 'unpause' ? (holdBusy ? 'Снимаю паузу…' : 'Снять паузу')
              : holdAction === 'start' ? (holdBusy ? 'Запускаю…' : 'Запустить фичу')
              : effectivePrimary ? effectivePrimary.label : 'Открыть ключевой чат'}
          </Button>
        </span>
      )}
      {items.length > 0 && (
        <ToolbarOverflowMenu
          isMobile={isMobile}
          title="Ещё"
          items={items}
        />
      )}
      {holdModal && (
        <HoldConfirmModal now={now} busy={holdBusy} onClose={() => setHoldModal(false)} onConfirm={runPause} />
      )}
    </>
  );
}

// === Блок тревоги / «ждёт вас» (макет §2) ===
//
// Рисуется, когда run.attention есть (wait/stopped/merge) или фича не начата
// (нейтральный блок «Фича ещё не начата»). Тон: wait/merge — infoBg, stopped —
// dangerBg, не начата — bgSelected + рамка. Каркас: R.lg, поля 10 12, gap 9, 13,
// глиф 14 сверху, отступ сверху 10. Действия: главное (на 360; на 1280 оно в
// шапке) + пара «чат · задача» (NavTargetButton; у wait/stopped — только «задача»,
// чат — и есть главное действие).
export interface FeaturePageAlertProps {
  // null — данных страницы ещё нет (loading): тревоги нет.
  run: FeatureRun | null;
  feature: FeatureIndexFeature | null;
  isMobile: boolean;
  // «Мерж…»: открыть общую модалку подтверждения (Ф6); undefined — без действия.
  onMerge?: () => void;
  // Пометки «в архиве»/«задача закрыта» (страница даёт полные, с чатами); без пропа —
  // из самого агрегата.
  marks?: NavMarks;
}

// Глифы «hold» и «draft» (макет feature-hold-1, §«Глифы»): пауза — круг с двумя
// брусками, черновик — пунктирное кольцо. Обводка textMuted: в тёмной теме C.dashed
// сливается с фоном (та же причина, что у глифа absent, замечание M1).
function HoldGlyph({ kind, size }: { kind: 'hold' | 'draft'; size: number }) {
  const v = { width: size, height: size, viewBox: '0 0 16 16', 'aria-hidden': true as const };
  if (kind === 'hold') {
    return (
      <svg {...v}>
        <circle cx="8" cy="8" r="7" fill={C.bgInset} stroke={C.textMuted} strokeWidth="1" />
        <rect x="5.3" y="4.6" width="1.9" height="6.8" rx="0.6" fill={C.textSecondary} />
        <rect x="8.8" y="4.6" width="1.9" height="6.8" rx="0.6" fill={C.textSecondary} />
      </svg>
    );
  }
  return (
    <svg {...v}>
      <circle cx="8" cy="8" r="6.2" fill="none" stroke={C.textMuted} strokeWidth="1.5" strokeDasharray="2.4 2" />
    </svg>
  );
}

// Блок состояния «на паузе»/«черновик» (макет §«Блок состояния»): не тревога —
// bgInset + рамка border / прозрачный + пунктир. Список прерванных шагов — из
// run.now (списка прерванных бэк не отдаёт): N = 0 — строки нет. На 360 главное
// действие («Снять паузу»/«Запустить фичу») — здесь, на 1280 — в шапке.
function HoldStateBlock({ run, feature, isMobile }: { run: FeatureRun; feature: FeatureIndexFeature | null; isMobile: boolean }) {
  const fid = run.featureId ?? feature?.featureId ?? '';
  const now = run.now ?? [];
  const paused = run.state === 'paused';
  const since = run.hold?.since ? new Date(run.hold.since).getTime() : NaN;
  const dur = Number.isFinite(since) ? formatDuration(Math.max(0, Math.round((Date.now() - since) / 60000))) : null;
  const [busy, setBusy] = useState(false);
  const act = () => {
    if (busy) return;
    setBusy(true);
    void doHoldClear(fid, run.state).finally(() => setBusy(false));
  };
  const keyChat = feature?.keySessionId ?? run.epic?.linkedSessionId ?? null;

  return (
    <div
      data-testid="feature-page-alarm"
      data-tone={paused ? 'hold' : 'draft'}
      role="status"
      aria-live="polite"
      style={{
        display: 'flex', gap: SP.sm, alignItems: 'flex-start',
        marginTop: SP.md, padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.lg,
        background: paused ? C.bgInset : 'transparent',
        border: paused ? `1px solid ${C.border}` : `1px dashed ${C.textMuted}`,
        fontSize: FS.base, fontFamily: FONT.sans, color: C.textPrimary,
      }}
    >
      <span style={{ paddingTop: SP.xxs, display: 'inline-flex', flexShrink: 0 }}>
        <HoldGlyph kind={paused ? 'hold' : 'draft'} size={14} />
      </span>
      <div style={{ minWidth: 0 }}>
        <div>
          <b>{paused ? `Фича на паузе${dur ? ` ${dur}` : ''}.` : 'Черновик.'}</b>
          <span> {paused ? 'Поставили вы. Новые шаги не запускаются.' : 'Шаги не запускаются — разрез, план и дизайн идут как обычно. Запустите фичу, когда план готов: куратор начнёт конвейер.'}</span>
        </div>
        {paused && now.length > 0 && (
          <div style={{ marginTop: SP.sm, fontSize: FS.sm, color: C.textSecondary }}>
            <div>Прервано {now.length} {pluralSteps(now.length)} — продолжатся с того же места после снятия паузы:</div>
            {now.map(n => <div key={n.taskId}>{nowLine(n)}</div>)}
          </div>
        )}
        {isMobile && (
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, marginTop: SP.sm }}>
            <Button
              size="xs"
              variant="primary"
              style={{ background: C.bgWhite, minHeight: 40 }}
              loading={busy}
              dataTestId="feature-page-unpause"
              onClick={act}
            >
              {paused ? 'Снять паузу' : 'Запустить фичу'}
            </Button>
            {!paused && keyChat && (
              <Button
                size="xs"
                variant="ghost"
                style={{ background: C.bgWhite, minHeight: 40 }}
                onClick={() => { void openChatById(keyChat); }}
              >
                Открыть ключевой чат
              </Button>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

export function FeaturePageAlert({ run, feature, isMobile, onMerge, marks: marksProp }: FeaturePageAlertProps) {
  // Пауза и черновик — блок состояния вместо тревоги: при paused тревога не рисуется
  // (paused стоит выше stopped и waiting в приоритете бэка, W4-HOLD-B1).
  if (run?.state === 'paused' || run?.state === 'draft') {
    return <HoldStateBlock run={run} feature={feature} isMobile={isMobile} />;
  }
  const a = run?.attention ?? null;
  const marks = marksProp ?? buildNavMarks(run);
  const notStarted = run?.state === 'not_started';
  if (!a && !notStarted) return null;

  // Тон блока: stopped — danger, wait/merge — info, не начата — нейтральный.
  const tone: 'info' | 'danger' | 'neutral' =
    a?.kind === 'stopped' ? 'danger' : (a ? 'info' : 'neutral');
  const bg = tone === 'danger' ? C.dangerBg : tone === 'info' ? C.infoBg : C.bgSelected;
  const emphasis = tone === 'danger' ? C.dangerText : C.textHeading;

  // Глиф: wait/merge — ромб, stopped — квадрат, не начата — нейтральный run.
  const glyphKind = a?.kind === 'stopped' ? 'stopped' : a ? 'wait' : 'run';

  // Длительность: с attention.since до сейчас.
  const sinceMin = a?.since
    ? Math.max(0, Math.round((Date.now() - new Date(a.since).getTime()) / 60000))
    : null;
  const dur = sinceMin != null ? formatDuration(sinceMin) : null;
  const name = attentionPersonaName(a);

  // Главная фраза по kind (макет §2).
  let head: string;
  let reason: string | null = a?.reason ?? null;
  if (a?.kind === 'wait') {
    head = name
      ? `${name} ждёт вашего ответа${dur ? ` ${dur}` : ''}`
      : `Ждёт вашего ответа${dur ? ` ${dur}` : ''}`;
  } else if (a?.kind === 'stopped') {
    head = `Стоит${dur ? ` ${dur}` : ''}`;
  } else if (a?.kind === 'merge') {
    head = 'Мерж в master ждёт вашего согласия';
    reason = null;
  } else {
    head = 'Фича ещё не начата';
    reason = 'Запустить её можно из ключевого чата — куратор начнёт с разреза.';
  }

  // Действия: главное (360) + «чат» (только merge) + «задача».
  // «Мерж…» — только когда state === 'merge_pending'; при merging/merged/closed —
  // только пара «чат · задача» (кнопки нет).
  const primary = featurePrimaryAction(a, name);
  const mergePending = run?.state === 'merge_pending';
  const effectivePrimary = (primary?.kind === 'merge' && !mergePending) ? null : primary;
  // Id фичи для точки возврата «страница фичи → чат/задача» (Ф9b).
  const fid = run?.featureId ?? feature?.featureId ?? '';
  // Название фичи (epic.title) — для «← к фиче {название}» в шапке чата (Ф-pl-b).
  const featureTitle = run?.epic?.title ?? null;
  const acts: ReactNode[] = [];
  if (isMobile && effectivePrimary) {
    acts.push(
      <Button
        key="primary"
        size="xs"
        variant="primary"
        style={{ background: C.bgWhite, minHeight: 40 }}
        onClick={() => {
          if (effectivePrimary.kind === 'chat' && effectivePrimary.chatId) {
            recordFeatureReturn(fid, { kind: 'chat', id: effectivePrimary.chatId }, featureTitle);
            void openChatById(effectivePrimary.chatId);
          }
          else if (effectivePrimary.kind === 'merge') onMerge?.();
        }}
      >
        {effectivePrimary.label}
      </Button>,
    );
  }
  if (a?.kind === 'merge' && a.linkedSessionId) {
    acts.push(
      <NavTargetButton
        key="chat"
        kind="chat"
        chatId={a.linkedSessionId}
        chatArchived={marks.chatArchived(a.linkedSessionId)}
        label="чат"
        isMobile={isMobile}
        title="Открыть чат. Страница фичи скроется, вернётся по плашке."
        onOpen={() => {
          recordFeatureReturn(fid, { kind: 'chat', id: a.linkedSessionId! }, featureTitle);
          void openChatById(a.linkedSessionId!);
        }}
      />,
    );
  }
  if (a?.taskId) {
    acts.push(
      <NavTargetButton
        key="task"
        kind="task"
        taskId={a.taskId}
        taskClosed={marks.taskClosed(a.taskId)}
        label="задача"
        isMobile={isMobile}
        title="Открыть задачу. Страница фичи скроется, вернётся по плашке."
        onOpen={() => {
          recordFeatureReturn(fid, { kind: 'task', id: a.taskId! }, featureTitle);
          openTaskInSection({ id: a.taskId!, projectId: run?.epic?.projectId ?? undefined });
        }}
      />,
    );
  }
  // Не начата: «Открыть ключевой чат» (ghost) — на 360 в блоке, на 1280 в шапке.
  if (!a && isMobile) {
    const keyChat = feature?.keySessionId ?? run?.epic?.linkedSessionId ?? null;
    if (keyChat) {
      acts.push(
        <Button
          key="keychat"
          size="xs"
          variant="ghost"
          style={{ background: C.bgWhite, minHeight: 40 }}
          onClick={() => { void openChatById(keyChat); }}
        >
          Открыть ключевой чат
        </Button>,
      );
    }
  }

  return (
    <div
      data-testid="feature-page-alarm"
      data-tone={tone}
      role="status"
      aria-live="polite"
      style={{
        display: 'flex', gap: SP.sm, alignItems: 'flex-start',
        marginTop: SP.md, padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.lg,
        background: bg, fontSize: FS.base, fontFamily: FONT.sans, color: C.textPrimary,
        ...(tone === 'neutral' ? { border: `1px solid ${C.borderLight}` } : {}),
      }}
    >
      <span style={{ paddingTop: SP.xxs, display: 'inline-flex', flexShrink: 0 }}>
        <FeatureGlyph kind={glyphKind} size={14} />
      </span>
      <div style={{ minWidth: 0 }}>
        <div>
          <b style={{ color: emphasis }}>{head}</b>
          {reason && <span> {reason}</span>}
        </div>
        {acts.length > 0 && (
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, marginTop: SP.sm }}>{acts}</div>
        )}
      </div>
    </div>
  );
}
