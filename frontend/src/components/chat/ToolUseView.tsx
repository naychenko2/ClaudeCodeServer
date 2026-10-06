import { memo, useState, useEffect, useMemo, useContext } from 'react';
import { Plug, Eye, SquarePen, Terminal, Globe, CircleUser, Sparkles, SquareCheck, Wrench } from 'lucide-react';
import type { ChatItem, ToolRunFailure } from '../../types';
import { C, FONT, FS, SP } from '../../lib/design';
import { relPath, stripRoot } from '../../lib/paths';
import { splitAgentResultTail, formatTailTokens, formatTailDuration, isAsyncLaunchAck, asyncLaunchAckNote } from '../../lib/agentTail';
import { ChatProjectContext, FalCostContext, GlifCostContext, ToolLivenessContext } from './contexts';
import { LiveDot, ProgressBar } from '../ui';
import { awaitsToolStart, FAILED_RE, formatClock, isQueued, meterText, stageCaptionOf, stageViews, toolClockMs, toolProgressPercent, toolProgressText, totalsText, TOOL_TIMER_MIN_MS, type StageView } from '../../lib/toolTiming';
import { useRunningElapsed } from '../../hooks/useRunningElapsed';
import { toolLabel, toolWord, toolCardLabel, testRunArg, buildArg, localJobsWaitArg, consoleCaption, isConsoleTool, operationOf, RUN_TESTS_TOOL, BUILD_TOOL, LOCAL_JOBS_WAIT_TOOL } from '../../lib/toolLabels';
import { OPERATION_ICON } from '../../lib/operationIcons';
import { useIsMobile } from '../../lib/breakpoints';
import { CodeBlockFrame } from './CodeCopyButton';
import { MediaBlock, extractMediaMeta, mediaLabel } from './MediaBlock';
import { useVisibleMedia } from './mediaDedup';

// Высота строки подписи прогресса и строки этапов под шапкой: фиксированная, чтобы приход
// и смена текста не двигали ленту
const CAPTION_LINE_H = 16;

// Общая вёрстка моноширинных блоков тела: команда под подписью и вывод выглядят одной
// парой — отступы, радиус, кегль и интерлиньяж совпадают
const MONO_PRE_STYLE: React.CSSProperties = {
  padding: '8px 10px', borderRadius: 7, fontFamily: FONT.mono, fontSize: 11.5, lineHeight: 1.5,
  overflow: 'auto', whiteSpace: 'pre-wrap', wordBreak: 'break-word',
};

// Место слева в шапке: пока инструмент идёт — живая точка (LiveDot), у готовой карточки —
// пустое место той же ширины, и шапка при завершении не прыгает вбок. Пока сколько осталось
// неизвестно, живость показывает точка; с процентом под карточкой встаёт полоса на видимой
// дорожке (ProgressMeter). Строки под шапкой отступают на это место плюс зазор
const LEAD_W = 18;
const HEAD_GAP = 10;
const BELOW_PAD = LEAD_W + HEAD_GAP;
// Слот слова статуса в шапке (десктоп): по самому длинному частому слову «прервано» — слова
// разной длины начинаются с одной линии, а время слева от слота выровнено по правому краю
const STATUS_SLOT_W = '8.5ch';
// Место шеврона раскрытия: держится и у карточки без тела
const CHEVRON_W = 11;
// Процент прогресса: факт (настоящие шаги) — сплошная заливка, оценка — пунктир
type ProgressPct = { value: number; estimate: boolean; label?: string };

// Полоса прогресса под карточкой: дорожка на всю ширину строки — видно, где конец, — и справа
// процент с «осталось». Высота строки фиксирована: смена подписи ленту не двигает
function ProgressMeter({ pct, text }: { pct: ProgressPct; text: string | null }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, height: CAPTION_LINE_H, paddingLeft: BELOW_PAD, paddingRight: SP.sm }}>
      {/* Серая (muted): вторичная информация не спорит с акцентом главного действия */}
      <ProgressBar value={pct.value} estimate={pct.estimate} tone="muted" size="thin" label={pct.label} transition="width .5s linear" style={{ flex: 1, minWidth: 0 }} />
      {text && (
        <span style={{ flexShrink: 0, fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' }}>
          {text}
        </span>
      )}
    </div>
  );
}

