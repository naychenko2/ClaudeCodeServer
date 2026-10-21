import { useState } from 'react';
import type { CSSProperties } from 'react';
import { ChevronRight } from 'lucide-react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { STAGE_NAME } from '../../lib/chatGroups';
import { roleLabel } from '../../lib/roleLabel';
import { getPersonaById } from '../../lib/personas';
import { dateOnly, hhmm } from '../chat/feature/featureTime';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { Tooltip } from '../ui/Tooltip';
import { openChatById } from '../../lib/openChat';
import { openTaskInSection } from '../../lib/tasks';
import { recordFeatureReturn } from '../../lib/featureBackNav';
import { NavTargetPair } from './NavTargetButton';
import type { FeatureRun, FeatureStage, FeatureStageExecutor, FeatureStepChain, FeatureRound, FeatureRoundVerdict, FeatureTask, StageKey } from '../../types/featureRun';

// Подкомпоненты вкладки «Хронология» (волна 3, Ф2a/Ф2b, макет features-overview-3-final §5):
// точка/линия этапа, подпись состояния, границы «dd.mm · ЧЧ:ММ – ЧЧ:ММ», длительность «≈»,
// исполнители (аватар, роль, чип модели, статус, время, пара «чат · задача»), плашки
// «ждёт вас · N мин» / «стоит · N мин»; Ф2b — шаги (шапка: код, роль, исполнитель, чип модели,
// статус, «чат · задача») и витки (ревью: кто, вердикт, находки; «чат · задача» ревью).
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

// === Ф2b: шаги и витки (макет §5: строки шагов и витки) ===

// Вердикт витка → подпись + тон (макет: «принят» — success, «вернул» — warning,
// «идёт» — active, «снят» — muted). «Принят с замечаниями» считается возвратом (warning).
export interface RoundVerdictView {
  label: string;
  tone: 'success' | 'warning' | 'active' | 'muted';
}
export function roundVerdictView(verdict: FeatureRoundVerdict): RoundVerdictView {
  switch (verdict) {
    case 'accepted': return { label: 'принят', tone: 'success' };
    case 'accepted_with_remarks': return { label: 'принят с замечаниями', tone: 'warning' };
    case 'returned': return { label: 'вернул', tone: 'warning' };
    case 'withdrawn': return { label: 'снят', tone: 'muted' };
    case 'running': return { label: 'идёт', tone: 'active' };
    default: return { label: '', tone: 'muted' };
  }
}

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

// Подпись статуса шага (обёртка stepStatusView: label).
export function stepStatusLabel(chain: FeatureStepChain): string {
  return stepStatusView(chain).label;
}

