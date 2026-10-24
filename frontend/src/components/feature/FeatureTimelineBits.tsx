import { useState } from 'react';
import type { CSSProperties } from 'react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { STAGE_NAME } from '../../lib/chatGroups';
import { roleLabel } from '../../lib/roleLabel';
import { getPersonaById } from '../../lib/personas';
import { dateOnly, hhmm } from '../chat/feature/featureTime';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { Tooltip } from '../ui/Tooltip';
import { openChatById } from '../../lib/openChat';
import { EMPTY_NAV_MARKS, type NavMarks } from '../../lib/featureNavMarks';
import { openTaskInSection } from '../../lib/tasks';
import { recordFeatureReturn } from '../../lib/featureBackNav';
import { NavTargetPair } from './NavTargetButton';
import type { StepProgress } from '../../lib/featureProgress';
import type { FeatureRun, FeatureStage, FeatureStageExecutor, FeatureStepChain, FeatureTask, StageKey } from '../../types/featureRun';

// Подкомпоненты вкладки «Хронология» (волна 3, Ф2a, макет features-overview-3-final §5):
// точка/линия этапа, подпись состояния, границы «dd.mm · ЧЧ:ММ – ЧЧ:ММ», длительность «≈»,
// исполнители (аватар, роль, чип модели, статус, время, пара «чат · задача»), плашки
// «ждёт вас · N мин» / «стоит · N мин». Строки шагов и витков (Ф2b) удалены: «Производство»
// рисуется лентой карточек (FeatureProdCardsView), других потребителей у них нет (хвост R4e).
// Чистые функции — в этом же файле, тестируются в FeatureTimelineTab.test.ts.

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

// === Ф2b: чистые функции статуса шага (лента карточек «Производства») ===

// Тон по готовой строке статуса бэка (chain.status): одна строка — одна пара
// (label, tone), чтобы подпись и цвет не расходились (раньше тон брался из outcome,
// а подпись из status — два источника могли не совпасть).
function toneFromStatus(status: string): 'success' | 'active' | 'muted' {
  switch (status) {
    case 'принят': return 'success';
    case 'идёт':
    case 'закрыт без повторного ревью': return 'active';
    case 'ждёт ревью':
    case 'снят с выкладки':
    default: return 'muted';
  }
}

// Статус шага — ОДИН источник: пара (label, tone). Если бэк дал готовую строку
// (chain.status) — подпись и тон из неё; у старых ответов без status — фолбэк из
// outcome (честная деградация, без выдуманных подписей). В обоих случаях label и tone
// производятся вместе, поэтому не могут расконсистентиться.
export function stepStatusView(chain: FeatureStepChain): { label: string; tone: 'success' | 'active' | 'muted' } {
  if (chain.status) return { label: chain.status, tone: toneFromStatus(chain.status) };
  switch (chain.outcome) {
    case 'accepted': return { label: 'принят', tone: 'success' };
    case 'running': return { label: 'идёт', tone: 'active' };
    case 'returned': return { label: 'закрыт без повторного ревью', tone: 'active' };
    case 'withdrawn': return { label: 'снят с выкладки', tone: 'muted' };
    case 'pending': return { label: 'ждёт ревью', tone: 'muted' };
    default: return { label: '', tone: 'muted' };
  }
}

// Чип модели шага: исполнитель стадии с тем же stepRef (задача-шаг, не доработка);
// совпадения нет → null (чипа нет, честная деградация).
export function stepModel(executors: FeatureStageExecutor[], stepRef: string):
  { label: string; full?: string | null } | null {
  if (!stepRef) return null;
  const head = executors.find(x => x.stepRef === stepRef && x.taskRole === 'step');
  const any = head ?? executors.find(x => x.stepRef === stepRef);
  if (!any || !any.modelLabel) return null;
  return { label: any.modelLabel, full: any.modelFull };
}

// === W4-3F: строка «оценивает →» ===

// Индекс исполнителей по taskId: единственный источник статуса для чипов «оценивает →» —
// stages[].executors[] (бэк W4-3B уже отфильтровал связь по задачам этого run). Сырой
// FeatureTask.reviewOf не используется: он не фильтрован и не является источником правды.
export function buildExecutorIndex(stages: FeatureStage[]): Map<string, FeatureStageExecutor> {
  const idx = new Map<string, FeatureStageExecutor>();
  for (const s of stages) {
    for (const x of s.executors) {
      if (x.taskId) idx.set(x.taskId, x);
    }
  }
  return idx;
}

// Оцениваемая задача: заголовок из taskMap, статус — из исполнителя задачи в
// stages[].executors[]; исполнителя нет (например, эпик) — статус не выдумывается.
export interface EvalTarget {
  taskId: string;
  title: string;
  statusLabel: string;
  tone: 'muted' | 'info' | 'danger' | 'success';
}