// Упавшие тесты на закрытой карточке — без раскрытия вывода: имя (режется слева, конец имени —
// метод — виден всегда) и первая строка сообщения; сверх показанных — «ещё N»
function FailureList({ failures, failed }: { failures: ToolRunFailure[]; failed: number }) {
  const more = failed - failures.length;
  const row: React.CSSProperties = { display: 'flex', minWidth: 0, gap: SP.xs, height: CAPTION_LINE_H, lineHeight: `${CAPTION_LINE_H}px`, fontSize: FS.xs, whiteSpace: 'nowrap' };
  return (
    <div aria-live="polite" style={{ paddingLeft: BELOW_PAD, paddingRight: SP.sm, paddingBottom: SP.xxs }}>
      {failures.map((f, i) => (
        <div key={i} title={f.message ? `${f.name}\n${f.message}` : f.name} style={row}>
          <span style={{ flexShrink: 0, color: C.dangerText }}>✕</span>
          <span className="cc-trunc-left" style={{ minWidth: 0, maxWidth: f.message ? '60%' : '100%', flexShrink: 0, overflow: 'hidden', textOverflow: 'ellipsis', color: C.textSecondary }}>
            {f.name}
          </span>
          {f.message && (
            <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', color: C.textMuted }}>
              — {f.message}
            </span>
          )}
        </div>
      ))}
      {more > 0 && <div style={{ ...row, color: C.textMuted }}>ещё {more} — в выводе</div>}
    </div>
  );
}

function LeadSlot({ live }: { live: boolean }) {
  return (
    <span aria-hidden={!live} style={{ width: LEAD_W, flexShrink: 0, display: 'flex', justifyContent: 'center' }}>
      {live && <LiveDot />}
    </span>
  );
}

// «упало K» — единственный тревожный сигнал живой карточки: выделен цветом ошибки
// (регулярка общая с индикатором ожидания — FAILED_RE в lib/toolTiming)
function ProgressCaption({ text }: { text: string }) {
  return <>{text.split(FAILED_RE).map((part, i) =>
    i % 2 ? <span key={i} style={{ color: C.dangerText }}>{part}</span> : part)}</>;
}

