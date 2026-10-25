import { useEffect, useRef, useState } from 'react';
import { ArrowRight, ChevronRight, GitBranch } from 'lucide-react';
import { C, FONT, R, FS, SP, SHADOW, TB } from '../../../lib/design';
import { stepProgressFromRun, formatStepProgress } from '../../../lib/featureProgress';
import { useIsMobile } from '../../../lib/breakpoints';
import { useCanHover } from '../../../lib/pointer';
import { Button, Tooltip } from '../../ui';
import { openTaskInSection } from '../../../lib/tasks';
import { openChatById } from '../../../lib/openChat';
import { recordFeatureReturn } from '../../../lib/featureBackNav';
import { api } from '../../../lib/api';
import { getPersonaById, usePersonasVersion, ensurePersonasLoaded } from '../../../lib/personas';
import { showToast } from '../../../lib/toast';
import { roleLabel } from '../../../lib/roleLabel';
import { STAGE_NAME, formatDuration } from '../../../lib/chatGroups';
import { PersonaAvatar } from '../../../features/personas/PersonaAvatar';
import { featureRunStatusTag, StatusTag, featurePrimaryAction, type FeaturePrimaryAction } from '../../feature/FeaturePageHeader';
import { FeatureMergeDialog } from '../../feature/FeatureMergeDialog';
import { NavTargetButton, NavTargetPair } from '../../feature/NavTargetButton';
import { EMPTY_NAV_MARKS, buildNavMarks, type NavMarks } from '../../../lib/featureNavMarks';
import type { FeatureRun, FeatureStage, FeatureStageExecutor, FeatureState, FeatureAttention } from '../../../types/featureRun';
import { FeatureGlyph, type FeatureGlyph as GlyphKind } from './FeatureGlyphs';

// Тело режима «Исполнение фичи» по утверждённому макету (turn-plan-feature-popup-1):
// компактная карточка фичи — шапка (глиф · название mono · тег статуса) + мета
// (ветка · роль этого чата) + строка состояния (9 состояний + загрузка + ошибка,
// с главным действием) + 7 этапов (раскрываются по одному, исполнители) + подвал
// «Открыть страницу фичи →».
//
// Ушло из тела (на страницу фичи): счётчик, «Сейчас» (NowCard), полный PipelineFlow,
// легенда, «Попросить Марка», «Итог финала», «Перезапустить».
//
// Данные — только из СПИСКА `GET features?sessionId=` (FeatureRun): run.state,
// attention, stages[].executors[], epic (название/ветка). `chatTaskId` — задача
// текущего чата (Session.TaskId): по ней выводится роль чата в мете.

// === Глиф шапки по состоянию фичи ===
// not_started/idle → «нет» (absent), running/merging → run, waiting/merge_pending → wait
// (ромб), stopped → stopped (квадрат), merged → done (галочка), closed → closed (круг с чертой).
function headerGlyph(state: FeatureState): GlyphKind {
  switch (state) {
    case 'not_started':
    case 'idle': return 'absent';
    case 'waiting':
    case 'merge_pending': return 'wait';
    case 'stopped': return 'stopped';
    case 'merged': return 'done';
    case 'closed': return 'closed';
    // running, merging → run.
    default: return 'run';
  }
}

// === Строка состояния (чистая функция — тестируется без DOM) ===
//
// Тон: «сейчас» (идёт/мерж идёт) → bgCard/borderLight; info (ждёт вас/ждёт согласия)
// → infoBg; danger (стоит) → dangerBg; нейтральный (не начата/закрыта) → bgSelected;
// success (смержена) → successBg. Главное действие — по таблице макета.
export type StatusTone = 'now' | 'info' | 'danger' | 'neutral' | 'success';
export interface StatusAction {
  kind: 'chat' | 'merge' | 'pair';
  // chat: «Ответить {имя}» / «Чат {имя}»; merge: «Мерж…»; pair: «чат · задача».
  label: string;
  chatId: string | null;
  taskId: string | null;
  // merge_pending: рядом с «Мерж…» — пара «чат · задача» финала.
  showPair?: boolean;
}
export interface StatusLine {
  tone: StatusTone;
  headline: string;
  why: string | null;
  action: StatusAction | null;
}

// Имя активной стадии (для «{этап} · идёт/ждёт/стоит»); null — нет активной.
function activeStageName(run: FeatureRun): string | null {
  const stage = (run.stages ?? []).find(s => s.state === 'active');
  return stage ? STAGE_NAME[stage.key] : null;
}