// «N связей не найдено» — с правильным склонением числа (нейтрально: не «вне фичи»).
export function outsideLinksLabel(n: number): string {
  if (n <= 0) return '';
  if (n === 1) return '1 связь не найдена';
  if (n <= 4) return `${n} связи не найдены`;
  return `${n} связей не найдено`;
}

// Строка «оценивает →» исполнителя. Ролевая граница (вариант 3А):
// • review/qa — чипы оцениваемых задач (только эти роли оценивают конкретные задачи;
//   у rework ReviewOf означает «откуда вернулась», а не «что оценивает» — чипов нет);
// • final — «фичу целиком: N из M принято» из stepProgress (прогресса нет — строки нет);
// • plan — «по разрезу», cut — «по эпику» (эти роли оценируют целое, а не задачи);
// • остальные роли — null.
export interface EvaluatedLine {
  targets: EvalTarget[];
  outside: number;
  scopeLabel: string | null;
}
export function evaluatedLine(
  x: FeatureStageExecutor,
  taskMap: Map<string, FeatureTask>,
  execIndex: Map<string, FeatureStageExecutor>,
  progress: StepProgress | null,
  now: number,
): EvaluatedLine | null {
  if (x.taskRole === 'review' || x.taskRole === 'qa') {
    const targets = (x.reviewOf ?? []).map((id) => {
      const ex = execIndex.get(id);
      const task = taskMap.get(id);
      const st = ex ? executorStatus(ex, task, now) : null;
      return {
        taskId: id,
        title: task?.title ?? id,
        statusLabel: st ? st.label : '',
        tone: st ? st.tone : 'muted',
      };
    });
    const outside = x.reviewOfOutside ?? 0;
    if (!targets.length && !outside) return null;
    return { targets, outside, scopeLabel: null };
  }
  if (x.taskRole === 'final') {
    if (!progress) return null;
    return { targets: [], outside: 0, scopeLabel: `фичу целиком: ${progress.accepted} из ${progress.total} принято` };
  }
  if (x.taskRole === 'plan') return { targets: [], outside: 0, scopeLabel: 'по разрезу' };
  if (x.taskRole === 'cut') return { targets: [], outside: 0, scopeLabel: 'по эпику' };
  return null;
}

// === Подкомпоненты ===

// Чип модели (макет §5/§7): mono 10, «Opus»/«Sonnet»/«Qwen»/«Авто»; «Авто» раскрывается —
// подсказка с полной моделью (modelFull). Подсказка — по наведению, фокусу и тапу (Tooltip).
// Доступность (ревью фронта 1, minor 3): tabIndex=0 + role="button" — чип фокусируется
// с клавиатуры, подсказка показывается по фокусу (не только по тапу на 360).
export function ModelChip({ label, full, isMobile = false }: { label: string; full?: string | null; isMobile?: boolean }) {
  const [tapped, setTapped] = useState(false);
  const [focused, setFocused] = useState(false);
  const tip = full ? (label === 'Авто' ? `Модель: ${full}` : full) : null;
  // Активация: клик (десктоп и мобайл) + Enter/Space (role="button" обязана реагировать
  // на клавиатуру, не только на фокус). Тап/нажатие переключает подсказку.
  const activate = () => setTapped(v => !v);
  const chip = (
    <span
      data-testid="timeline-model-chip"
      role="button"
      tabIndex={0}
      onClick={activate}
      onKeyDown={e => {
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); activate(); }
      }}
      onFocus={() => setFocused(true)}
      onBlur={() => setFocused(false)}
      style={{
        fontFamily: FONT.mono, fontSize: FS.xxs, fontWeight: 700,
        color: C.textSecondary, background: C.bgSelected,
        borderRadius: R.sm, padding: '0 4px', whiteSpace: 'nowrap', flexShrink: 0,
        minHeight: isMobile ? 40 : undefined,
        display: 'inline-flex', alignItems: 'center',
        cursor: 'pointer',
      }}
    >
      {label}
    </span>
  );
  if (!tip) return chip;
  return (
    <Tooltip content={tip} focused={tapped || focused}>
      {chip}
    </Tooltip>
  );
}