// Тон статуса шага (обёртка stepStatusView: tone).
export function stepStatusTone(chain: FeatureStepChain): 'success' | 'active' | 'muted' {
  return stepStatusView(chain).tone;
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

// Пустое состояние зоны витков (честная деградация, без выдуманных подписей):
// витков нет + нет исполнителя → «нет исполнителя»; витков нет → «ещё не ревьюили»;
// последний виток идёт → «ревью идёт»; иначе null (витки рендерятся строками).
export function roundsAreaLabel(chain: FeatureStepChain): string | null {
  if (chain.rounds.length === 0) {
    return chain.personaId ? 'ещё не ревьюили' : 'нет исполнителя';
  }
  const last = chain.rounds[chain.rounds.length - 1]!;
  return last.verdict === 'running' ? 'ревью идёт' : null;
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
// Экспортируется: Ф2b — строка шага рисует тот же аватар.
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
export function ExecutorRow({ x, task, now, featureId, featureTitle = null, isMobile = false }: {
  x: FeatureStageExecutor; task?: FeatureTask; now: number; featureId: string; featureTitle?: string | null; isMobile?: boolean;
}) {
  const persona = x.personaId ? getPersonaById(x.personaId) : undefined;
  // Персона в данных, но не найдена (недоступна) — нейтральное «персона недоступна»,
  // не сырой UUID; без personaId — прочерк.
  const name = persona?.name ?? (x.personaId ? 'персона недоступна' : '—');
  const role = roleLabel(x.taskRole, x.stepRef, x.lane);
  const st = executorStatus(x, task, now);
  const time = st.minutes != null ? `${st.minutes} мин` : null;

  const statusColor =
    st.tone === 'info' ? C.info :
    st.tone === 'danger' ? C.dangerText :
    st.tone === 'success' ? C.successText :
    C.textMuted;

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
      <span style={{ marginLeft: 'auto', display: 'inline-flex', gap: SP.xs }}>
        <NavTargetPair
          chatId={x.chatId}
          chatArchived={x.chatArchived}
          onOpenChat={openChat}
          chatTitle="Открыть чат. Страница фичи скроется, вернётся по плашке."
          showNoChat
          taskId={x.taskId}
          // «Закрыта» — ОДИН источник с StepRow: статус ЗАДАЧИ (task.status === 'done'),
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

// === Ф2b: витки и шаги (макет §5) ===

// Строка витка (макет §5): «виток N» · ревью (кто, вердикт, находки, если есть в данных) ·
// пара «чат · задача» ревью (NavTargetPair: чат = reviewChatId, задача = reviewTaskId).
// Вердикт-чип красится по исходу; находки — Verification.Notes (reason), перенос по словам.
export function RoundRow({ round, featureId, featureTitle = null, isMobile = false }: {
  round: FeatureRound; featureId: string; featureTitle?: string | null; isMobile?: boolean;
}) {
  const persona = round.personaId ? getPersonaById(round.personaId) : undefined;
  // Персона недоступна (id в данных, но не найдена) — нейтральное «персона недоступна»,
  // не сырой UUID; без personaId — имени нет.
  const name = persona?.name ?? (round.personaId ? 'персона недоступна' : null);
  const verdict = roundVerdictView(round.verdict);
  const vStyle: CSSProperties =
    verdict.tone === 'success' ? { background: C.successBg, color: C.successText } :
    verdict.tone === 'warning' ? { background: C.warningBg, color: C.warningText } :
    verdict.tone === 'active' ? { background: C.bgSelected, color: C.textHeading } :
    { background: 'transparent', color: C.textMuted, boxShadow: `inset 0 0 0 1px ${C.border}` };

  // Переход в чат/задачу витка: те же пути, что у исполнителя (openChatById / openTaskInSection)
  // + точка возврата на страницу фичи (Ф9b).
  const openChat = () => {
    if (!round.reviewChatId) return;
    recordFeatureReturn(featureId, { kind: 'chat', id: round.reviewChatId }, featureTitle);
    void openChatById(round.reviewChatId);
  };
  const openTask = () => {
    if (!round.reviewTaskId) return;
    recordFeatureReturn(featureId, { kind: 'task', id: round.reviewTaskId }, featureTitle);
    openTaskInSection({ id: round.reviewTaskId });
  };

  return (
    <div
      data-testid={`feature-round-${round.n}`}
      style={{
        display: 'grid', gridTemplateColumns: '54px minmax(0, 1fr) auto',
        gap: SP.sm, alignItems: 'baseline', padding: `${SP.xs}px 0`,
        fontFamily: FONT.sans, fontSize: FS.sm, color: C.textSecondary,
      }}
    >
      <span data-testid="feature-round-n" style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap' }}>
        виток {round.n}
      </span>
      <span style={{ display: 'inline-flex', alignItems: 'baseline', gap: SP.sm, flexWrap: 'wrap', minWidth: 0 }}>
        {name && (
          <span data-testid="feature-round-who" style={{ color: C.textPrimary, whiteSpace: 'nowrap' }}>{name}</span>
        )}
        <span
          data-testid="feature-round-verdict"
          style={{ ...vStyle, fontSize: FS.xxs, fontWeight: 700, padding: `0 ${SP.xs}px`, borderRadius: R.sm, whiteSpace: 'nowrap', lineHeight: '15px' }}
        >
          {verdict.label}
        </span>
        {round.reason && (
          <span data-testid="feature-round-reason" style={{ minWidth: 0, overflowWrap: 'anywhere' }}>
            {round.reason}
          </span>
        )}
      </span>
      <span style={{ display: 'inline-flex', gap: SP.xs }}>
        <NavTargetPair
          chatId={round.reviewChatId}
          onOpenChat={openChat}
          chatTitle="Открыть чат. Страница фичи скроется, вернётся по плашке."
          showNoChat
          taskId={round.reviewTaskId ?? null}
          onOpenTask={openTask}
          taskTitle="Открыть задачу. Страница фичи скроется, вернётся по плашке."
          isMobile={isMobile}
        />
      </span>
    </div>
  );
}

// Строка шага (макет §5): кнопка — шеврон, код (stepRef), название; справа — исполнитель
// (аватар + имя), roleLabel, чип модели, статус, пара «чат · задача». Раскрыто — цепочка
// витков (RoundRow) + строка «итого». Один шаг раскрыт за раз (состояние — у родителя).
// Честная деградация: без stepRef кода нет; без исполнителя — «нет исполнителя»;
// без чата — «чата нет» (NavTargetPair); без модели — чипа нет.
export function StepRow({ chain, model, expanded, onToggle, featureId, featureTitle = null, task, isMobile = false }: {
  chain: FeatureStepChain;
  model: { label: string; full?: string | null } | null;
  expanded: boolean;
  onToggle: () => void;
  featureId: string;
  featureTitle?: string | null;
  // Задача шага (первая в цепочке): для пометки «закрыта» (один источник с
  // ExecutorRow: status === 'done', не outcome === 'accepted' — разные поля бэка).
  task?: import('../../types/featureRun').FeatureTask;
  isMobile?: boolean;
}) {
  const persona = chain.personaId ? getPersonaById(chain.personaId) : undefined;
  // Персона недоступна (id в данных, но не найдена) — нейтральное «персона недоступна»,
  // не сырой UUID; без personaId — имени нет.
  const name = persona?.name ?? (chain.personaId ? 'персона недоступна' : null);
  const role = roleLabel('step', chain.stepRef || null, chain.lane ?? null);
  // Статус шага — ОДИН источник: label и tone из stepStatusView (не расходятся).
  const { label: statusLabel, tone } = stepStatusView(chain);
  const duration = stageDurationLabel(chain.durationMinutes);
  const areaLabel = roundsAreaLabel(chain);

  const statusStyle: CSSProperties =
    tone === 'success' ? { background: C.successBg, color: C.successText } :
    tone === 'active' ? { background: C.bgSelected, color: C.textHeading } :
    { background: 'transparent', color: C.textMuted, boxShadow: `inset 0 0 0 1px ${C.border}` };

  // Переход в чат/задачу шага: чат = chain.chatId (fail-closed), задача = первая задача
  // цепочки (задача-шаг); те же пути, что у исполнителя, + точка возврата.
  const openChat = () => {
    if (!chain.chatId) return;
    recordFeatureReturn(featureId, { kind: 'chat', id: chain.chatId }, featureTitle);
    void openChatById(chain.chatId);
  };
  const openTask = () => {
    const taskId = chain.taskIds[0];
    if (!taskId) return;
    recordFeatureReturn(featureId, { kind: 'task', id: taskId }, featureTitle);
    openTaskInSection({ id: taskId });
  };

  // Строка-кнопка (макет §5, .srow): кнопка на всю ширину + пара «чат · задача» справа
  // в той же строке; hover — фон строки (как в макете).
  const [hover, setHover] = useState(false);
  return (
    <div data-testid={`feature-step-${chain.stepRef || 'no-ref'}`}>
      <div
        onMouseEnter={() => setHover(true)}
        onMouseLeave={() => setHover(false)}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.xs,
          margin: `0 -${SP.md}px`, padding: `0 ${SP.md}px`,
          background: hover ? C.bgSelected : 'transparent',
          borderRadius: R.md,
        }}
      >
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={expanded}
        aria-label={`${chain.stepRef ? chain.stepRef + ' ' : ''}${chain.title}: ${expanded ? 'свернуть' : 'раскрыть'} витки`}
        data-testid={`feature-step-header-${chain.stepRef || 'no-ref'}`}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, flex: 1, minWidth: 0,
          background: 'transparent', border: 'none', cursor: 'pointer',
          padding: `${SP.xs}px ${SP.sm}px`, borderRadius: R.md, minHeight: isMobile ? 44 : 36,
          fontFamily: FONT.sans, fontSize: FS.sm, color: C.textPrimary, textAlign: 'left',
        }}
      >
        <ChevronRight
          size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} aria-hidden
          style={{
            flexShrink: 0, color: C.textMuted,
            transform: expanded ? 'rotate(90deg)' : undefined, transition: 'transform .15s',
          }}
        />
        {chain.stepRef && (
          <span data-testid="feature-step-code" style={{ fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700, color: C.textHeading, flexShrink: 0 }}>
            {chain.stepRef}
          </span>
        )}
        <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {chain.title}
        </span>
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 0 }}>
          <ExecutorAvatar personaId={chain.personaId ?? null} />
          {name && (
            <span data-testid="feature-step-executor" style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap' }}>
              {name}
            </span>
          )}
          {role && (
            <span
              data-testid="feature-step-role"
              style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary, background: C.bgSelected, borderRadius: R.sm, padding: `0 ${SP.xs}px`, whiteSpace: 'nowrap' }}
            >
              {role}
            </span>
          )}
          {model && <ModelChip label={model.label} full={model.full} isMobile={isMobile} />}
          {statusLabel && (
            <span
              data-testid="feature-step-status"
              style={{ ...statusStyle, fontSize: FS.xs, fontWeight: 600, padding: `0 ${SP.xs}px`, borderRadius: R.sm, whiteSpace: 'nowrap', lineHeight: '16px' }}
            >
              {statusLabel}
            </span>
          )}
        </span>
      </button>
      {/* «чат · задача» шага (в той же строке, макет §5): чат = chain.chatId (fail-closed),
          задача = задача-шаг (первая задача цепочки) */}
      <NavTargetPair
        chatId={chain.chatId}
        onOpenChat={openChat}
        chatTitle="Открыть чат. Страница фичи скроется, вернётся по плашке."
        showNoChat
        taskId={chain.taskIds[0] ?? null}
        // «Закрыта» — ОДИН источник с ExecutorRow: задача шага done (status === 'done'),
        // не outcome === 'accepted' (другое поле бэка: «принят» — вердикт ревью, не
        // статус задачи). «Идёт»/«ждёт ревью»/«вернул» — задача ещё открыта.
        taskClosed={task?.status === 'done'}
        onOpenTask={openTask}
        taskTitle="Открыть задачу. Страница фичи скроется, вернётся по плашке."
        isMobile={isMobile}
      />
      </div>
      {/* Раскрыто: витки (или пустое состояние) + «итого» */}
      {expanded && (
        <div
          data-testid={`feature-step-tours-${chain.stepRef || 'no-ref'}`}
          style={{ margin: `${SP.xxs}px 0 ${SP.sm}px ${SP.xl}px`, borderLeft: `1px solid ${C.divider}`, paddingLeft: SP.md }}
        >
          {chain.rounds.length === 0 ? (
            <div
              data-testid="feature-step-rounds-empty"
              style={{ fontSize: FS.sm, color: C.textMuted, padding: `${SP.xs}px 0`, fontFamily: FONT.sans }}
            >
              {areaLabel}
            </div>
          ) : (
            <>
              {areaLabel && (
                <div
                  data-testid="feature-step-review-running"
                  style={{ fontSize: FS.xs, fontWeight: 600, color: C.info, padding: `${SP.xs}px 0`, fontFamily: FONT.sans }}
                >
                  {areaLabel}
                </div>
              )}
              {chain.rounds.map(r => (
                <RoundRow key={`${chain.stepRef}-${r.n}`} round={r} featureId={featureId} featureTitle={featureTitle} isMobile={isMobile} />
              ))}
              <div
                data-testid="feature-step-total"
                style={{ display: 'grid', gridTemplateColumns: '54px minmax(0, 1fr)', gap: SP.sm, alignItems: 'baseline', padding: `${SP.xs}px 0`, fontFamily: FONT.sans, fontSize: FS.sm, color: C.textSecondary }}
              >
                <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>итого</span>
                <span style={{ minWidth: 0 }}>
                  {name ?? '—'}{duration ? ` · ${duration}` : ' · —'}
                </span>
              </div>
            </>
          )}
        </div>
      )}
    </div>
  );
}