// Имя персоны по id (null — не резолвится).
function personaName(id: string | null | undefined): string | null {
  if (!id) return null;
  return getPersonaById(id)?.name ?? null;
}

// Имена идущих (из now[], уникальные, «Кира, Денис»); '' — никто.
function runningNames(run: FeatureRun): string {
  const names: string[] = [];
  for (const n of run.now ?? []) {
    const name = personaName(n.personaId);
    if (name && !names.includes(name)) names.push(name);
  }
  return names.join(', ');
}

// Время строки: attention.since (ждёт/стоит) → «N мин»; active.runningSince (идёт/мерж)
// → «N мин»; merged/closed → duration из summary; null — без числа.
function statusMinutes(run: FeatureRun, now: number): number | null {
  const att = run.attention;
  if (att?.since) {
    const m = Math.max(0, Math.floor((now - new Date(att.since).getTime()) / 60000));
    if (m > 0) return m;
  }
  const active = (run.stages ?? []).find(s => s.state === 'active');
  if (active?.runningSince) {
    const m = Math.max(0, Math.floor((now - new Date(active.runningSince).getTime()) / 60000));
    if (m > 0) return m;
  }
  if (run.state === 'merged' || run.state === 'closed') {
    const d = run.summary?.durationMinutes;
    if (d != null && d > 0) return d;
  }
  return null;
}

// Дата-время (dd.mm hh:mm) для «Смержена …»/«Закрыта …»; null — нет.
function mergedDateTime(run: FeatureRun): string | null {
  const iso = run.summary?.mergedAt;
  if (!iso) return null;
  const d = new Date(iso);
  if (!Number.isFinite(d.getTime())) return null;
  const mm = String(d.getMonth() + 1).padStart(2, '0');
  const dd = String(d.getDate()).padStart(2, '0');
  const hh = String(d.getHours()).padStart(2, '0');
  const mi = String(d.getMinutes()).padStart(2, '0');
  return `${dd}.${mm} ${hh}:${mi}`;
}

// Итог смерженной: «{длительность} · {N} задач · {M} шагов · ↺K».
function mergedSummary(run: FeatureRun): string {
  const s = run.summary;
  if (!s) return '';
  const parts: string[] = [];
  if (s.durationMinutes > 0) parts.push(`Шла ${formatDuration(s.durationMinutes)}`);
  parts.push(`${s.tasks} ${pluralTasks(s.tasks)}`);
  parts.push(`${s.steps} ${pluralSteps(s.steps)}`);
  if (s.loops > 0) parts.push(`↺${s.loops}`);
  return parts.join(' · ');
}
function pluralTasks(n: number): string {
  const d = n % 10, h = n % 100;
  if (d === 1 && h !== 11) return 'задача';
  if (d >= 2 && d <= 4 && (h < 10 || h >= 20)) return 'задачи';
  return 'задач';
}
function pluralSteps(n: number): string {
  const d = n % 10, h = n % 100;
  if (d === 1 && h !== 11) return 'шаг';
  if (d >= 2 && d <= 4 && (h < 10 || h >= 20)) return 'шага';
  return 'шагов';
}

// R2 major 2 + 7: главное действие строки состояния waiting/stopped.
//   • att.kind==='merge' (защитный: бэк так не отдаёт — waiting ⇒ тревога wait,
//     stopped ⇒ stopped) — действие «Мерж…» (модалка), а не чат с подписью «Мерж…»
//     (major 7: подпись и переход должны совпадать);
//   • иначе — чат («Ответить {имя}» / «Чат {имя}») + рядом кнопка «задача» по
//     attention.taskId (major 2; макет: «Ответить Кире →» · «задача»).
function waitingStoppedAction(primary: FeaturePrimaryAction | null, att: FeatureAttention | null | undefined): StatusAction | null {
  if (!primary) return null;
  if (primary.kind === 'merge') {
    return { kind: 'merge', label: 'Мерж…', chatId: primary.chatId, taskId: att?.taskId ?? null };
  }
  return { kind: 'chat', label: primary.label, chatId: primary.chatId, taskId: att?.taskId ?? null };
}