// Строка этапов прогона: «✓ сборка 1:42 · тесты 2:13 · 412 из 7951».
// Прошедшие прижимаются и режутся многоточием, текущий этап с подписью прогресса — никогда:
// даже на 320 px видно, что идёт сейчас. Этап, на котором оборвалось, — крестиком и красным.
// Единственный этап (сборка) без часов: его время — то же «идёт»/«готово» в шапке
function StageLine({ stages, caption }: { stages: StageView[]; caption: string | null }) {
  const clock = (s: StageView) => s.ms != null && stages.length > 1 ? ` ${formatClock(s.ms)}` : '';
  const tail = stages[stages.length - 1].state === 'done' ? null : stages[stages.length - 1];
  const past = (tail ? stages.slice(0, -1) : stages).map(s => `✓ ${s.label}${clock(s)}`).join(' · ');
  return (
    <div style={{ display: 'flex', minWidth: 0, height: CAPTION_LINE_H, lineHeight: `${CAPTION_LINE_H}px`, fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' }}>
      {past && <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}>{past}</span>}
      {tail && (
        <span style={{ flexShrink: 0, whiteSpace: 'nowrap' }}>
          {/* Неразрывный пробел: ведущий обычный у флекс-элемента схлопывается («1:42· тесты») */}
          {past ? ' · ' : ''}
          {tail.state === 'failed'
            ? <span style={{ color: C.dangerText }}>✕ {tail.label}{clock(tail)}</span>
            : <span style={{ color: C.textSecondary, fontWeight: 600 }}>{tail.label}{clock(tail)}</span>}
          {caption && <> · <ProgressCaption text={caption} /></>}
        </span>
      )}
    </div>
  );
}

// Иконка и цвет по типу инструмента — чтобы read/edit/bash/web/mcp различались с первого взгляда.
// Типовая операция (тесты, сборка, git…) — иконкой по смыслу, цвет остаётся по виду инструмента
function toolMeta(name: string, input: unknown): { color: string; icon: React.ReactNode } {
  const base = toolKindMeta(name);
  const op = operationOf(name, input);
  if (!op) return base;
  const Icon = OPERATION_ICON[op];
  return { color: base.color, icon: <Icon size={13} strokeWidth={2} /> };
}

function toolKindMeta(name: string): { color: string; icon: React.ReactNode } {
  const n = name.toLowerCase();
  const common = { size: 13, strokeWidth: 2 } as const;
  if (n.startsWith('mcp__'))
    return { color: C.plan, icon: <Plug {...common} /> };
  if (['read', 'glob', 'grep', 'ls'].includes(n))
    return { color: C.info, icon: <Eye {...common} /> };
  if (['edit', 'write', 'multiedit', 'notebookedit'].includes(n))
    return { color: C.accent, icon: <SquarePen {...common} /> };
  if (n.startsWith('bash') || n.includes('shell'))
    return { color: C.success, icon: <Terminal {...common} /> };
  if (['websearch', 'webfetch'].includes(n))
    return { color: C.plan, icon: <Globe {...common} /> };
  if (n === 'task')
    return { color: C.accent, icon: <CircleUser {...common} /> };
  if (n === 'skill')
    return { color: C.plan, icon: <Sparkles {...common} /> };
  // Todo-задачи — та же «галочка в рамке», что у карточки плана
  if (['taskcreate', 'taskupdate', 'tasklist', 'taskget'].includes(n))
    return { color: C.accent, icon: <SquareCheck {...common} /> };
  return { color: C.info, icon: <Wrench {...common} /> };
}

// Статусы todo-задач (TaskUpdate) по-русски — для компактной строки в ленте
const TASK_STATUS_RU: Record<string, string> = {
  pending: 'в очереди', in_progress: 'в работе', completed: 'готово',
  cancelled: 'отменена', deleted: 'удалена',
};

// Подписи инструментов живут в lib (ими же подписан живой прогресс); реэкспорт — для
// прежних импортёров из этого файла
export { toolLabel, toolWord };

// Inline-diff для Edit/MultiEdit/Write: удалённые строки красным, добавленные зелёным
function DiffBody({ hunks }: { hunks: Array<{ old?: string; new?: string }> }) {
  const MAX = 240;
  let count = 0;
  const rows: React.ReactNode[] = [];
  const pushLines = (text: string, kind: 'del' | 'add') => {
    for (const ln of text.split('\n')) {
      if (count >= MAX) return;
      rows.push(
        <div key={count} style={{
          display: 'flex', gap: 7, padding: '0 9px',
          background: kind === 'del' ? C.diffRemBg : C.diffAddBg,
          color: kind === 'del' ? C.diffRemText : C.diffAddText,
          whiteSpace: 'pre-wrap', wordBreak: 'break-word',
        }}>
          <span style={{ userSelect: 'none', opacity: 0.55, flexShrink: 0 }}>{kind === 'del' ? '−' : '+'}</span>
          <span style={{ flex: 1 }}>{ln || ' '}</span>
        </div>
      );
      count++;
    }
  };
  hunks.forEach(h => { if (h.old) pushLines(h.old, 'del'); if (h.new) pushLines(h.new, 'add'); });
  return (
    <div style={{
      margin: '0 0 9px', borderRadius: 7, overflow: 'hidden', border: `1px solid ${C.bgInset}`,
      fontFamily: FONT.mono, fontSize: 11.5, lineHeight: 1.55,
      maxHeight: 320, overflowY: 'auto',
    }}>
      {rows}
      {count >= MAX && <div style={{ padding: '2px 9px', color: C.textMuted, fontStyle: 'italic' }}>…(обрезано)</div>}
    </div>
  );
}

export type ToolUseItem = Extract<ChatItem, { kind: 'tool_use' }>;

// Строка инструмента с раскрываемым телом результата (вывод Bash/Read и т.п.).
// React.memo: элементы ленты иммутабельны по ссылке (кроме стримящегося последнего) —
// при дописывании ленты завершённые строки не перерендериваются.
export const ToolUseView = memo(function ToolUseView({ item, online = true, onOpenFile }: { item: Extract<ChatItem, { kind: 'tool_use' }>; online?: boolean; onOpenFile?: (path: string) => void }) {
  const meta = toolMeta(item.name, item.input);
  const [open, setOpen] = useState(false);
  const project = useContext(ChatProjectContext);
  const n = item.name.toLowerCase();
  // input инструмента — неизвестный JSON (unknown в контракте ChatItem): читаем
  // точечно по ключам, значения сужаем проверками в местах использования
  const inp = (item.input ?? {}) as Record<string, unknown>;
  // Во время стриминга показываем накопленный partial_json («печатает команду»), затем — разобранный аргумент.
  // Пути показываем относительно корня проекта: file_path/path — целиком, в командах и
  // glob-шаблонах вырезаем абсолютный корень из текста (там путь — часть строки).
  const pathVal = inp.file_path ?? inp.path ?? inp.notebook_path;
  // Человекочитаемый аргумент для todo-задач: TaskCreate — тема, TaskUpdate — «#id → статус»
  // Прогон тестов — цель, файлы и фильтр (вид прогона — в имени шапки)
  const taskArg = n === 'taskcreate' && typeof inp.subject === 'string' ? inp.subject
    : n === 'taskupdate' && inp.taskId != null
      ? `#${inp.taskId}${typeof inp.status === 'string' ? ` → ${TASK_STATUS_RU[inp.status] ?? inp.status}` : ''}`
      : item.name === RUN_TESTS_TOOL ? testRunArg(inp)
      : item.name === BUILD_TOOL ? buildArg(inp)
      : item.name === LOCAL_JOBS_WAIT_TOOL ? localJobsWaitArg(inp)
      : null;
  // Консольная команда с русской подписью: в шапке — подпись, команда — в теле над выводом
  const caption = consoleCaption(item.name, item.input, item.streamingArg);
  const commandText = caption ? stripRoot(caption.command, project?.rootPath) : null;
  const toolArg = item.streamingArg ?? caption?.description ?? taskArg ?? String(
    (inp.command != null ? stripRoot(String(inp.command), project?.rootPath) : null)
    ?? (pathVal != null ? relPath(String(pathVal), project?.rootPath) : null)
    ?? (inp.pattern != null ? stripRoot(String(inp.pattern), project?.rootPath) : null)
    ?? inp.query ?? inp.url ?? inp.description ?? inp.prompt ?? '');
  // Аргумент-путь (Read/Edit/…) — на мобиле обрезаем слева, чтобы было видно имя файла
  const argIsPath = inp.command == null && pathVal != null && item.streamingArg == null;
  // Имя инструмента по-русски (MCP → «server · tool»); у прогона тестов — с видом («Тесты · vitest»)
  const displayName = toolCardLabel(item.name, item.input);
  // Inline-diff из input (доступен сразу, не дожидаясь tool_result)
  const editHunks: Array<{ old?: string; new?: string }> =
    n === 'edit' && (typeof inp.old_string === 'string' || typeof inp.new_string === 'string')
      ? [{ old: typeof inp.old_string === 'string' ? inp.old_string : undefined, new: typeof inp.new_string === 'string' ? inp.new_string : undefined }]
    : n === 'multiedit' && Array.isArray(inp.edits)
      ? (inp.edits as Array<{ old_string?: string; new_string?: string }>).map(e => ({ old: e.old_string, new: e.new_string }))
    : n === 'write' && typeof inp.content === 'string'
      ? [{ new: inp.content }]
    : [];
  const hasDiff = editHunks.length > 0;
  const hasResult = item.result != null && item.result.trim().length > 0;
  // Системный хвост результата сабагента (agentId + <usage>…</usage>) — сырым текстом
  // в ленте выглядит мусором: вырезаем из тела и показываем аккуратной строкой метрик
  const isAgentTool = n === 'task' || n === 'agent';
  const agentSplit = useMemo(() => {
    if (!isAgentTool || item.result == null) return null;
    // Квитанция фонового запуска (run_in_background) — служебная метаинформация CLI,
    // сырым текстом её не показываем; ход агента виден в его блоке действий.
    // Прерванный агент (bgAborted) — честная пометка вместо «работает в фоне».
    if (isAsyncLaunchAck(item.result))
      return { body: asyncLaunchAckNote(item.bgAborted), tail: null };
    return splitAgentResultTail(item.result);
  }, [isAgentTool, item.result, item.bgAborted]);
  // Полный текст вывода (без обрезки до 4000 символов, которая идёт только в показ) —
  // то, что уходит в буфер по кнопке копирования.
  const outputText = useMemo(() => {
    if (item.result == null) return '';
    return stripRoot(agentSplit?.body ?? item.result, project?.rootPath);
  }, [item.result, agentSplit, project?.rootPath]);
  // Медиа (изображения + видео) из результата MCP-инструментов. Сквозной дедуп ленты
  // (glif project_update + media_view одного URL) применяется картой из контекста
  const rawMedia = useVisibleMedia(item);
  const media = hasResult && !item.isError ? rawMedia : [];
  const mediaMeta = hasResult && !item.isError ? extractMediaMeta(item.result!, media) : {};
  const hasMedia = media.length > 0;

  // Точная стоимость генерации fal.ai приходит с backend (billing-events по request_id).
  // Сопоставляем по request_id, извлечённому из результата вызова.
  const falCostByRequest = useContext(FalCostContext);
  const falRequestId = useMemo(() => {
    if (!hasMedia || !hasResult || item.isError) return undefined;
    try { return JSON.parse(item.result!).request_id as string | undefined; }
    catch { return undefined; }
  }, [hasMedia, hasResult, item.isError, item.result]);
  const falCostUsd = falRequestId ? falCostByRequest.get(falRequestId) : undefined;
  // glif: стоимость приезжает прямо в JSON результата (_meta.glif.billing) — берём из меты
  const costUsd = falCostUsd ?? mediaMeta.costUsd;
  // Кредиты glif — с backend (glif_cost по jobId, только когда зонд нашёл billing)
  const glifCostByJob = useContext(GlifCostContext);
  const glifCredits = mediaMeta.jobId ? glifCostByJob.get(mediaMeta.jobId) : undefined;
  // Генерация fal распознана, но стоимость ещё не подсчитана (биллинг приходит с задержкой)
  const costPending = hasMedia && !!falRequestId && falCostUsd === undefined;
  // «Считается…» не должно висеть вечно: если стоимость так и не пришла (напр. старое
  // изображение под другим аккаунтом fal.ai — его нет в текущем billing), убираем метку через 30с.
  const [pendingExpired, setPendingExpired] = useState(false);
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- автоснятие метки pending по таймеру 30с
    if (!costPending) { setPendingExpired(false); return; }
    const t = setTimeout(() => setPendingExpired(true), 30000);
    return () => clearTimeout(t);
  }, [costPending]);
  // Имя модели — из input вызова (в результате fal его нет)
  const falModel = ((inp.endpoint_id as string | undefined) ?? mediaMeta.model)?.split('/').pop();
  // Медиа показываем сразу, без клика; текст/diff — за клик. Команда, ушедшая из шапки в
  // тело, раскрывается и до результата
  const hasBody = hasDiff || (hasResult && !hasMedia) || commandText != null;
  // Консольные инструменты (Bash/shell) → тёмный «терминальный» вывод.
  // Остальные (Read/Grep/Glob/MCP и пр.) → светлая «панель вывода», чтобы текст/код не давил тёмным фоном.
  const isConsole = isConsoleTool(item.name);

  // Таймер: пока нет результата — тикает раз в секунду (только в живом ходе, см.
  // ToolLivenessContext), после результата — итоговая длительность. Короче порога не
  // показываем. Результата нет — через `== null`: из истории приходит "result": null.
  // Оборванный ход (Стоп, ошибка, падение) результата не пришлёт — карточка «прервано»
  // Bash и агенты до tool_started «идёт» не показывают (см. awaitsToolStart)
  const liveness = useContext(ToolLivenessContext);
  const settled = item.result != null;
  const aborted = !settled && liveness?.dead.has(item.id) === true;
  const running = !settled && !aborted && (liveness?.live.has(item.id) ?? true) && typeof item.startedAt === 'number';
  const shownElapsed = useRunningElapsed(item.startedAt, running && !awaitsToolStart(item));
  // «прервано» тоже с длительностью: до момента обрыва (ToolLiveness.abortedAt)
  const elapsed = toolClockMs(item, running, shownElapsed, aborted ? liveness?.abortedAt?.get(item.id) : null);
  const showClock = elapsed != null && elapsed >= TOOL_TIMER_MIN_MS;
  // Живой прогресс поверх таймера (tool_progress: сабагент, тесты, локальная генерация) —
  // подпись и, только если источник знает процент, полоса под карточкой с процентом и
  // «осталось» справа (в подписи их тогда нет). Ожидание в очереди полосы не даёт вовсе:
  // ничего не выполняется, живость — точка
  const progressValue = running && !isQueued(item.progress) ? toolProgressPercent(item.progress) : null;
  const progressText = running ? toolProgressText(item.progress, progressValue == null) : null;
  const progressPct: ProgressPct | null = progressValue != null
    ? { value: progressValue, estimate: item.progress?.exact !== true, label: toolProgressText(item.progress) ?? undefined }
    : null;
  // Строка этапов прогона тестов (этапы шлёт сервер, они же в истории вызова): идёт — «сейчас»
  // по часам карточки, закрыта — до результата или до обрыва
  const stages = useMemo(() => stageViews(item.stages, {
    running,
    aborted,
    now: running && typeof item.startedAt === 'number' && shownElapsed != null ? item.startedAt + shownElapsed : null,
    endAt: aborted ? liveness?.abortedAt?.get(item.id) ?? null : item.finishedAt ?? null,
  }), [item.stages, item.startedAt, item.finishedAt, item.id, running, aborted, shownElapsed, liveness]);
  const hasStages = stages.length > 0;
  // С этапами подпись прогресса едет при текущем этапе, а не в шапке (тесты — счётчик,
  // очередь — «занято 2»); «сборка» и подсчёт под «тестами» сами себе подпись — при них только
  // счётчик этапа
  const stageCaption = hasStages ? stageCaptionOf(item.progress, item.name) : null;
  // «Осталось» по темпу текущего этапа — от его начала, а не от старта вызова (сборка не в счёт)
  const currentStage = stages.length > 0 && stages[stages.length - 1].state === 'current' ? stages[stages.length - 1] : null;
  const meter = progressPct ? meterText(item.progress, currentStage?.ms ?? null) : null;
  // На мобиле подпись прогресса и итог — отдельной строкой под шапкой у ВСЕХ карточек
  // (шапка остаётся описанию, итог у всех стоит на одном месте). Строка держится с начала
  // выполнения, поэтому завершение ленту не сдвигает
  const isMobile = useIsMobile();
  const captionBelow = isMobile;
  const headCaption = hasStages ? null : progressText;
  // Итог завершённой или оборванной карточки — в шапке либо (мобила) строкой подписи. У
  // прогона тестов — со счётчиками: «готово · 2:15 · 174 из 177 · упало 3»
  // Упавшая сборка (dev build, сборка run_tests) — штатный результат вызова, не isError:
  // неуспех несёт закрытый крестиком этап, и шапка обязана сказать «ошибка», а не «готово»
  const stageFailed = settled && item.stages?.some(s => s.failed) === true;
  const failed = item.isError || stageFailed;
  const statusWord = failed ? 'ошибка' : item.bgAborted || aborted ? 'прервано' : hasMedia ? mediaLabel(media) : 'готово';
  const statusClock = showClock ? formatClock(elapsed) : null;
  // Мобила: итог строкой под шапкой слева — «статус · время», как был
  const statusText = statusWord + (statusClock ? ` · ${statusClock}` : '');
  const statusColor = failed || item.bgAborted || aborted ? C.dangerText : C.textMuted;
  const totals = totalsText(item.totals);
  const status = (
    <>
      {statusText}
      {totals && <span style={{ color: C.textMuted }}> · <ProgressCaption text={totals} /></span>}
    </>
  );

  return (
    // data-tool-id — по нему лента следит, видна ли карточка активного инструмента: видна —
    // индикатор ожидания не повторяет её подпись и время
    <div data-tool-id={item.id}>
      <div
        // Справа — запас под полосу прокрутки ленты: таймер и итог прижаты к правому краю
        // колонки, и полоса-накладка (её ширина в замере 0, рисуется поверх) съедала
        // последнюю цифру «идёт 1:0». minWidth: 0 — шапка не шире колонки при любом аргументе
        style={{ padding: `3px ${SP.sm}px 3px 0`, minWidth: 0, display: 'flex', alignItems: 'center', gap: 10, cursor: hasBody ? 'pointer' : 'default' }}
        onClick={() => hasBody && setOpen(o => !o)}
      >
        <LeadSlot live={!settled && !aborted} />
        <span style={{ display: 'flex', alignItems: 'center', gap: 5, flexShrink: 0, color: meta.color }}>
          {meta.icon}
          <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted }}>{displayName}</span>
        </span>
        {toolArg
          ? (() => {
              // Путь к файлу делаем кликабельным — открывает файл на просмотр (десктоп: split справа от чата).
              // Имя файла вынесено в отдельный span: каталог слева обрезается через direction: rtl,
              // basename всегда виден полностью и стилистически отделён — при длинном пути файл
              // читается как имя файла, а не как «…часть хвоста».
              const clickable = argIsPath && !!onOpenFile && pathVal != null;
              const sepIdx = argIsPath ? toolArg.lastIndexOf('/') : -1;
              const dir = sepIdx >= 0 ? toolArg.slice(0, sepIdx + 1) : '';
              const base = sepIdx >= 0 ? toolArg.slice(sepIdx + 1) : toolArg;
              return (
                <span
                  title={commandText ?? undefined}
                  style={{ flex: 1, display: 'flex', alignItems: 'baseline', overflow: 'hidden', fontFamily: caption ? FONT.sans : FONT.mono, fontSize: caption ? FS.xs : 11, minWidth: 0 }}
                >
                  {dir && (
                    <span
                      className="cc-trunc-left"
                      style={{ color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 }}
                    >
                      {dir}
                    </span>
                  )}
                  <span
                    onClick={clickable ? (e) => { e.stopPropagation(); onOpenFile!(relPath(String(pathVal), project?.rootPath)); } : undefined}
                    title={clickable ? 'Открыть файл' : undefined}
                    // Имя файла держим целиком (каталог слева обрежется сам); прочий аргумент —
                    // подпись, команда — обрезаем многоточием, а не посреди слова
                    style={{
                      color: clickable ? C.accent : C.textMuted, cursor: clickable ? 'pointer' : 'inherit',
                      ...(argIsPath ? { flexShrink: 0 } : { minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }),
                    }}
                  >
                    {base}
                  </span>
                </span>
              );
            })()
          : <span style={{ flex: 1 }} />}
        {headCaption && !captionBelow && (
          <span title={headCaption} style={{ fontSize: FS.xs, color: C.textMuted, minWidth: 0, maxWidth: '45%', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' }}>
            <ProgressCaption text={headCaption} />
          </span>
        )}
        {/* Мобила: живое «идёт M:SS» в шапке — как было */}
        {captionBelow && running && showClock && (
          <span style={{ fontSize: FS.xs, color: C.textMuted, flexShrink: 0, whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' }}>
            идёт {formatClock(elapsed)}
          </span>
        )}
        {/* Десктоп: итог двумя колонками у правого края — [счётчики] [время] [слот статуса].
            Слот фиксированной ширины, слово в нём слева: «готово», «ошибка», «прервано», «идёт»
            начинаются с одной линии, а время, прижатое к слоту, — выровнено по правому краю. Так
            колонка карточек в ленте ровная и при завершении ничего не прыгает. Красное — только
            слово статуса */}
        {!captionBelow && (settled || aborted) && totals && (
          <span style={{ fontSize: FS.xs, color: C.textMuted, flexShrink: 0, whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' }}>
            <ProgressCaption text={totals} />
          </span>
        )}
        {!captionBelow && (settled || aborted || (running && showClock)) && (
          <span data-tool-status="" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexShrink: 0, fontSize: FS.xs, whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' }}>
            {statusClock && <span style={{ color: C.textMuted }}>{statusClock}</span>}
            <span style={{ minWidth: STATUS_SLOT_W, color: running ? C.textMuted : statusColor }}>
              {running ? 'идёт' : statusWord}
            </span>
          </span>
        )}
        {/* Место шеврона держится всегда: у карточки без тела слот статуса иначе съезжал бы
            вправо на ширину шеврона, и колонка рвалась */}
        <span aria-hidden={!hasBody} style={{ width: CHEVRON_W, color: C.textMuted, fontSize: 11, flexShrink: 0, display: 'inline-block', textAlign: 'center', transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.2s' }}>
          {hasBody ? '▾' : null}
        </span>
      </div>
      {/* Мобила: строка подписи держится с начала выполнения (пустая до первого прогресса), а
          итог «готово · M:SS» встаёт на её место той же высоты — завершение ленту не двигает.
          У прогона тестов подпись едет в строке этапов, поэтому отдельной строки нет */}
      {captionBelow && running && !hasStages && (
        <div title={progressText ?? undefined} style={{ margin: `${SP.xxs}px 0 0`, paddingLeft: BELOW_PAD, height: CAPTION_LINE_H, lineHeight: `${CAPTION_LINE_H}px`, fontSize: FS.xs, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' }}>
          {progressText && <ProgressCaption text={progressText} />}
        </div>
      )}
      {captionBelow && (settled || aborted) && (
        <div style={{ margin: `${SP.xxs}px 0 0`, paddingLeft: BELOW_PAD, height: CAPTION_LINE_H, lineHeight: `${CAPTION_LINE_H}px`, fontSize: FS.xs, color: statusColor, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' }}>
          {status}
        </div>
      )}
      {/* Строка этапов — и пока идёт, и на закрытой карточке без раскрытия (после F5 — из
          истории). Справа тот же запас под полосу прокрутки ленты, что у шапки: иначе на 320 px
          хвост «упало K» уезжал под полосу-накладку */}
      {hasStages && (
        <div style={{ paddingLeft: BELOW_PAD, paddingRight: SP.sm, paddingBottom: SP.xxs }}>
          <StageLine stages={stages} caption={running ? stageCaption : null} />
        </div>
      )}
      {/* Полоса — последней строкой живой карточки: под подписью или этапами, к которым относится */}
      {progressPct && <ProgressMeter pct={progressPct} text={meter} />}
      {(settled || aborted) && item.totals?.failures?.length ? (
        <FailureList failures={item.totals.failures} failed={item.totals.failed} />
      ) : null}
      {/* Медиа (изображения + видео) — сразу под шапкой, без клика */}
      {hasMedia && (
        <div style={{ paddingBottom: 8, display: 'flex', flexDirection: 'column', gap: 8 }}>
          {media.map((m, i) => {
            const filename = m.fileName ?? m.url.split('/').pop()?.split('?')[0] ?? m.kind;
            return (
              <MediaBlock key={i} m={m} filename={filename} model={falModel} inferenceTime={mediaMeta.inferenceTime} costUsd={costUsd} costPending={costPending && !pendingExpired} credits={glifCredits} source={mediaMeta.source} outputType={mediaMeta.outputType} online={online} />
            );
          })}
        </div>
      )}
      {open && hasDiff && <DiffBody hunks={editHunks} />}
      {/* Команда под русской подписью — над выводом и до результата; копируется именно она */}
      {open && commandText != null && (
        <CodeBlockFrame text={commandText}>
          <pre style={{
            ...MONO_PRE_STYLE, margin: `0 0 ${SP.xs}px`, maxHeight: 160,
            background: C.termBg, color: C.termText,
          }}>
            {commandText}
          </pre>
        </CodeBlockFrame>
      )}
      {open && !hasDiff && hasResult && !hasMedia && (
        <>
          <CodeBlockFrame text={outputText}>
            <pre style={{
              ...MONO_PRE_STYLE, maxHeight: 280,
              margin: agentSplit?.tail ? '0 0 4px' : '0 0 9px',
              // Bash → тёмный терминал; остальное → светлая панель вывода
              background: isConsole ? C.termBg : C.outputBg,
              border: isConsole ? 'none' : `1px solid ${C.outputBorder}`,
              // На светлой панели ошибку красим в danger; на тёмной — светлый «терминальный» оттенок
              color: isConsole
                ? (item.isError ? C.termError : C.termText)
                : (item.isError ? C.dangerText : C.textPrimary),
            }}>
              {outputText.length > 4000 ? outputText.slice(0, 4000) + '\n…(обрезано)' : outputText}
            </pre>
          </CodeBlockFrame>
          {/* Метрики сабагента из системного хвоста — вместо сырых строк CLI */}
          {agentSplit?.tail && (
            <div style={{
              margin: '0 0 9px', display: 'flex', alignItems: 'center', gap: 5, flexWrap: 'wrap',
              fontFamily: FONT.sans, fontSize: 11, color: C.textMuted,
            }}>
              {agentSplit.tail.tokens != null && <span>{formatTailTokens(agentSplit.tail.tokens)} токенов</span>}
              {agentSplit.tail.tokens != null && (agentSplit.tail.toolUses != null || agentSplit.tail.durationMs != null) && <span>·</span>}
              {agentSplit.tail.toolUses != null && <span>{agentSplit.tail.toolUses} {toolWord(agentSplit.tail.toolUses)}</span>}
              {agentSplit.tail.toolUses != null && agentSplit.tail.durationMs != null && <span>·</span>}
              {agentSplit.tail.durationMs != null && <span>{formatTailDuration(agentSplit.tail.durationMs)}</span>}
            </div>
          )}
        </>
      )}
    </div>
  );
});
