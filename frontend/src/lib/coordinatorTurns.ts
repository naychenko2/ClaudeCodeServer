// Группировка хода координатора чата-штаба («Командная реализация») в один блок ленты.
// Координатор — не сабагент, а сам чат, поэтому его работа падает в ленту россыпью:
// плашка ⚑ staffNote, голые tool_use, текст, result. Модель ниже собирает из этой
// россыпи группу под карточку той же формы, что у персоны-консультанта
// (PersonaConsultCard): шапка с фазой, вложенная активность, ответ, метрики.
//
// Приём — как у errorGroups в ChatPanel: лента НЕ схлопывается, индексы items
// сохраняются. Возвращаем карту «индекс якоря → группа» и набор погашенных индексов;
// рендер рисует карточку на месте якоря и пропускает погашенные.
//
// ИНВАРИАНТ (жёсткий): элементы, требующие внимания человека — карточка эскалации
// (team_escalation), карточка плана (team_plan), вопрос человеку (ask_question),
// запрос разрешения (permission_request), ревью плана (plan_review) и ошибки —
// НИКОГДА не попадают внутрь группы. Такой элемент завершает группу и остаётся
// самостоятельным элементом ленты; остаток хода даёт новую группу. Иначе вопрос
// человеку утонул бы в свёрнутом блоке, а авто-волны встали бы молча.

import type { ChatItem, SessionTeamImplement } from '../types';
import { toolWord } from '../components/chat/ToolUseView';
import { formatTailDuration } from './agentTail';
import { parseDelegationReport } from './delegationReport';

export interface CoordinatorActivityEntry { item: ChatItem; idx: number }

export interface CoordinatorTurnGroup {
  // Якорь: индекс, на месте которого рисуется карточка (служебный триггер хода либо
  // первый элемент остатка, если ход разрезан карточкой для человека)
  startIdx: number;
  // Последний элемент группы включительно (все индексы между ними — погашены)
  endIdx: number;
  statusRunning: string;   // «Разбирает доклады волны 2» — строка состояния в шапке
  statusDone: string;      // «разобрал доклады волны 2» — для свёрнутого вида
  activity: CoordinatorActivityEntry[];
  // Ответ карточки — последний текст координатора верхнего уровня (сводка волны);
  // из activity он изъят, чтобы не дублироваться
  answerIdx: number | null;
  running: boolean;
  isError: boolean;
  aborted: boolean;         // ход оборван человеком («Стоп»)
  metrics: { tools: number; durationMs?: number; tokens?: number };
}

export interface CoordinatorTurns {
  at: Map<number, CoordinatorTurnGroup>;
  suppressed: Set<number>;
}

export interface CoordinatorPhaseLabel {
  running: string;   // настоящее время, шапка карточки
  done: string;      // прошедшее время со строчной, свёрнутый вид
}

// === Фазы ===
// Опора — фактические staffNote бэкенда: TeamWaveService (закрытие волны),
// SessionManager (ответ на карточку, возврат в интервью), TaskExecutionService
// (доклад исполнителя постановщику).

const WAVE_CLOSED_RE = /^Волна\s+(\d+)\s+закрыта/;
export const STAFF_NOTE_CARD_ANSWER = 'Ответ на карточку передан координатору';
export const STAFF_NOTE_BACK_TO_INTERVIEW = 'Возврат в интервью — координатор задаст вопросы';
export const STAFF_NOTE_TASK_REPORT = 'Доклад по задаче передан постановщику';

export const COORDINATOR_PHASE_CARD: CoordinatorPhaseLabel =
  { running: 'Учитывает ваш ответ', done: 'учёл ответ' };
export const COORDINATOR_PHASE_INTERVIEW: CoordinatorPhaseLabel =
  { running: 'Готовит вопросы', done: 'подготовил вопросы' };
export const COORDINATOR_PHASE_REPORT: CoordinatorPhaseLabel =
  { running: 'Разбирает доклад исполнителя', done: 'разобрал доклад исполнителя' };