// Строка состояния по run.state (единая точка правды — 9 состояний + загрузка/ошибка).
export function statusLineFor(run: FeatureRun, now: number): StatusLine {
  const stage = activeStageName(run);
  const mins = statusMinutes(run, now);
  const time = mins != null ? ` · ${formatDuration(mins)}` : '';
  const att = run.attention;

  switch (run.state) {
    case 'not_started':
    case 'idle':
      return {
        tone: 'neutral',
        headline: 'Не начата.',
        why: 'Разрез ещё не запускался — шаги появятся после него.',
        action: null,
      };
    case 'running': {
      const names = runningNames(run);
      return {
        tone: 'now',
        headline: `${stage ?? 'Разрез'} · идёт${time}.`,
        why: names ? `Работают ${names}.` : null,
        action: null,
      };
    }
    case 'waiting': {
      const name = personaName(att?.personaId);
      return {
        tone: 'info',
        headline: `${stage ?? 'Разрез'} · ждёт вас${time}.`,
        why: att?.reason ?? null,
        action: waitingStoppedAction(featurePrimaryAction(att, name), att),
      };
    }
    case 'stopped': {
      const name = personaName(att?.personaId);
      return {
        tone: 'danger',
        headline: `${stage ?? 'Разрез'} · стоит${time}.`,
        why: att?.reason ? `${name ? name + ' — ' : ''}${att.reason}` : null,
        action: waitingStoppedAction(featurePrimaryAction(att, name), att),
      };
    }
    case 'merge_pending': {
      // «Мерж…» (модалка) + рядом пара «чат · задача» финала.
      const final = run.final;
      return {
        tone: 'info',
        headline: `Мерж ждёт согласия${time}.`,
        why: 'Все шаги приняты, финальное ревью пройдено.',
        action: { kind: 'merge', label: 'Мерж…', chatId: final?.linkedSessionId ?? null, taskId: final?.id ?? null, showPair: true },
      };
    }
    case 'merging': {
      const merge = run.merge;
      const name = personaName(merge?.personaId);
      return {
        tone: 'now',
        headline: `Мерж идёт${time}.`,
        why: name ? `${name} сливает ветку в master.` : 'Сливаем ветку в master.',
        action: { kind: 'pair', label: 'мерж', chatId: merge?.linkedSessionId ?? null, taskId: merge?.id ?? null },
      };
    }
    case 'merged': {
      const dt = mergedDateTime(run);
      return {
        tone: 'success',
        headline: `Смержена${dt ? ' ' + dt : ''}.`,
        why: mergedSummary(run) || null,
        action: null,
      };
    }
    case 'closed': {
      const dt = mergedDateTime(run);
      return {
        tone: 'neutral',
        headline: `Закрыта без мержа${dt ? ' ' + dt : ''}.`,
        why: 'Ветка не слита; задачи остались в трекере.',
        action: { kind: 'pair', label: 'эпик', chatId: null, taskId: run.epic?.id ?? null },
      };
    }
    // default: state не пришёл (старые данные/кеш) — нейтральная «Не начата».
    default: {
      return { tone: 'neutral', headline: 'Не начата.', why: null, action: null };
    }
  }
}

// === Шапка: глиф · название mono · тег статуса ===
function Header({ run }: { run: FeatureRun }) {
  // run.state — всегда отдаёт бэк; ?? 'not_started' — защита от старых данных (state нет).
  const tag = featureRunStatusTag(run.state ?? 'not_started', activeStageName(run));
  const title = run.epic?.title ?? '';
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 7, minWidth: 0 }}>
      <FeatureGlyph kind={headerGlyph(run.state)} size={14} />
      <span
        title={title}
        style={{
          flex: 1, minWidth: 0, fontFamily: FONT.mono, fontSize: FS.base, fontWeight: 700,
          color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}
      >
        {title}
      </span>
      {/* R2 minor 1: отдельный id (feature-run-status-tag) — строка состояния несёт feature-run-status-line. */}
      <StatusTag tag={tag} testId="feature-run-status-tag" />
    </div>
  );
}

// === Мета: ветка (mono) · роль этого чата ===
// Роль — задача chatTaskId (Session.TaskId) среди executors[] фичи; нет — только ветка.
function Meta({ run, chatTaskId, isMobile }: { run: FeatureRun; chatTaskId?: string | null; isMobile: boolean }) {
  const branch = run.epic?.worktreeBranch ?? null;
  // Роль чата: исполнитель, чья taskId === chatTaskId, среди всех стадий.
  let role: string | null = null;
  if (chatTaskId) {
    for (const stage of run.stages ?? []) {
      const ex = stage.executors.find(e => e.taskId === chatTaskId);
      if (ex) { role = roleLabel(ex.taskRole, ex.stepRef, ex.lane) || null; break; }
    }
  }
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: '4px 12px', flexWrap: 'wrap',
      fontSize: FS.sm, color: C.textSecondary, fontFamily: FONT.sans,
      minHeight: isMobile ? TB.iconHitMobile : undefined,
    }}>
      {branch && (
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4, fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>
          <GitBranch size={12} strokeWidth={2} style={{ flexShrink: 0 }} />
          {branch}
        </span>
      )}
      {role && <span>этот чат — {role}</span>}
    </div>
  );
}

