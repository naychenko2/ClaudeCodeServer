import { useState } from 'react';
import type { CSSProperties } from 'react';
import { C, R, FS, FONT } from '../../lib/design';
import { STAGE_NAME } from '../../lib/chatGroups';
import { roleLabel } from '../../lib/roleLabel';
import { getPersonaById } from '../../lib/personas';
import { dateOnly, hhmm } from '../chat/feature/featureTime';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { Tooltip } from '../ui/Tooltip';
import { NavTargetButton } from './NavTargetButton';
import type { FeatureRun, FeatureStage, FeatureStageExecutor, FeatureTask, StageKey } from '../../types/featureRun';

// Подкомпоненты вкладки «Хронология» (волна 3, Ф2a, макет features-overview-3-final §5):
// точка/линия этапа, подпись состояния, границы «dd.mm · ЧЧ:ММ – ЧЧ:ММ», длительность «≈»,
// исполнители (аватар, роль, чип модели, статус, время, пара «чат · задача») и плашки
// «ждёт вас · N мин» / «стоит · N мин». Чистые функции — в этом же файле, тестируются
// в FeatureTimelineTab.test.ts.

// === Чистые функции (тестируются в node, без DOM) ===

// Имя этапа по ключу (порядок жизненного цикла: cut → plan → prod → review → qa → final → merge).
export function stageName(key: StageKey): string {
  return STAGE_NAME[key] ?? key;
}

// Подпись состояния этапа (макет §5): done — нет; active — по тону (run/loop — «идёт»,
// wait — «ждёт вас», stopped — «стоит»); pending — «впереди»; absent — «не было».
// overlapsActive — «идёт параллельно» (стадия пересекается с идущей, макет: кольцо вместо точки).
// tone — muted (textMuted) | info (C.info) | danger (C.dangerText).
export interface StageStatus {
  text: string;
  tone: 'muted' | 'info' | 'danger';
}
export function stageStatusLabel(stage: FeatureStage): StageStatus | null {
  if (stage.state === 'done') return null;
  if (stage.state === 'active') {
    if (stage.overlapsActive) return { text: 'идёт параллельно', tone: 'muted' };
    if (stage.tone === 'wait') return { text: 'ждёт вас', tone: 'info' };
    if (stage.tone === 'stopped') return { text: 'стоит', tone: 'danger' };
    return { text: 'идёт', tone: 'muted' };
  }
  if (stage.state === 'pending') return { text: 'впереди', tone: 'muted' };
  return { text: 'не было', tone: 'muted' };
}

// Границы этапа (макет §5): обе метки — «dd.mm · ЧЧ:ММ – ЧЧ:ММ»; только старт — «с dd.mm · ЧЧ:ММ»;
// старта нет — null. Время — местный пояс (featureTime: локальные геттеры).
export function stageBoundsLabel(stage: FeatureStage): string | null {
  const start = dateOnly(stage.firstStartedAt);
  if (!start) return null;
  const t1 = hhmm(stage.firstStartedAt);
  const end = dateOnly(stage.lastCompletedAt);
  const t2 = hhmm(stage.lastCompletedAt);
  if (end && t2) return `${start} · ${t1} – ${t2}`;
  return `с ${start} · ${t1}`;
}

// Длительность «≈» (макет §5): минуты → «≈ N мин» / «≈ N ч M мин»; 0/null — null.
export function stageDurationLabel(approxMinutes?: number | null): string | null {
  if (!approxMinutes || approxMinutes <= 0) return null;
  if (approxMinutes < 60) return `≈ ${approxMinutes} мин`;
  const h = Math.floor(approxMinutes / 60);
  const m = approxMinutes % 60;
  return m > 0 ? `≈ ${h} ч ${m} мин` : `≈ ${h} ч`;
}

// Точка этапа (макет §5, 14 px): цвет/кольцо по состоянию; overlapsActive — белая с кольцом.
export function stageDotStyle(stage: FeatureStage): CSSProperties {
  const base: CSSProperties = {
    width: 14, height: 14, borderRadius: R.full, flexShrink: 0, position: 'relative', zIndex: 1,
  };
  if (stage.state === 'done') {
    return { ...base, background: C.success };
  }
  if (stage.state === 'active') {
    if (stage.overlapsActive) {
      return { ...base, background: C.bgWhite, boxShadow: `inset 0 0 0 3px ${C.textHeading}` };
    }
    if (stage.tone === 'wait') return { ...base, background: C.info, boxShadow: `0 0 0 3px ${C.infoBg}` };
    if (stage.tone === 'stopped') return { ...base, background: C.danger, boxShadow: `0 0 0 3px ${C.dangerBg}` };
    return { ...base, background: C.textHeading, boxShadow: `0 0 0 3px ${C.bgSelected}` };
  }
  if (stage.state === 'pending') {
    return { ...base, background: C.bgWhite, boxShadow: `inset 0 0 0 2px ${C.progressTrack}` };
  }
  // absent
  return { ...base, background: 'transparent', boxShadow: `inset 0 0 0 2px ${C.dashed}` };
}