export const COORDINATOR_PHASE_ASSIGN: CoordinatorPhaseLabel =
  { running: 'Ставит задачи исполнителям', done: 'поставил задачи исполнителям' };
export const COORDINATOR_PHASE_REISSUE: CoordinatorPhaseLabel =
  { running: 'Перевыдаёт задачу', done: 'перевыдал задачу' };

export function coordinatorWavePhase(wave: number): CoordinatorPhaseLabel {
  return { running: `Разбирает доклады волны ${wave}`, done: `разобрал доклады волны ${wave}` };
}

// Прошедшая форма для строки «Координатор · … · …»: со строчной буквы и без хвостовой
// пунктуации — точка внутри строки, разделённой точками, читается как опечатка.
function lowerFirst(text: string): string {
  const trimmed = text.replace(/[.!;…]+$/, '');
  return trimmed.charAt(0).toLowerCase() + trimmed.slice(1);
}

function upperFirst(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1);
}

// Подпись фазы по заметке штаба. Незнакомая заметка — не выдумываем формулировку, а
// показываем её текст как есть (в прошедшей форме — со строчной буквы, чтобы строка
// «Координатор · …» читалась одним предложением).
export function coordinatorPhaseFromNote(note: string | null | undefined): CoordinatorPhaseLabel {
  const text = (note ?? '').trim();
  if (!text) return COORDINATOR_PHASE_REPORT;
  const wave = text.match(WAVE_CLOSED_RE);
  if (wave) return coordinatorWavePhase(Number(wave[1]));
  if (text === STAFF_NOTE_CARD_ANSWER) return COORDINATOR_PHASE_CARD;
  if (text === STAFF_NOTE_BACK_TO_INTERVIEW) return COORDINATOR_PHASE_INTERVIEW;
  if (text === STAFF_NOTE_TASK_REPORT) return COORDINATOR_PHASE_REPORT;
  return { running: text, done: lowerFirst(text) };
}

// Имя MCP-инструмента без префикса сервера: «mcp__tasks__tasks_create» → «tasks_create»
function bareToolName(name: string): string {
  const parts = name.split('__');
  return parts[parts.length - 1];
}

const ASSIGN_TOOLS = new Set(['tasks_create', 'tasks_run_executor']);

// Уточнение фазы по содержимому хода: координатор раздал задачи — это виднее из
// вызовов, чем из заметки штаба (по заметке ход выглядел бы «разбирает доклады»).
// repeatRun — повторный запуск исполнителя по той же задаче (перевыдача).
export function coordinatorPhaseFromTools(toolNames: string[], repeatRun: boolean): CoordinatorPhaseLabel | null {
  const bare = toolNames.map(bareToolName);
  if (repeatRun && bare.includes('tasks_run_executor')) return COORDINATOR_PHASE_REISSUE;
  return bare.some(n => ASSIGN_TOOLS.has(n)) ? COORDINATOR_PHASE_ASSIGN : null;
}

// === Границы группы ===

// Элементы, требующие внимания человека: внутрь карточки не попадают никогда
const ATTENTION_KINDS: ReadonlySet<ChatItem['kind']> = new Set<ChatItem['kind']>([
  'team_escalation', 'team_plan', 'ask_question', 'permission_request', 'plan_review',
  'error', 'error_group',
]);

export function isCoordinatorAttentionItem(item: ChatItem): boolean {
  return ATTENTION_KINDS.has(item.kind);
}

// Содержимое хода координатора. Всё остальное (session_started, смена модели, маркеры
// механик…) группу завершает — прятать незнакомый элемент внутрь карточки нельзя:
// в ленте он просто исчез бы.
const CONTENT_KINDS: ReadonlySet<ChatItem['kind']> = new Set<ChatItem['kind']>([
  'text', 'thinking', 'redacted_thinking', 'tool_use', 'file_changed', 'result',
]);

type TriggerItem = Extract<ChatItem, { kind: 'user_message' }>;