// === Строка состояния (тон панели А) + главное действие ===
const STATUS_STYLE: Record<StatusTone, { bg: string; border?: string; strong: string }> = {
  now:     { bg: C.bgCard, border: C.borderLight, strong: C.textHeading },
  info:    { bg: C.infoBg, strong: C.textHeading },
  danger:  { bg: C.dangerBg, strong: C.dangerText },
  neutral: { bg: C.bgSelected, strong: C.textHeading },
  success: { bg: C.successBg, strong: C.successText },
};

function StatusLine({ line, isMobile, marks = EMPTY_NAV_MARKS, onOpenChat, onOpenTask, onMerge }: {
  line: StatusLine;
  marks?: NavMarks;
  isMobile: boolean;
  onOpenChat: (chatId: string) => void;
  onOpenTask: (taskId: string) => void;
  onMerge: () => void;
}) {
  const s = STATUS_STYLE[line.tone];
  const btnStyle = isMobile ? { minHeight: TB.iconHitMobile } : undefined;
  return (
    <div
      data-testid="feature-run-status-line"
      role="status"
      style={{
        background: s.bg, borderRadius: R.lg, padding: '8px 10px',
        border: s.border ? `1px solid ${s.border}` : 'none',
        display: 'flex', flexDirection: 'column', gap: SP.xs, fontSize: FS.sm, color: C.textPrimary,
        fontFamily: FONT.sans,
      }}
    >
      <div style={{ display: 'flex', gap: SP.xs, alignItems: 'flex-start' }}>
        <span style={{ marginTop: 2, flexShrink: 0 }}>
          <FeatureGlyph kind={line.tone === 'danger' ? 'stopped' : line.tone === 'info' ? 'wait' : line.tone === 'success' ? 'done' : 'run'} size={14} />
        </span>
        <span style={{ minWidth: 0 }}>
          <b style={{ color: s.strong, fontWeight: 700 }}>{line.headline}</b>{' '}
          {line.why && <span style={{ color: C.textSecondary }}>{line.why}</span>}
        </span>
      </div>
      {line.action && (
        <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', alignItems: 'center' }}>
          {line.action.kind === 'merge' && (
            <Button size="xs" variant="primary" style={btnStyle} onClick={onMerge}>Мерж…</Button>
          )}
          {line.action.kind === 'chat' && line.action.chatId && (
            <Button size="xs" variant="primary" style={btnStyle} onClick={() => onOpenChat(line.action!.chatId!)}>
              {line.action.label}
              <ArrowRight size={13} strokeWidth={2} />
            </Button>
          )}
          {/* R2 major 2: «задача» рядом с главным действием (waiting/stopped, по attention.taskId). */}
          {line.action.kind === 'chat' && line.action.taskId && (
            <NavTargetButton
              kind="task"
              taskId={line.action.taskId}
              taskClosed={marks.taskClosed(line.action.taskId)}
              isMobile={isMobile}
              onOpen={() => onOpenTask(line.action!.taskId!)}
            />
          )}
          {(line.action.kind === 'pair' || line.action.showPair) && (
            <NavTargetPair
              chatId={line.action.chatId}
              chatArchived={marks.chatArchived(line.action.chatId)}
              taskId={line.action.taskId}
              taskClosed={marks.taskClosed(line.action.taskId)}
              showNoChat={false}
              isMobile={isMobile}
              onOpenChat={() => { if (line.action!.chatId) onOpenChat(line.action!.chatId!); }}
              onOpenTask={() => { if (line.action!.taskId) onOpenTask(line.action!.taskId!); }}
            />
          )}
        </div>
      )}
    </div>
  );
}