// Линия под точкой (к следующему этапу, макет §5): done — success; absent — пунктир;
// иначе — progressTrack; последний этап — null (линии нет).
export function stageLineStyle(stage: FeatureStage, isLast: boolean): CSSProperties | null {
  if (isLast) return null;
  const base: CSSProperties = { position: 'absolute', left: 8, top: 20, bottom: -6, width: 2 };
  if (stage.state === 'done') return { ...base, background: C.success };
  if (stage.state === 'absent') return { ...base, background: 'none', borderLeft: `2px dashed ${C.dashed}`, width: 0 };
  return { ...base, background: C.progressTrack };
}

// Задачи run по id (для времени/статуса исполнителей): epic + steps + reviews + qa + final +
// merge + cut + plan. Контракт: UI обязан пережить отсутствие любого списка (старые данные).
export function buildTaskMap(run: FeatureRun): Map<string, FeatureTask> {
  const map = new Map<string, FeatureTask>();
  const add = (t?: FeatureTask | null) => { if (t?.id) map.set(t.id, t); };
  add(run.epic);
  for (const t of run.steps ?? []) add(t);
  for (const t of run.reviews ?? []) add(t);
  for (const t of run.qa ?? []) add(t);
  add(run.final);
  add(run.merge);
  for (const t of run.cut ?? []) add(t);
  for (const t of run.plan ?? []) add(t);
  return map;
}

// Минуты от ISO-момента до now (округление вверх, min 0); null/невалидно — null.
export function minutesSince(iso: string | null | undefined, now: number): number | null {
  if (!iso) return null;
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return null;
  return Math.max(0, Math.ceil((now - t) / 60000));
}

// Статус исполнителя (макет §5): label + тон + (идёт/ждёт/стоит) минуты от задачи.
// Тон: muted (идёт/не начат) | info (ждёт вас) | danger (стоит) | success (готово).
export interface ExecutorStatus {
  label: string;
  tone: 'muted' | 'info' | 'danger' | 'success';
  minutes?: number;
}
export function executorStatus(x: FeatureStageExecutor, task: FeatureTask | undefined, now: number): ExecutorStatus {
  switch (x.status) {
    case 'running': {
      const start = task?.claudeStartedAt ?? task?.createdAt;
      const minutes = start ? minutesSince(start, now) : undefined;
      return { label: 'идёт', tone: 'muted', minutes: minutes ?? undefined };
    }
    case 'waiting': {
      const minutes = minutesSince(task?.awaitingHumanSince, now);
      return { label: 'ждёт вас', tone: 'info', minutes: minutes ?? undefined };
    }
    case 'stopped': {
      const minutes = minutesSince(task?.executorStoppedAt, now);
      return { label: 'стоит', tone: 'danger', minutes: minutes ?? undefined };
    }
    case 'done':
      return { label: 'готово', tone: 'success' };
    case 'pending':
      return { label: 'не начат', tone: 'muted' };
    default:
      return { label: '', tone: 'muted' };
  }
}

// Плашка состояния этапа (макет §5): «ждёт вас · N мин» (info) / «стоит · N мин» (danger)
// по тону стадии; N — максимум минут по исполнителям с этим статусом. Тоны run/loop — без
// плашки (стадия просто идёт).
export function stageStatusBadge(stage: FeatureStage, taskMap: Map<string, FeatureTask>, now: number):
  { text: string; tone: 'info' | 'danger' } | null {
  if (stage.state !== 'active' || (stage.tone !== 'wait' && stage.tone !== 'stopped')) return null;
  const want = stage.tone === 'wait' ? 'waiting' : 'stopped';
  let max: number | undefined;
  for (const x of stage.executors) {
    if (x.status !== want) continue;
    const task = x.taskId ? taskMap.get(x.taskId) : undefined;
    const st = executorStatus(x, task, now);
    if (st.minutes != null) max = Math.max(max ?? 0, st.minutes);
  }
  const base = stage.tone === 'wait' ? 'ждёт вас' : 'стоит';
  return { text: max != null ? `${base} · ${max} мин` : base, tone: stage.tone === 'wait' ? 'info' : 'danger' };
}

