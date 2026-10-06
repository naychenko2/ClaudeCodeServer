// Время выполнения инструмента на карточке ленты: «идёт 0:42» пока работает и итоговая
// длительность после результата. Отметки startedAt/finishedAt ставит сервер своими часами
// (Unix-мс) — поэтому отсчёт после F5 продолжается с того же места.
//
// «Нет результата» везде проверяется через `== null`: в истории сервера у незавершённого
// вызова лежит `"result": null`, и строгое `=== undefined` принимало его за результат —
// после F5 карточка идущего или прерванного инструмента показывала «готово» без времени.

import { isBgLaunchResult } from './agentTail';
import { BUILD_TOOL, RUN_TESTS_TOOL, START_STAND_TOOL, isConsoleTool, toolCardLabel, toolLabel, toolWord } from './toolLabels';
import type { ToolProgress, ToolRunTotals, ToolStage } from '../types';

// Имя инструмента внутри подписи «сейчас …». Русское — со строчной: это середина фразы.
// Незнакомый MCP — в кавычках: его «server · tool» иначе сливался с разделителями подписи
// («сейчас notes · notes_create · 3 действия» читалось как три пункта). Сырое английское имя
// незнакомого инструмента — как есть: «lSP» и «taskOutput» были бы искажением, а не стилем
// Прогон тестов с известным видом — тоже в кавычках: «тесты · vitest»
function inlineToolName(name: string, kind?: string | null): string {
  if (name === RUN_TESTS_TOOL && kind) return `«${inlineToolName(name)} · ${kind}»`;
  const label = toolLabel(name);
  if (name.startsWith('mcp__') && label === name.slice(5).replace(/__/g, ' · ')) return `«${label}»`;
  if (label === name) return label;
  return label.charAt(0).toLowerCase() + label.slice(1);
}

// Подпись живого прогресса (tool_progress) рядом с таймером «идёт M:SS». null — нечего сказать.
// Сабагент: что делает сейчас и сколько действий (его описание и так в шапке карточки);
// имя инструмента — по-русски, как в остальной ленте.
// Локальная генерация: место в очереди либо процент и сколько осталось. Процент по настоящим
// шагам ComfyUI (exact) — без «≈», шаг — в label; оценка по ETA — с «≈»
// meter false — без процента и «осталось»: их показывает полоса прогресса справа от себя
export function toolProgressText(p: ToolProgress | null | undefined, meter = true): string | null {
  if (!p) return null;
  const parts: string[] = [];
  if (p.stage === 'working') {
    if (p.lastTool) parts.push(`сейчас ${inlineToolName(p.lastTool, p.lastToolKind)}`);
    if (typeof p.toolUses === 'number' && p.toolUses > 0) parts.push(`${p.toolUses} ${toolWord(p.toolUses)}`);
  } else {
    if (p.label) parts.push(p.label);
    // Подпись очереди от сервера («ждёт очереди сборок») уже говорит про очередь — общее
    // «в очереди» к ней не дописываем; место в очереди — дописываем всегда, это новая цифра
    if (p.stage === 'queued') {
      if (typeof p.queuePosition === 'number' && p.queuePosition > 0) parts.push(`${p.queuePosition}-я в очереди`);
      else if (!p.label) parts.push('в очереди');
    }
    if (meter && typeof p.percent === 'number') parts.push(`${p.exact ? '' : '≈'}${Math.round(p.percent)}%`);
    if (meter && typeof p.etaSeconds === 'number' && p.etaSeconds > 0) parts.push(`осталось ~${formatClock(p.etaSeconds * 1000)}`);
  }
  return parts.length ? parts.join(' · ') : null;
}

// Подпись справа от полосы: процент («≈» у оценки) и сколько осталось. Осталось — от источника
// (etaSeconds local-media), а без него у текущего этапа с настоящим процентом — по темпу этапа:
// stageMs / percent × (100 − percent). Пока выборка мала (до 10% или 15 с), темп скачет — молчим
export const ETA_MIN_PERCENT = 10;
export const ETA_MIN_STAGE_MS = 15_000;