// === Чип модели (mono, navInk) + подсказка с полной моделью ===
// R2 major 6: чип — КНОПКА: на тач-экране (нет hover) тап раскрывает/закрывает
// подсказку (повторный тап закрывает, как в макете); hover/фокус сохраняются (Tooltip).
// R2 minor 5: на 360 аватар скрыт — чип обычный inline-элемент (не на аватаре).
function ModelChip({ label, full, isMobile }: { label: string | null; full: string | null; isMobile: boolean }) {
  if (!label) return null;
  const tip = full && full !== label ? `Модель чата: ${full}` : `Модель чата: ${label}`;
  const canHover = useCanHover();
  const [tapped, setTapped] = useState(false);
  const overlay = !isMobile; // на аватаре (десктоп) — absolute; на 360 (аватар скрыт) — inline.
  return (
    <Tooltip content={tip} focused={tapped}>
      <button
        type="button"
        aria-label={tip}
        onClick={() => setTapped(t => !t)}
        // Десктоп: после клика чип сфокусирован — не «закрепляем» подсказку навсегда.
        onMouseLeave={() => { if (canHover) setTapped(false); }}
        style={{
          ...(overlay
            ? { position: 'absolute', left: '50%', transform: 'translateX(-50%)', bottom: -2 }
            : { position: 'relative' }),
          border: 'none', cursor: 'pointer', flexShrink: 0,
          fontFamily: FONT.mono, fontSize: FS.xxs, fontWeight: 700, lineHeight: '14px',
          padding: '0 4px', borderRadius: R.sm, background: C.navInk, color: C.onNavInk,
          boxShadow: `0 0 0 1.5px ${C.bgWhite}`, whiteSpace: 'nowrap',
        }}
      >
        {label}
      </button>
    </Tooltip>
  );
}

// === Исполнитель этапа: аватар (+чип модели) · имя · роль · статус · «чат · задача» ===
function ExecutorRow({ ex, isMobile, marks = EMPTY_NAV_MARKS, onOpenChat, onOpenTask }: {
  ex: FeatureStageExecutor;
  marks?: NavMarks;
  isMobile: boolean;
  onOpenChat: (chatId: string) => void;
  onOpenTask: (taskId: string) => void;
}) {
  usePersonasVersion();
  useEffect(() => { void ensurePersonasLoaded(); }, []);
  const persona = ex.personaId ? getPersonaById(ex.personaId) : undefined;
  const name = persona?.name ?? 'Исполнитель';
  const role = roleLabel(ex.taskRole, ex.stepRef, ex.lane);
  // Статус исполнителя: running → «идёт», waiting → «ждёт вас», stopped → «стоит», done → «готово», pending → «не начат».
  const statusWord = ex.status === 'running' ? 'идёт'
    : ex.status === 'waiting' ? 'ждёт вас'
    : ex.status === 'stopped' ? 'стоит'
    : ex.status === 'pending' ? 'не начат'
    : 'готово';
  const statusColor = ex.status === 'waiting' ? C.info : ex.status === 'stopped' ? C.dangerText : C.textMuted;
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minHeight: isMobile ? TB.iconHitMobile : 36, padding: '2px 0' }}>
      <span style={{ position: 'relative', flexShrink: 0, display: 'inline-flex', alignItems: 'center' }}>
        {/* R2 minor 5: аватар скрыт на 360 (макет); чип модели остаётся (major 6 — по тапу). */}
        {persona && !isMobile && <PersonaAvatar persona={persona} size={24} />}
        <ModelChip label={ex.modelLabel} full={ex.modelFull} isMobile={isMobile} />
      </span>
      <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
        <span style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0 }}>
          <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{name}</span>
          {role && !isMobile && (
            <span style={{
              fontFamily: FONT.mono, fontSize: FS.xxs, fontWeight: 700, flexShrink: 0,
              background: C.bgSelected, color: C.textSecondary, borderRadius: R.sm, padding: '0 5px', lineHeight: '15px',
            }}>{role}</span>
          )}
        </span>
        <span style={{ fontSize: FS.xs, color: statusColor, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {statusWord}
        </span>
      </div>
      <NavTargetPair
        chatId={ex.chatId}
        // minor 3: ОДИН источник «в архиве» — marks.chatArchived (unified), не ex.chatArchived.
        chatArchived={marks.chatArchived(ex.chatId)}
        taskId={ex.taskId}
        taskClosed={marks.taskClosed(ex.taskId)}
        showNoChat={false}
        isMobile={isMobile}
        onOpenChat={() => { if (ex.chatId) onOpenChat(ex.chatId); }}
        onOpenTask={() => onOpenTask(ex.taskId)}
      />
    </div>
  );
}