// Служебный триггер хода координатора: заметка штаба (⚑) либо доклад делегированной
// задачи. Ход, начатый ЖИВЫМ сообщением человека, не группируется — это обычный
// разговор в ленте, и прятать его в блок «работа координатора» неправильно.
export function isCoordinatorTrigger(item: ChatItem): item is TriggerItem {
  if (item.kind !== 'user_message') return false;
  if (item.staffNote) return true;
  return !!item.delegationTaskId || parseDelegationReport(item.text) !== null;
}

// Повторный запуск исполнителя: taskId уже встречался в более раннем tasks_run_executor.
// Считаем по всей ленте разом — внутри группы этого знания нет.
function repeatRunIds(items: ChatItem[]): Set<string> {
  const seen = new Set<string>();
  const repeats = new Set<string>();
  for (const it of items) {
    if (it.kind !== 'tool_use' || bareToolName(it.name) !== 'tasks_run_executor') continue;
    const taskId = (it.input as { taskId?: unknown } | null | undefined)?.taskId;
    if (typeof taskId !== 'string' || !taskId) continue;
    if (seen.has(taskId)) repeats.add(it.id); else seen.add(taskId);
  }
  return repeats;
}

export function buildCoordinatorTurns(
  items: ChatItem[],
  teamState: SessionTeamImplement | null | undefined,
): CoordinatorTurns {
  const at = new Map<number, CoordinatorTurnGroup>();
  const suppressed = new Set<number>();
  if (!teamState) return { at, suppressed };

  const repeats = repeatRunIds(items);

  let i = 0;
  while (i < items.length) {
    const trigger = items[i];
    if (!isCoordinatorTrigger(trigger)) { i++; continue; }
    const notePhase = coordinatorPhaseFromNote(trigger.staffNote);
    // Якорь первой группы хода — сам триггер: плашка ⚑ становится строкой состояния
    // в шапке карточки и отдельной строкой в ленте больше не живёт
    let anchor: number | null = i;
    let j = i + 1;
    for (;;) {
      const entries: CoordinatorActivityEntry[] = [];
      let resultIdx: number | null = null;
      let stopped: 'result' | 'trigger' | 'attention' | 'user' | 'other' | 'end' = 'end';
      while (j < items.length) {
        const it = items[j];
        // Сообщение пользователя содержимым хода не бывает, поэтому разбор «чей это
        // элемент» идёт после проверки на содержимое: служебный триггер отдаём внешнему
        // циклу (следующая группа), живое сообщение человека завершает группировку
        if (!CONTENT_KINDS.has(it.kind)) {
          stopped = isCoordinatorAttentionItem(it) ? 'attention'
            : it.kind === 'user_message' ? (isCoordinatorTrigger(it) ? 'trigger' : 'user')
              : 'other';
          break;
        }
        if (it.kind === 'result') { resultIdx = j; j++; stopped = 'result'; break; }
        entries.push({ item: it, idx: j }); j++;
      }
      const hasContent = entries.length > 0 || resultIdx !== null;
      // Группу без содержимого делаем только на триггере (ход уже начался, элементов
      // ещё нет — карточка живёт в состоянии «работает»); остаток без содержимого
      // карточки не заслуживает
      if (anchor !== null || hasContent) {
        const startIdx = anchor ?? (entries.length > 0 ? entries[0].idx : resultIdx!);
        const group = buildGroup({
          items, startIdx, entries, resultIdx, phase: notePhase,
          continuation: anchor === null, repeats,
        });
        at.set(startIdx, group);
        for (let k = startIdx + 1; k <= group.endIdx; k++) suppressed.add(k);
      }
      if (stopped !== 'attention' && stopped !== 'other') break;
      // Прерыватель остаётся в ленте сам по себе, ход продолжается новой группой
      j++;
      anchor = null;
      if (j >= items.length) break;
    }
    i = Math.max(j, i + 1);
  }
  return { at, suppressed };
}