// Хвост прогона темпом не обещаем: на последних процентах копятся самые долгие тесты
export const ETA_TAIL_PERCENT = 98;

// sinceMs — сколько прошло с последнего события прогресса (progressAt); null — метки нет (после
// F5 до первого события): «осталось» тогда не показываем. Оценка фиксируется на МОМЕНТ события
// и отсчитывается вниз, а не пересчитывается на каждом рендере: пока хвостовые тесты висят,
// процент стоит, а время этапа растёт — пересчёт держал бы «≈0:03» вечно. Истекла — молчим
export function meterText(p: ToolProgress | null | undefined, stageMs: number | null, sinceMs: number | null): string | null {
  if (typeof p?.percent !== 'number') return null;
  const parts = [`${p.exact ? '' : '≈'}${Math.round(toolProgressPercent(p)!)}%`];
  const since = sinceMs == null ? null : Math.max(0, sinceMs);
  let left: number | null = null;
  if (since == null) left = null;
  else if (typeof p.etaSeconds === 'number' && p.etaSeconds > 0) left = p.etaSeconds * 1000 - since;
  else if (p.exact && stageMs != null && p.percent < ETA_TAIL_PERCENT) {
    // Сколько шёл этап на момент события — по нему и темп, и пороги выборки
    const stageAt = stageMs - since;
    if (stageAt >= ETA_MIN_STAGE_MS && p.percent >= ETA_MIN_PERCENT)
      left = stageAt / p.percent * (100 - p.percent) - since;
  }
  // Меньше секунды — уже «0:00»: не обещаем то, что истекает на глазах
  if (left != null && left >= 1000)
    parts.push(`осталось ${typeof p.etaSeconds === 'number' && p.etaSeconds > 0 ? '~' : '≈'}${formatClock(left)}`);
  return parts.join(' · ');
}

// Процент для полосы; null — полоса остаётся неопределённой. Потолок: у оценки 95, у настоящих
// шагов 99 — «готово» в обоих случаях говорит только результат
export function toolProgressPercent(p: ToolProgress | null | undefined): number | null {
  return typeof p?.percent === 'number' ? Math.max(0, Math.min(p.exact ? 99 : 95, p.percent)) : null;
}

// Ожидание в очереди (очередь сборок, local-media «N-я в очереди»): ничего не выполняется,
// поэтому бегущей полосы нет — только пустая дорожка на её месте
export function isQueued(p: ToolProgress | null | undefined): boolean {
  return p?.stage === 'queued';
}

// Короткие вызовы (Read, Grep) таймером не шумят: полоса и время появляются с этого порога
export const TOOL_TIMER_MIN_MS = 2000;

// Очередь короче этого в строке этапов не показываем: слот взят почти сразу — это не событие
export const QUEUE_STAGE_MIN_MS = 2000;

// Этап в строке этапов карточки: done — ✓ с длительностью, current — идёт (выделен, не
// режется), failed — ✕ (сборка упала, прогон оборван на нём). ms null — длительность не
// восстановить (старая история, этап без конца)
export interface StageView { key: string; label: string; ms: number | null; state: 'done' | 'current' | 'failed' }

// Подсчёт тестов отдельным этапом на карточке не показываем: он входит в «тесты». Сервер его
// уже не шлёт, а в сохранённой истории он есть — сливаем со следующим этапом (тот начинается с
// начала подсчёта), а последний (обрыв на подсчёте) переименовываем в «тесты»
function foldListStage(stages: readonly ToolStage[]): readonly ToolStage[] {
  if (!stages.some(s => s.stage === 'list')) return stages;
  const out: ToolStage[] = [];
  let listStart: number | null = null;
  stages.forEach((s, i) => {
    if (s.stage === 'list' && i < stages.length - 1) { listStart ??= s.startedAt; return; }
    const folded = s.stage === 'list' ? { ...s, stage: 'running', label: 'тесты' } : s;
    out.push(listStart != null ? { ...folded, startedAt: listStart } : folded);
    listStart = null;
  });
  return out;
}