// === Точка этапа (как на «Хронологии»): done/active(+tone)/pending/absent ===
function stageDotStyle(stage: FeatureStage): { bg: string; ring?: string; inset?: string } {
  if (stage.state === 'done') return { bg: C.success };
  if (stage.state === 'active') {
    if (stage.tone === 'wait') return { bg: C.info, ring: C.infoBg };
    if (stage.tone === 'stopped') return { bg: C.danger, ring: C.dangerBg };
    if (stage.tone === 'loop') return { bg: C.bgWhite, inset: C.textHeading };
    return { bg: C.textHeading, ring: C.bgSelected }; // run
  }
  if (stage.state === 'pending') return { bg: C.bgWhite, inset: C.progressTrack };
  return { bg: C.bgWhite, inset: C.dashed }; // absent
}

function StageDot({ stage }: { stage: FeatureStage }) {
  const d = stageDotStyle(stage);
  const style: React.CSSProperties = {
    width: 14, height: 14, borderRadius: 14, flexShrink: 0, position: 'relative', zIndex: 1,
    background: d.bg,
    ...(d.ring ? { boxShadow: `0 0 0 3px ${d.ring}` } : {}),
    ...(d.inset ? { boxShadow: `inset 0 0 0 2px ${d.inset}` } : {}),
  };
  return <span style={style} />;
}

// Слово состояния этапа (справа): пройден/идёт/ждёт вас/стоит/по шагам/впереди/не было.
function stageWord(stage: FeatureStage): { word: string; color: string } {
  if (stage.state === 'done') return { word: 'пройден', color: C.textMuted };
  if (stage.state === 'active') {
    if (stage.tone === 'wait') return { word: 'ждёт вас', color: C.info };
    if (stage.tone === 'stopped') return { word: 'стоит', color: C.dangerText };
    if (stage.tone === 'loop') return { word: 'по шагам', color: C.textHeading };
    return { word: 'идёт', color: C.textHeading };
  }
  if (stage.state === 'pending') return { word: 'впереди', color: C.textMuted };
  return { word: 'не было', color: C.textMuted };
}