// === Подкомпоненты ===

// Чип модели (макет §5/§7): mono 10, «Opus»/«Sonnet»/«Qwen»/«Авто»; «Авто» раскрывается —
// подсказка с полной моделью (modelFull). Подсказка — по наведению, фокусу и тапу (Tooltip).
export function ModelChip({ label, full, isMobile = false }: { label: string; full?: string | null; isMobile?: boolean }) {
  const [tapped, setTapped] = useState(false);
  const tip = full ? (label === 'Авто' ? `Модель: ${full}` : full) : null;
  const chip = (
    <span
      data-testid="timeline-model-chip"
      onClick={isMobile ? () => setTapped(v => !v) : undefined}
      style={{
        fontFamily: FONT.mono, fontSize: FS.xxs, fontWeight: 700,
        color: C.textSecondary, background: C.bgSelected,
        borderRadius: R.sm, padding: '0 4px', whiteSpace: 'nowrap', flexShrink: 0,
        minHeight: isMobile ? 40 : undefined,
        display: 'inline-flex', alignItems: 'center',
      }}
    >
      {label}
    </span>
  );
  if (!tip) return chip;
  return (
    <Tooltip content={tip} focused={tapped}>
      {chip}
    </Tooltip>
  );
}

// Аватар исполнителя (18): персона по personaId; не найдена — нейтральный круг (без инициалов).
function ExecutorAvatar({ personaId }: { personaId: string | null }) {
  if (personaId) {
    const persona = getPersonaById(personaId);
    if (persona) return <PersonaAvatar persona={persona} size={18} />;
  }
  return (
    <span
      aria-hidden
      style={{
        width: 18, height: 18, borderRadius: R.full, flexShrink: 0,
        background: C.bgSelected, color: C.textMuted,
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
        fontFamily: FONT.sans, fontSize: FS.xxs, fontWeight: 700,
      }}
    >
      ?
    </span>
  );
}

// Строка исполнителя (макет §5): аватар 18 · имя 12 · роль (roleLabel) · чип модели ·
// статус (идёт/ждёт вас/стоит/готово) · время · пара «чат · задача» (NavTargetButton).
// chatId == null → «чата нет» (без мёртвой ссылки). Навигацию наполнит Ф9a.
export function ExecutorRow({ x, task, now, isMobile = false }: {
  x: FeatureStageExecutor; task?: FeatureTask; now: number; isMobile?: boolean;
}) {
  const persona = x.personaId ? getPersonaById(x.personaId) : undefined;
  const name = persona?.name ?? (x.personaId ? x.personaId : '—');
  const role = roleLabel(x.taskRole, x.stepRef, x.lane);
  const st = executorStatus(x, task, now);
  const time = st.minutes != null ? `${st.minutes} мин` : null;

  const statusColor =
    st.tone === 'info' ? C.info :
    st.tone === 'danger' ? C.dangerText :
    st.tone === 'success' ? C.successText :
    C.textMuted;

  return (
    <div
      data-testid="timeline-executor"
      style={{
        display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap',
        padding: '3px 0', fontFamily: FONT.sans,
      }}
    >
      <ExecutorAvatar personaId={x.personaId} />
      <span style={{ fontSize: FS.sm, color: C.textPrimary, whiteSpace: 'nowrap' }}>{name}</span>
      {role && (
        <span
          data-testid="timeline-executor-role"
          style={{
            fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary,
            background: C.bgSelected, borderRadius: R.sm, padding: '0 5px',
            whiteSpace: 'nowrap', flexShrink: 0,
          }}
        >
          {role}
        </span>
      )}
      {x.modelLabel && <ModelChip label={x.modelLabel} full={x.modelFull} isMobile={isMobile} />}
      {st.label && (
        <span data-testid="timeline-executor-status" style={{ fontSize: FS.xs, fontWeight: 600, color: statusColor, whiteSpace: 'nowrap' }}>
          {st.label}
        </span>
      )}
      {time && (
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap' }}>
          {time}
        </span>
      )}
      <span style={{ marginLeft: 'auto', display: 'inline-flex', gap: 4 }}>
        {x.chatId ? (
          <NavTargetButton kind="chat" label="чат" title="Открыть чат" onOpen={() => {}} isMobile={isMobile} />
        ) : (
          <span data-testid="timeline-executor-no-chat" style={{ fontSize: FS.xs, color: C.textMuted, alignSelf: 'center' }}>
            чата нет
          </span>
        )}
        <NavTargetButton kind="task" label="задача" title="Открыть задачу" onOpen={() => {}} isMobile={isMobile} />
      </span>
    </div>
  );
}