function buildGroup(args: {
  items: ChatItem[];
  startIdx: number;
  entries: CoordinatorActivityEntry[];
  resultIdx: number | null;
  phase: CoordinatorPhaseLabel;
  continuation: boolean;
  repeats: Set<string>;
}): CoordinatorTurnGroup {
  const { items, startIdx, entries, resultIdx, phase, continuation, repeats } = args;
  const lastEntryIdx = entries.length > 0 ? entries[entries.length - 1].idx : startIdx;
  const endIdx = Math.max(startIdx, lastEntryIdx, resultIdx ?? -1);

  // Ответ карточки — последний текст самого координатора (не текст сабагента)
  let answerIdx: number | null = null;
  for (let k = entries.length - 1; k >= 0; k--) {
    const it = entries[k].item;
    if (it.kind === 'text' && !it.parentToolUseId) { answerIdx = entries[k].idx; break; }
  }
  const activity = answerIdx === null ? entries : entries.filter(e => e.idx !== answerIdx);

  const toolNames = activity.filter(e => e.item.kind === 'tool_use').map(e => (e.item as Extract<ChatItem, { kind: 'tool_use' }>).name);
  const repeatRun = activity.some(e => e.item.kind === 'tool_use' && repeats.has(e.item.id));
  const status = coordinatorPhaseFromTools(toolNames, repeatRun) ?? phase;

  const result = resultIdx !== null ? items[resultIdx] as Extract<ChatItem, { kind: 'result' }> : null;
  // Живой ход: результата ещё нет и группа упирается в конец ленты. Разрезанная
  // карточкой для человека группа живой не считается — работа встала на ней
  const running = result === null && endIdx === items.length - 1;
  const prev = continuation ? items[startIdx - 1] : undefined;
  const isError = (result !== null && result.subtype !== 'success')
    || items[endIdx + 1]?.kind === 'error'
    || prev?.kind === 'error';
  // «Стоп» человека приходит отдельным элементом сразу за группой (в CONTENT_KINDS его
  // нет, поэтому он её и завершает)
  const aborted = items[endIdx + 1]?.kind === 'interrupted';

  // У координатора нет системного хвоста CLI (splitAgentResultTail неприменим):
  // время и токены берём из result хода, действия считаем сами
  const usage = result?.usage;
  const tokens = usage ? usage.inputTokens + usage.outputTokens : undefined;
  return {
    startIdx,
    endIdx,
    statusRunning: status.running,
    statusDone: status.done,
    activity,
    answerIdx,
    running,
    isError,
    aborted,
    metrics: {
      tools: toolNames.length,
      durationMs: result?.durationMs,
      tokens: tokens && tokens > 0 ? tokens : undefined,
    },
  };
}

// Строка состояния в шапке карточки: пока ход идёт — настоящее время, после — прошедшее
// с заглавной («Разобрал доклады волны 2»). Завершённый ход не должен час спустя
// уверять, что он всё ещё разбирает доклады.
export function coordinatorStatusLine(group: CoordinatorTurnGroup): string {
  return group.running ? group.statusRunning : upperFirst(group.statusDone);
}

// Строка свёрнутого вида: «Координатор · разобрал доклады волны 2 · 12с · 4 действия».
// Пока ход идёт — настоящее время («разбирает доклады волны 2»): прошедшее посреди
// работы врало бы. Метрики — от важного к второстепенному; токенов здесь нет совсем,
// на узком экране строка обрывалась бы ровно на них (в развёрнутой карточке они есть).
export function coordinatorCollapsedSummary(group: CoordinatorTurnGroup): string {
  const parts = ['Координатор', group.running ? lowerFirst(group.statusRunning) : group.statusDone];
  if (group.metrics.durationMs != null) parts.push(formatTailDuration(group.metrics.durationMs));
  if (group.metrics.tools > 0) parts.push(`${group.metrics.tools} ${toolWord(group.metrics.tools)}`);
  return parts.join(' · ');
}