// Аватар исполнителя (18): персона по personaId; не найдена — нейтральный круг (без инициалов).
// Экспортируется: тот же аватар рисуют карточки «Производства» (FeatureProdCardsView),
// вкладки «Чаты»/«Задачи»/«События» и ExecutorRow здесь.
export function ExecutorAvatar({ personaId }: { personaId: string | null }) {
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
// статус (идёт/ждёт вас/стоит/готово) · время · пара «чат · задача» (NavTargetPair).
// chatId == null → «чата нет» (без мёртвой ссылки); чат в архиве → «· архив».
// Навигация (Ф9a): чат — openChatById, задача — openTaskInSection; точка возврата
// «страница фичи → чат/задача» — recordFeatureReturn (чип возврата — Ф9b).
export function ExecutorRow({ x, task, now, featureId, featureTitle = null, marks = EMPTY_NAV_MARKS, isMobile = false,
  taskMap = new Map<string, FeatureTask>(), execIndex = new Map<string, FeatureStageExecutor>(), progress = null }: {
  x: FeatureStageExecutor; task?: FeatureTask; now: number; featureId: string; featureTitle?: string | null; marks?: NavMarks; isMobile?: boolean;
  taskMap?: Map<string, FeatureTask>; execIndex?: Map<string, FeatureStageExecutor>; progress?: StepProgress | null;
}) {
  const persona = x.personaId ? getPersonaById(x.personaId) : undefined;
  // Персона в данных, но не найдена (недоступна) — нейтральное «персона недоступна»,
  // не сырой UUID; без personaId — прочерк.
  const name = persona?.name ?? (x.personaId ? 'персона недоступна' : '—');
  const role = roleLabel(x.taskRole, x.stepRef, x.lane);
  const st = executorStatus(x, task, now);
  const time = st.minutes != null ? `${st.minutes} мин` : null;
  // W4-3F: строка «оценивает →» (чипы только у review/qa; финал — по всей фиче; план/разрез —
  // «по разрезу»/«по эпику»). Связь — только из stages[].executors[], сырой reviewOf не берём.
  const ev = evaluatedLine(x, taskMap, execIndex, progress, now);

  const statusColor =
    st.tone === 'info' ? C.info :
    st.tone === 'danger' ? C.dangerText :
    st.tone === 'success' ? C.successText :
    C.textMuted;
  const toneColor = (t: 'muted' | 'info' | 'danger' | 'success') =>
    t === 'info' ? C.info : t === 'danger' ? C.dangerText : t === 'success' ? C.successText : C.textMuted;

  // Переход в чат исполнителя: openChatById (тот же путь, что плашка FeatureBanner)
  // + точка возврата на страницу фичи (Ф9b).
  const openChat = () => {
    if (!x.chatId) return;
    recordFeatureReturn(featureId, { kind: 'chat', id: x.chatId }, featureTitle);
    void openChatById(x.chatId);
  };
  // Переход в задачу исполнителя: openTaskInSection (как из карточки «Шаг N фичи»)
  // + точка возврата.
  const openTask = () => {
    recordFeatureReturn(featureId, { kind: 'task', id: x.taskId }, featureTitle);
    openTaskInSection({ id: x.taskId, projectId: task?.projectId ?? undefined });
  };

  return (
    <div
      data-testid="timeline-executor"
      style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap',
        padding: `${SP.xxs}px 0`, fontFamily: FONT.sans,
      }}
    >
      <ExecutorAvatar personaId={x.personaId} />
      <span style={{ fontSize: FS.sm, color: C.textPrimary, whiteSpace: 'nowrap' }}>{name}</span>
      {role && (
        <span
          data-testid="timeline-executor-role"
          style={{
            fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary,
            background: C.bgSelected, borderRadius: R.sm, padding: `0 ${SP.xs}px`,
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
      {ev && (
        <span
          data-testid="timeline-eval-line"
          style={{ display: 'inline-flex', gap: SP.xs, alignItems: 'center', flexWrap: 'wrap' }}
        >
          <span style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap' }}>оценивает →</span>
          {ev.targets.map((t) => (
            <span
              key={t.taskId}
              data-testid="timeline-eval-target"
              style={{
                fontSize: FS.xs, color: C.textSecondary, background: C.bgSelected,
                borderRadius: R.sm, padding: `0 ${SP.xs}px`, whiteSpace: 'nowrap', flexShrink: 0,
              }}
            >
              {t.title}
              {t.statusLabel && (
                <span style={{ color: toneColor(t.tone), fontWeight: 600 }}> · {t.statusLabel}</span>
              )}
            </span>
          ))}
          {ev.outside > 0 && (
            <span data-testid="timeline-eval-outside" style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap' }}>
              {outsideLinksLabel(ev.outside)}
            </span>
          )}
          {ev.scopeLabel && (
            <span data-testid="timeline-eval-scope" style={{ fontSize: FS.xs, color: C.textSecondary, whiteSpace: 'nowrap' }}>
              {ev.scopeLabel}
            </span>
          )}
        </span>
      )}
      <span style={{ marginLeft: 'auto', display: 'inline-flex', gap: SP.xs }}>
        <NavTargetPair
          chatId={x.chatId}
          // minor 3: ОДИН источник «в архиве» — marks.chatArchived (unified), не x.chatArchived.
          chatArchived={marks.chatArchived(x.chatId)}
          onOpenChat={openChat}
          chatTitle="Открыть чат. Страница фичи скроется, вернётся по плашке."
          showNoChat
          taskId={x.taskId}
          // «Закрыта» — один источник: статус ЗАДАЧИ (task.status === 'done'),
          // не статус исполнителя (x.status) — разные поля бэка.
          taskClosed={task?.status === 'done'}
          onOpenTask={openTask}
          taskTitle="Открыть задачу. Страница фичи скроется, вернётся по плашке."
          isMobile={isMobile}
        />
      </span>
    </div>
  );
}