// === Строка этапа (32 px, на 360 — 44): точка · название · слово+время · шеврон ===
function StageRow({ stage, open, isMobile, onToggle }: {
  stage: FeatureStage;
  open: boolean;
  isMobile: boolean;
  onToggle: () => void;
}) {
  const [hovered, setHovered] = useState(false);
  const [focused, setFocused] = useState(false);
  const clickable = stage.state !== 'pending' && stage.state !== 'absent';
  const { word, color } = stageWord(stage);
  const time = stage.state === 'active' && stage.runningSince
    ? (() => { const m = Math.max(0, Math.round((Date.now() - new Date(stage.runningSince!).getTime()) / 60000)); return m > 0 ? formatDuration(m) : null; })()
    : null;
  const inner = (
    <>
      <StageDot stage={stage} />
      <span style={{
        flex: 1, minWidth: 0, fontSize: FS.base, fontWeight: 600,
        color: clickable ? C.textHeading : C.textMuted,
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>{STAGE_NAME[stage.key]}</span>
      <span style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap', flexShrink: 0 }}>
        <span style={{ fontWeight: 600, color }}>{word}</span>
        {time && <span style={{ fontFamily: FONT.mono }}>{time}</span>}
        {clickable && (
          <ChevronRight size={12} strokeWidth={2} style={{ color: C.textMuted, transform: open ? 'rotate(90deg)' : 'none', transition: 'transform 0.15s' }} />
        )}
      </span>
    </>
  );
  const baseStyle: React.CSSProperties = {
    display: 'grid', gridTemplateColumns: '16px 1fr auto', gap: 9, alignItems: 'center',
    width: '100%', minHeight: isMobile ? TB.iconHitMobile : 32, padding: '4px 6px 4px 0',
    borderRadius: R.md, border: 'none', textAlign: 'left', fontFamily: 'inherit',
    background: open ? C.bgCard : (hovered ? C.bgSelected : 'transparent'),
    cursor: clickable ? 'pointer' : 'default',
    boxShadow: focused && clickable ? SHADOW.focus : 'none',
  };
  if (!clickable) {
    return <div aria-label={`${STAGE_NAME[stage.key]}: ${word}`} style={baseStyle}>{inner}</div>;
  }
  return (
    <button
      type="button"
      aria-expanded={open}
      onClick={onToggle}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      onFocus={() => setFocused(true)}
      onBlur={() => setFocused(false)}
      style={baseStyle}
    >
      {inner}
    </button>
  );
}

// === Этапы: 7 строк, раскрыт один (по умолчанию текущий/первый кликабельный) ===
function Stages({ run, isMobile, marks, onOpenChat, onOpenTask }: {
  run: FeatureRun;
  marks: NavMarks;
  isMobile: boolean;
  onOpenChat: (chatId: string) => void;
  onOpenTask: (taskId: string) => void;
}) {
  const stages = run.stages ?? [];
  // По умолчанию раскрыт текущий (active); нет активного — первый кликабельный (done); иначе 0.
  const [openKey, setOpenKey] = useState<string | null>(() => {
    const active = stages.find(s => s.state === 'active');
    if (active) return active.key;
    const done = stages.find(s => s.state === 'done');
    if (done) return done.key;
    return stages.length > 0 ? stages[0].key : null;
  });
  if (stages.length === 0) return null;
  return (
    <div style={{ display: 'flex', flexDirection: 'column' }}>
      {stages.map(stage => {
        const open = openKey === stage.key;
        return (
          <div key={stage.key}>
            <StageRow
              stage={stage}
              open={open}
              isMobile={isMobile}
              onToggle={() => setOpenKey(open ? null : stage.key)}
            />
            {open && (
              <div style={{ margin: '0 0 4px 25px', paddingLeft: 8, borderLeft: `1px solid ${C.divider}`, display: 'flex', flexDirection: 'column', gap: 2 }}>
                {stage.executors.length > 0
                  ? stage.executors.map((ex, i) => (
                    <ExecutorRow key={`${ex.taskId}-${i}`} ex={ex} isMobile={isMobile} marks={marks} onOpenChat={onOpenChat} onOpenTask={onOpenTask} />
                  ))
                  : <span style={{ fontSize: FS.sm, color: C.textMuted, padding: '4px 0 6px' }}>Исполнителей нет</span>}
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}

// === Загрузка: скелет (заголовок, строка, 7 этапов) ===
function LoadingSkeleton() {
  const bar = (w: string, h: number) => (
    <div style={{ width: w, height: h, background: C.bgSelected, borderRadius: R.sm }} />
  );
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      {bar('60%', 14)}
      {bar('40%', 12)}
      {bar('100%', 44)}
      {[0, 1, 2, 3, 4, 5, 6].map(() => bar('100%', 18))}
    </div>
  );
}

// === Ошибка: «Не удалось загрузить фичу.» + «Повторить» ===
function ErrorBlock({ error, isMobile, onRetry }: { error: string | null; isMobile: boolean; onRetry: () => void }) {
  return (
    <div
      role="alert"
      style={{
        background: C.dangerBg, border: `1px solid ${C.dangerBorder}`, borderRadius: R.lg,
        padding: '10px 12px', display: 'flex', flexDirection: 'column', gap: SP.sm,
        fontSize: FS.sm, color: C.textPrimary, fontFamily: FONT.sans,
      }}
    >
      <span><b style={{ color: C.dangerText }}>Не удалось загрузить фичу.</b>{' '}{error ?? 'Сервер не ответил.'}</span>
      <Button size="xs" variant="secondary" style={isMobile ? { minHeight: TB.iconHitMobile } : undefined} onClick={onRetry}>
        Повторить
      </Button>
    </div>
  );
}

// === Подвал: «Открыть страницу фичи →» (ведёт на «Хронологию») ===
function Footer({ isMobile, onOpenPage }: { isMobile: boolean; onOpenPage: () => void }) {
  const [hovered, setHovered] = useState(false);
  const [focused, setFocused] = useState(false);
  return (
    <button
      type="button"
      onClick={onOpenPage}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      onFocus={() => setFocused(true)}
      onBlur={() => setFocused(false)}
      title="Открыть страницу фичи"
      style={{
        display: 'flex', alignItems: 'center', justifyContent: 'flex-end', gap: 5,
        width: '100%', minHeight: isMobile ? TB.iconHitMobile : undefined,
        padding: '8px 13px', border: 'none', borderTop: `1px solid ${C.divider}`,
        background: 'transparent', cursor: 'pointer',
        fontSize: FS.sm, fontWeight: 600, color: C.accent, fontFamily: 'inherit',
        boxShadow: focused ? SHADOW.focus : 'none',
      }}
    >
      Открыть страницу фичи
      <span style={{ display: 'flex', transform: hovered ? 'translateX(3px)' : 'none', transition: 'transform 0.15s' }}>
        <ArrowRight size={13} strokeWidth={2} />
      </span>
    </button>
  );
}

// === Карточка фичи (тело режима «Исполнение фичи») ===
//
// `chatTaskId` — задача текущего чата (Session.TaskId): по ней роль в мете.
// `loading`/`error` — состояния запроса (из стора featureRuns). `onOpenPage` — подвал.
export function FeatureRunView({
  run, chatTaskId, loading, error, onOpenPage, onRetry, onClose,
}: {
  run: FeatureRun;
  chatTaskId?: string | null;
  loading?: boolean;
  error?: string | null;
  onOpenPage?: () => void;
  onRetry?: () => void;
  // R2 major 3: переход (чат/задача) закрывает попап — вызывающий (попап пилюли)
  // передаёт onClose; в панели А (карточка, не попап) onClose не передан.
  onClose?: () => void;
}) {
  const isMobile = useIsMobile();
  usePersonasVersion();
  useEffect(() => { void ensurePersonasLoaded(); }, []);
  // Пометки «в архиве»/«задача закрыта» — из агрегата (задачи и исполнители стадий).
  const marks = buildNavMarks(run);
  // Честный прогресс (Ф-pr): из run.stepProgress; null — старые ответы (строки нет).
  const progress = stepProgressFromRun(run);

  // Навигация: переход в чат/задачу; onClose — закрыть попап (R2 major 3).
  // major 1: точка возврата «← к фиче {название}» — БЕЗ вкладки (из попапа/панели, не из
  // вкладки страницы); возврат ведёт на последнюю использованную вкладку (механизм Ф-pl-b).
  // minor (ревью Ф9b, зам. 2): точка возврата пишется только если чат реально открылся:
  // при openChatById → false («Чат не найден») указатель на несуществующий чат только
  // потерял бы прежнюю точку возврата без видимой причины.
  const openChat = async (chatId: string) => {
    const opened = await openChatById(chatId);
    if (opened) recordFeatureReturn(run.featureId, { kind: 'chat', id: chatId }, run.epic?.title ?? null, undefined, false);
    onClose?.();
  };
  const openTask = (taskId: string) => { openTaskInSection({ id: taskId, projectId: run.epic?.projectId ?? undefined }); onClose?.(); };

  // === Мерж (Ф6): общая модалка подтверждения → POST /merge ===
  // pending (кнопка «Запускаю…» заблокирована), успех (тост, диалог закрывается),
  // 409 (идемпотентность, без красной ошибки), прочий сбой (сообщение + «Повторить»).
  const [mergeOpen, setMergeOpen] = useState(false);
  const [mergePending, setMergePending] = useState(false);
  const [mergeError, setMergeError] = useState<string | null>(null);
  const mergeInFlight = useRef(false);
  const openMergeDialog = () => { setMergeError(null); setMergeOpen(true); };
  const closeMergeDialog = () => { setMergeOpen(false); setMergePending(false); setMergeError(null); mergeInFlight.current = false; };
  const confirmMerge = async () => {
    if (mergeInFlight.current) return;
    mergeInFlight.current = true;
    setMergePending(true);
    setMergeError(null);
    try {
      await api.tasks.mergeFeature(run.featureId);
      showToast('Задача мержа создана', 'Марк начал мерж');
      closeMergeDialog();
    } catch (e) {
      const err = e as Error & { status?: number };
      if (err.status === 409) closeMergeDialog();
      else {
        mergeInFlight.current = false;
        setMergeError(err.message || 'Сервер не ответил');
        setMergePending(false);
      }
    }
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      {loading ? (
        <LoadingSkeleton />
      ) : error ? (
        <ErrorBlock error={error} isMobile={isMobile} onRetry={onRetry ?? (() => {})} />
      ) : (
        <>
          <Header run={run} />
          <Meta run={run} chatTaskId={chatTaskId} isMobile={isMobile} />
          {progress && (
            <div
              data-testid="feature-run-progress"
              style={{ fontSize: FS.sm, color: C.textSecondary, fontFamily: FONT.sans }}
            >
              {formatStepProgress(progress)}
            </div>
          )}
          <StatusLine
            line={statusLineFor(run, Date.now())}
            isMobile={isMobile}
            marks={marks}
            onOpenChat={openChat}
            onOpenTask={openTask}
            onMerge={openMergeDialog}
          />
          <Stages run={run} isMobile={isMobile} marks={marks} onOpenChat={openChat} onOpenTask={openTask} />
          {onOpenPage && <Footer isMobile={isMobile} onOpenPage={onOpenPage} />}
        </>
      )}
      {mergeOpen && (
        <FeatureMergeDialog
          title={run.epic?.title ?? ''}
          branch={run.epic?.worktreeBranch ?? null}
          pending={mergePending}
          error={mergeError}
          onCancel={closeMergeDialog}
          onConfirm={() => { void confirmMerge(); }}
        />
      )}
    </div>
  );
}