// Строка этапов run_tests из снимка сервера. now — «сейчас» по часам карточки (идёт), endAt —
// конец вызова (finishedAt результата или момент обрыва): им закрывается этап, который сервер
// не успел закрыть. aborted — вызов оборван: незакрытый последний этап — на нём и оборвалось
export function stageViews(
  raw: readonly ToolStage[] | null | undefined,
  opts: { running: boolean; aborted: boolean; now: number | null; endAt: number | null },
): StageView[] {
  if (!raw?.length) return [];
  const stages = foldListStage(raw);
  const views: StageView[] = [];
  stages.forEach((s, i) => {
    const last = i === stages.length - 1;
    const open = s.endedAt == null;
    const end = !open ? s.endedAt! : last ? (opts.running ? opts.now : opts.endAt) : null;
    const ms = end != null ? Math.max(0, end - s.startedAt) : null;
    if (s.stage === 'queued' && (ms == null || ms < QUEUE_STAGE_MIN_MS)) return;
    const state = s.failed === true || (last && open && opts.aborted) ? 'failed'
      : last && open && opts.running ? 'current' : 'done';
    views.push({ key: `${s.stage}-${s.startedAt}`, label: s.label, ms, state });
  });
  return views;
}

// Финальный ли снимок этапов: итог есть либо последний этап закрыт. Живой снимок всегда держит
// последний этап открытым — закрывает его только конец прогона (TestRunStages.Finish)
export function isFinalStages(
  stages: readonly ToolStage[] | null | undefined,
  totals: ToolRunTotals | null | undefined,
): boolean {
  return totals != null || (stages != null && stages.length > 0 && stages[stages.length - 1].endedAt != null);
}

// Подпись при текущем этапе в строке этапов. Тесты — подпись прогресса целиком («тесты 12 из
// 177»); очередь — только подробность из скобок («занято 2»): слово «очередь» уже в этапе.
// «Сборка» у тестов сама себе подпись; подсчёт идёт под этапом «тесты» без подписи и процента.
// У инструмента сборки (build) этап «сборка» — единственный, и его счётчик («12 из 40
// проектов», «vite build · этап 2 из 6») — вся суть подписи; у подъёма стенда (start_stand)
// сборка идёт теми же движками — тот же счётчик
export function stageCaptionOf(p: ToolProgress | null | undefined, toolName?: string): string | null {
  if (!p?.label) return null;
  if (p.stage === 'running') return p.label;
  if (p.stage === 'build' && (toolName === BUILD_TOOL || toolName === START_STAND_TOOL)) return p.label;
  if (p.stage === 'queued') return /\(([^)]+)\)\s*$/.exec(p.label)?.[1] ?? p.label;
  return null;
}

// Итог прогона на закрытой карточке: «174 из 177 · упало 3», без упавших — «177 из 177»
export function totalsText(t: ToolRunTotals | null | undefined): string | null {
  if (!t) return null;
  return `${t.passed} из ${t.total}` + (t.failed > 0 ? ` · упало ${t.failed}` : '');
}

// M:SS, с часа — H:MM:SS
export function formatClock(ms: number): string {
  const total = Math.floor(Math.max(0, ms) / 1000);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = String(total % 60).padStart(2, '0');
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${s}` : `${m}:${s}`;
}

// Сколько уже идёт (результата нет) либо сколько шёл (есть finishedAt). null — отметок нет
// (история до этих полей) либо результат есть, а конца нет: врать длительностью не будем.
// Итоговая длительность считается целиком по часам сервера и точна. «Идёт» — разность
// часов браузера и сервера: расхождение часов сдвигает его на ту же величину в обе
// стороны (у синхронизированных по NTP машин — доли секунды); к нулю прижат только
// уход в минус, когда часы браузера отстают.
export function toolElapsedMs(
  item: { startedAt?: number | null; finishedAt?: number | null; result?: string | null },
  now: number,
): number | null {
  if (typeof item.startedAt !== 'number') return null;
  if (item.result == null) return Math.max(0, now - item.startedAt);
  return typeof item.finishedAt === 'number' ? Math.max(0, item.finishedAt - item.startedAt) : null;
}

// Консоль (Bash, PowerShell) и агенты получают фактический старт (tool_started) — до него
// «идёт» не показываем вовсе: отсчёт от tool_use включал бы ожидание разрешения, и после
// «Разрешить» цифра замирала бы, пока честный отсчёт от фактического старта её не догонит
// (у PowerShell без этого индикатор ожидания показывал ложный отсчёт и сбрасывал его на старте).
// У остальных инструментов tool_started не бывает — у них отсчёт от tool_use. Список явный, а
// не isConsoleTool: BashOutput и KillShell тоже «консоль» по имени, но старта не получают —
// их таймер не появился бы никогда
const STARTED_TOOLS = new Set(['bash', 'powershell', 'task', 'agent']);

export function awaitsToolStart(item: { name: string; started?: boolean }): boolean {
  return STARTED_TOOLS.has(item.name.toLowerCase()) && item.started !== true;
}

// Показанное «идёт» привязано к своему startedAt. Сдвиг старта (финальный tool_use после
// ранней карточки со стрима аргументов, tool_started) начинает отсчёт заново: иначе максимум
// перенёс бы в итог время генерации аргументов — «готово · 0:30» при 50 мс работы, а после
// F5 то же «готово» без времени. В пределах одного старта значение не убывает.
export interface ShownClock { startedAt: number; value: number }

export function tickShownClock(prev: ShownClock | null, startedAt: number, now: number): ShownClock {
  const value = Math.max(0, now - startedAt);
  return prev?.startedAt === startedAt ? { startedAt, value: Math.max(prev.value, value) } : { startedAt, value };
}

// Показанное для текущего старта; отсчёт от прежнего старта не в счёт
export function shownFor(clock: ShownClock | null, startedAt: number | null | undefined): number | null {
  return clock != null && clock.startedAt === startedAt ? clock.value : null;
}

// Что показать часами карточки. running — инструмент идёт в живом ходе; shown — последнее
// значение живого отсчёта «идёт» (держится и после остановки). Итог «готово» по часам
// сервера не меньше уже показанного «идёт»: иначе расхождение часов браузера дало бы откат.
// abortedAt — момент обрыва хода (ToolLiveness.abortedAt): «прервано» тоже показывает, сколько
// успело проработать. Нет отметки — последнее показанное «идёт» (обрыв на глазах)
export function toolClockMs(
  item: { name: string; started?: boolean; startedAt?: number | null; finishedAt?: number | null; result?: string | null },
  running: boolean,
  shown: number | null,
  abortedAt?: number | null,
): number | null {
  if (running) return awaitsToolStart(item) ? null : shown;
  const final = item.result != null ? toolElapsedMs(item, 0)
    : typeof abortedAt === 'number' && !awaitsToolStart(item) ? toolElapsedMs({ ...item, result: '', finishedAt: abortedAt }, 0)
    : null;
  if (final == null) return shown;
  return shown != null ? Math.max(final, shown) : final;
}

// Какие карточки инструментов без результата ещё идут, а какие оборваны.
// live — вызовы без результата в ЖИВОМ ходе или внутри ещё работающего фонового агента:
// только им тикает таймер. Гейт «чат занят»
// на весь чат не годится: карточка оборванного хода ожила бы на следующем ходу («идёт 47:12»).
// dead — вызовы, чей ход закончился без результата для них: после «Стопа», ошибки или
// аварийного выхода (interrupted / error / session_ended) — все; после штатного result —
// только верхнего уровня, а вызовы внутри сабагентов (parentToolUseId) могут честно
// доработать после конца хода (доживающий фоновый агент). Такая карточка — «прервано».
// Вложенный вызов мёртв и тогда, когда закрыт его родитель: у обычного агента есть
// результат, у фонового — bgDone/bgAborted/workflowDone. Иначе внутренний Bash агента,
// не дождавшийся tool_result (CLI не дослал, процесс перезапущен штатным exited), тикал бы
// на каждом следующем ходу: ни result, ни session_ended его не закрывают.
// abortedAt — момент обрыва (ts пометки «прервано»/ошибки) для оборванных вызовов: по нему
// «прервано» показывает, сколько вызов успел проработать. Отметки нет (штатный result,
// session_ended, закрытый родитель) — вызова в карте нет
export interface ToolLiveness {
  live: ReadonlySet<string>;
  dead: ReadonlySet<string>;
  abortedAt?: ReadonlyMap<string, number>;
}

type LivenessItem = {
  kind: string; id?: string; result?: string | null; parentToolUseId?: string | null;
  bgDone?: boolean; bgAborted?: boolean; workflowDone?: boolean; ts?: number;
};

// Родитель закрыт — его вложенные вызовы уже не доработают. Результат фонового запуска —
// лишь квитанция, а не конец: у такого родителя конец только по bgDone/bgAborted/workflowDone
function parentClosed(p: LivenessItem): boolean {
  if (p.bgDone === true || p.bgAborted === true || p.workflowDone === true) return true;
  return p.result != null && !isBgLaunchResult(p.result);
}

export function toolLiveness(items: readonly LivenessItem[], busy: boolean): ToolLiveness {
  let open: LivenessItem[] = [];
  const dead = new Set<string>();
  const abortedAt = new Map<string, number>();
  const byId = new Map<string, LivenessItem>();
  for (const it of items) {
    if (it.kind === 'tool_use') {
      if (it.id) byId.set(it.id, it);
      if (it.result == null && it.id) open.push(it);
    } else if (it.kind === 'interrupted' || it.kind === 'error' || it.kind === 'session_ended') {
      for (const o of open) {
        dead.add(o.id!);
        if (typeof it.ts === 'number') abortedAt.set(o.id!, it.ts);
      }
      open = [];
    } else if (it.kind === 'result') {
      for (const o of open) if (!o.parentToolUseId) dead.add(o.id!);
      open = open.filter(o => o.parentToolUseId);
    }
  }
  // Цепочка вверх: вызов внутри агента, вложенного в закрытый или мёртвый агент, тоже мёртв
  const isClosed = (id: string, depth = 0): boolean => {
    const p = byId.get(id);
    if (!p || depth > 16) return false;
    if (dead.has(id) || parentClosed(p)) return true;
    return !!p.parentToolUseId && isClosed(p.parentToolUseId, depth + 1);
  };
  open = open.filter(o => {
    if (!o.parentToolUseId || !isClosed(o.parentToolUseId)) return true;
    dead.add(o.id!);
    return false;
  });
  // Ход закончился, а фоновый агент ещё работает: его внутренние вызовы тикают и без «чат
  // занят». Закрытые и мёртвые предки уже отсеяны выше, так что квитанция запуска в цепочке
  // означает живого агента. Прочие открытые вызовы вне хода не оживают
  const inLiveBgAgent = (id: string, depth = 0): boolean => {
    const p = byId.get(id);
    if (!p || depth > 16) return false;
    if (p.result != null && isBgLaunchResult(p.result)) return true;
    return !!p.parentToolUseId && inLiveBgAgent(p.parentToolUseId, depth + 1);
  };
  const live = busy ? open : open.filter(o => !!o.parentToolUseId && inLiveBgAgent(o.parentToolUseId));
  return { live: new Set(live.map(o => o.id!)), dead, abortedAt };
}

// Активный инструмент для индикатора ожидания под лентой: из живых (ToolLiveness.live) —
// самый поздний по порядку ленты вызов верхнего уровня с отметкой старта. Вложенные вызовы
// сабагентов не в счёт (их видно в карточке агента), стримящиеся аргументы — тоже: команда
// ещё печатается. Параллельные вызовы: показан последний из уже стартовавших (у ждущего
// tool_started времени нет — он сбросил бы идущий отсчёт соседа); стартовавших нет — последний
// ждущий, подписью без времени. Закончился показанный раньше остальных — подпись честно
// прыгает на предыдущий живой
type ActiveToolItem = {
  kind: string; id?: string; name?: string; input?: unknown; parentToolUseId?: string | null;
  streamingArg?: string | null; startedAt?: number | null; started?: boolean;
};

export function pickActiveTool<T extends ActiveToolItem>(items: readonly T[], live: ReadonlySet<string>): T | null {
  // Зовётся на каждую дельту стрима: без живых вызовов ленту не обходим
  if (live.size === 0) return null;
  let waiting: T | null = null;
  for (let i = items.length - 1; i >= 0; i--) {
    const it = items[i];
    if (it.kind !== 'tool_use' || !it.id || !live.has(it.id)) continue;
    if (it.parentToolUseId || it.streamingArg != null || typeof it.startedAt !== 'number') continue;
    if (!awaitsToolStart({ name: it.name ?? '', started: it.started })) return it;
    waiting ??= it;
  }
  return waiting;
}

// Подпись активного инструмента: русское description от модели, иначе имя по-русски.
// description берём только у консоли (признак тот же, что у consoleCaption) и у Task/Agent —
// там это короткая подпись действия; у MCP и прочих (tasks_create, TaskCreate, Workflow)
// description — содержимое, нередко длинный markdown, в строку ожидания ему нельзя
function hasCaptionDescription(name: string): boolean {
  if (name === 'Task' || name === 'Agent') return true;
  return isConsoleTool(name) && !name.startsWith('mcp__');
}

export function activeToolLabel(item: { name: string; input?: unknown }): string {
  const d = (item.input as { description?: unknown } | null | undefined)?.description;
  return hasCaptionDescription(item.name) && typeof d === 'string' && d.trim()
    ? d.trim()
    : toolCardLabel(item.name, item.input);
}

// «упало K» (K > 0) — единственный тревожный сигнал живой подписи прогресса: карточка и индикатор
// ожидания красят его цветом ошибки. Группа захвата — чтобы split отдавал совпадения нечётными
export const FAILED_RE = /(упало [1-9]\d*)/;

// Печатная машинка индикатора ожидания (вариант D): по кругу печатает фразы инструмента —
// подпись, затем счётчик этапа; одна фраза — впечатывается один раз и стоит. Чистая функция
// шага: компонент крутит её таймером, тест — без таймеров.
// phrase — индекс фразы, shown — сколько символов напечатано; hold и done показывают фразу
// ЦЕЛИКОМ (счётчик, сменивший значение, пока стоит, обновляется без перепечатки)
export type TypewriterPhase = 'typing' | 'hold' | 'erasing' | 'pause' | 'done';
export interface TypewriterState { phrase: number; shown: number; phase: TypewriterPhase }

// Задержки фаз, мс: печать символа, показ целиком, стирание символа, пауза между фразами
export const TYPEWRITER_MS = { type: 35, hold: 2600, erase: 18, pause: 200 } as const;

// Новая подпись — печать с нуля; монтирование посреди инструмента (F5) — первая фраза уже
// стоит целиком и держится, как после печати
export const TYPEWRITER_START: TypewriterState = { phrase: 0, shown: 0, phase: 'typing' };
export const TYPEWRITER_AT_REST: TypewriterState = { phrase: 0, shown: Number.MAX_SAFE_INTEGER, phase: 'hold' };

export function nextTypewriterStep(s: TypewriterState, phrases: readonly string[]): TypewriterState {
  const n = phrases.length;
  if (n === 0) return { phrase: 0, shown: 0, phase: 'done' };
  // Фраз стало меньше (счётчик пропал) — к первой
  const phrase = s.phrase < n ? s.phrase : 0;
  const len = phrases[phrase].length;
  switch (s.phase) {
    case 'typing': {
      const shown = Math.min(s.shown + 1, len);
      return shown < len ? { phrase, shown, phase: 'typing' } : { phrase, shown: len, phase: n > 1 ? 'hold' : 'done' };
    }
    case 'hold':
      return n > 1 ? { phrase, shown: len, phase: 'erasing' } : { phrase, shown: len, phase: 'done' };
    case 'erasing': {
      const shown = Math.min(s.shown, len) - 1;
      return shown > 0 ? { phrase, shown, phase: 'erasing' } : { phrase, shown: 0, phase: 'pause' };
    }
    case 'pause':
      return { phrase: (phrase + 1) % n, shown: 0, phase: 'typing' };
    default:
      return { phrase, shown: len, phase: 'done' };
  }
}

// Сколько ждать до следующего шага; null — цикл стоит (одна фраза впечатана)
export function typewriterDelay(s: TypewriterState): number | null {
  switch (s.phase) {
    case 'typing': return TYPEWRITER_MS.type;
    case 'hold': return TYPEWRITER_MS.hold;
    case 'erasing': return TYPEWRITER_MS.erase;
    case 'pause': return TYPEWRITER_MS.pause;
    default: return null;
  }
}

// Что видно сейчас: показ целиком (hold, done) — фраза как она есть СЕЙЧАС, иначе напечатанная часть
export function typewriterText(s: TypewriterState, phrases: readonly string[]): string {
  if (phrases.length === 0) return '';
  const text = phrases[s.phrase < phrases.length ? s.phrase : 0];
  return s.phase === 'hold' || s.phase === 'done' ? text : text.slice(0, Math.max(0, s.shown));
}

// Время рядом с подписью индикатора — словами, как в строке ожидания: «52 с», «1 мин 12 с»,
// «1 ч 3 мин» (секунды в часовом масштабе — шум)
export function formatWaitClock(ms: number): string {
  const total = Math.floor(Math.max(0, ms) / 1000);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  if (h > 0) return `${h} ч ${m} мин`;
  if (m > 0) return `${m} мин ${s} с`;
  return `${s} с`;
}

// Что показать индикатору ожидания вместо глагола. null — глаголы: инструмента нет, ход
// ждёт ответа человека или порог не пройден (elapsed null — до первого тика). clock null —
// подпись без времени: фактический старт Bash/агента ещё не пришёл (timer = false).
// leadMs — сколько вызов уже провисел до текущего startedAt (tool_started сдвинул старт, см.
// captionLeadMs). Порог один на вызов и считается от появления: иначе подпись, уже стоявшая
// до старта, на tool_started сменялась глаголами ещё на 2 с — отсчёт-то начался с нуля
export function waitingToolCaption(
  label: string | null | undefined, elapsed: number | null, timer: boolean, awaitingResponse: boolean, leadMs = 0,
): { label: string; clock: string | null } | null {
  if (awaitingResponse || !label) return null;
  // До первого тика нового отсчёта (elapsed null) — ноль: прошедший порог не откатываем
  const run = elapsed ?? (leadMs > 0 ? 0 : null);
  if (run == null || run + leadMs < TOOL_TIMER_MIN_MS) return null;
  return { label, clock: timer ? formatWaitClock(run) : null };
}

// Сколько вызов провисел до фактического старта: от появления (appearedAt — прежний startedAt,
// его хранит редьюсер на tool_started) до текущего startedAt. 0 — старт не сдвигался
export function captionLeadMs(startedAt: number | null | undefined, appearedAt: number | null | undefined): number {
  return typeof startedAt === 'number' && typeof appearedAt === 'number' ? Math.max(0, startedAt - appearedAt) : 0;
}

// Пройдена ли группа «N действий» (сворачивать ли её). Обычно — как только после неё встал
// видимый элемент или ход кончился. Но пока в группе есть вызов без результата, который ещё
// идёт (live) или ждёт разрешения, группа не пройдена: карточка разрешения встаёт в ленте
// ПОСЛЕ группы и свернула бы её вместе с живой карточкой и таймером. Оборванный вызов
// (dead, «прервано») не держит — такая группа сворачивается как раньше
export function isToolGroupDone(opts: {
  entries: readonly LivenessItem[];
  liveness: ToolLiveness | null;
  hasVisibleAfter: boolean;
  busy: boolean;
  awaitingPermission: boolean;
}): boolean {
  const { entries, liveness, hasVisibleAfter, busy, awaitingPermission } = opts;
  const pending = entries.some(it => it.kind === 'tool_use' && it.result == null && !!it.id
    && liveness?.dead.has(it.id) !== true
    && (liveness?.live.has(it.id) === true || awaitingPermission));
  if (pending) return false;
  return hasVisibleAfter || !busy;
}
