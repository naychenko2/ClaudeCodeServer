// Витрина дизайн-системы — секция «Карточка инструмента: прогресс».
//
// Все состояния живой карточки инструмента рядом, на НАСТОЯЩЕМ ToolUseView и
// ToolGroupBlock (не макет из div): живая точка вместо бегущей полосы, с процентом — полоса на
// видимой дорожке под карточкой (сплошная — факт, пунктир — оценка) с процентом и «осталось»
// справа, «идёт M:SS», «готово · M:SS», «прервано», строка подписи прогресса (сабагент,
// local-media), строка этапов прогона тестов с итогом «174 из 177 · упало 3» и списком упавших
// на закрытой карточке и группа «N действий», не сворачивающаяся при живом инструменте.
//
// Живость задаётся тем же ToolLivenessContext, что в ленте: живые id — в live, оборванные —
// в dead. Отметки startedAt берутся от момента монтирования секции, поэтому таймеры тикают
// по-настоящему. Мобильная строка подписи включается сама на узком экране (useIsMobile).

import { useMemo, useState, type ReactNode } from 'react';
import { Timer } from 'lucide-react';
import { C, FS, SP, ISLAND, CHAT_MAX_W } from '../lib/design';
import { Island, IslandHeader } from '../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../components/ui/icons';
import { ToolUseView, type ToolUseItem } from '../components/chat/ToolUseView';
import { ToolGroupBlock } from '../components/chat/timeline';
import { ToolLivenessContext } from '../components/chat/contexts';
import { isToolGroupDone, type ToolLiveness } from '../lib/toolTiming';
import { applyServerMessage, initialChatState } from '../lib/chatReducer';
import type { ServerMessage, ToolStage } from '../types';
// Сабагент с ОДНИМ вызовом run_tests — через настоящий редьюсер и в боевом порядке событий:
// прогресс «сейчас тесты» приходит раньше, чем вызов с аргументами (FAIL Киры). Вид прогона
// в подписи обязан дорисоваться приходом вызова
function agentWithOneTestRun(startedAt: number): ToolUseItem {
  const msgs: ServerMessage[] = [
    { sessionId: 'kit', type: 'tool_use', id: 'k-agent-one', name: 'Task', input: { description: 'Проверить фронт одним прогоном' }, startedAt },
    { sessionId: 'kit', type: 'tool_started', toolUseId: 'k-agent-one', startedAt },
    { sessionId: 'kit', type: 'tool_progress', toolUseId: 'k-agent-one', stage: 'working', lastTool: 'mcp__tests__run_tests', toolUses: 1 },
    { sessionId: 'kit', type: 'tool_use', id: 'k-agent-one-rt', name: 'mcp__tests__run_tests', input: { kind: 'vitest', target: 'frontend' }, parentToolUseId: 'k-agent-one', startedAt },
  ];
  const s = msgs.reduce((acc, m) => applyServerMessage(acc, m), initialChatState());
  return s.items.find(it => it.kind === 'tool_use' && it.id === 'k-agent-one') as ToolUseItem;
}

// Сколько секунд назад «начался» каждый живой пример — чтобы таймер сразу был за порогом 2 с
const AGO = { bash: 42, agent: 95, build: 18, list: 31, tests: 154, vitest: 47, pw: 63, lm: 38, queue: 12 };

type Demo = { label: string; item: ToolUseItem };

// Набор состояний: живые (без result), завершённые (result + finishedAt) и оборванный
function buildDemos(t0: number): { groups: { title: string; demos: Demo[] }[]; live: string[]; dead: string[]; abortedAt: ReadonlyMap<string, number> } {
  const ago = (s: number) => t0 - s * 1000;
  const tu = (id: string, name: string, input: unknown, over: Partial<ToolUseItem> = {}): ToolUseItem =>
    ({ kind: 'tool_use', id, name, input, ...over });
  // Прогон уже вызван (tool_started из tools/call) — иначе карточка ждёт без «идёт»
  const tests = (id: string, input: Record<string, unknown>, over: Partial<ToolUseItem>) =>
    tu(id, 'mcp__tests__run_tests', input, { started: true, ...over });
  const dotnet = { target: 'backend/ClaudeHomeServer.Tests', filter: 'FullyQualifiedName~ToolProgress' };
  // Этапы прогона, как их шлёт сервер: [ключ, подпись, длительность с] подряд от старта;
  // последний без длительности — идёт (или оборван, если open)
  const st = (start: number, ...parts: [string, string, number | null][]): ToolStage[] => {
    let at = start;
    return parts.map(([stage, label, s]) => {
      const it: ToolStage = { stage, label, startedAt: at, ...(s != null ? { endedAt: at + s * 1000 } : {}) };
      if (s != null) at += s * 1000;
      return it;
    });
  };

  const groups = [
    {
      title: 'Команда (Bash)',
      demos: [
        // Старт «в будущем»: отсчёт прижат к нулю, карточка навсегда остаётся до порога 2 с
        { label: 'короче 2 с — только живая точка', item: tu('k-read', 'Read', { file_path: 'frontend/src/lib/toolTiming.ts' }, { startedAt: t0 + 24 * 3600_000 }) },
        { label: 'идёт — «идёт M:SS» и точка; бегущей полосы нет', item: tu('k-bash-run', 'Bash', { command: 'dotnet build backend/ClaudeHomeServer.slnx' }, { startedAt: ago(AGO.bash), started: true }) },
        { label: 'готово — шапка не съезжает влево', item: tu('k-bash-done', 'Bash', { command: 'git status --short' }, { startedAt: t0 - 15_000, finishedAt: t0, started: true, result: ' M frontend/src/dev/UiKitPage.tsx' }) },
        { label: 'прервано (ход оборван «Стопом») — с длительностью', item: tu('k-bash-dead', 'Bash', { command: 'npm run lint:design' }, { startedAt: t0 - 70_000, started: true }) },
      ],
    },
    {
      title: 'Сабагент',
      demos: [
        { label: 'идёт — «сейчас … · N действий»', item: tu('k-agent', 'Task', { description: 'Разведка: кто зовёт ToolUseView' }, { startedAt: ago(AGO.agent), started: true, progress: { stage: 'working', lastTool: 'Grep', toolUses: 7 } }) },
        { label: 'идёт — внутри гоняет тесты', item: tu('k-agent-tests', 'Task', { description: 'Прогнать фронтовые тесты' }, { startedAt: ago(AGO.agent), started: true, progress: { stage: 'working', lastTool: 'mcp__tests__run_tests', lastToolKind: 'vitest', toolUses: 3 } }) },
        { label: 'один вызов run_tests: прогресс пришёл раньше вызова — вид всё равно в подписи', item: agentWithOneTestRun(ago(AGO.agent)) },
      ],
    },
    {
      title: 'Тесты · dotnet — фазы прогона',
      demos: [
        // Несколько run_tests в одном ответе CLI выполняет по очереди: пока до вызова не дошло,
        // tool_started нет — ни «идёт», ни отсчёта, ни полосы
        { label: 'выписан, ещё не вызван — ждёт соседний вызов, без «идёт» и отсчёта', item: tests('k-t-wait', dotnet, { startedAt: ago(AGO.queue), started: false }) },
        { label: 'ждёт очереди сборок — точка, без полосы; очередь в этапах с 2 с', item: tests('k-t-queue', dotnet, { startedAt: ago(AGO.queue), progress: { stage: 'queued', label: 'ждёт очереди сборок (занято 2)' }, stages: st(ago(AGO.queue), ['queued', 'очередь', null]) }) },
        { label: 'сборка — после очереди', item: tests('k-t-build', dotnet, { startedAt: ago(AGO.build), progress: { stage: 'build', label: 'сборка' }, stages: st(ago(AGO.build), ['queued', 'очередь', 12], ['build', 'сборка', null]) }) },
        { label: 'подсчёт тестов — уже этап «тесты», без процента', item: tests('k-t-list', dotnet, { startedAt: ago(AGO.list + 102), progress: { stage: 'list', label: 'подсчёт тестов' }, stages: st(ago(AGO.list + 102), ['build', 'сборка', 102], ['running', 'тесты', null]) }) },
        { label: 'N из M с упавшими — полоса строкой ниже, «упало K» красным; «осталось» — с 10% и 15 с этапа', item: tests('k-t-run', dotnet, { startedAt: ago(AGO.tests), progress: { stage: 'running', label: '412 из 7951 · упало 2', percent: 5, exact: true }, stages: st(ago(AGO.tests), ['build', 'сборка', 102], ['running', 'тесты', null]) }) },
        // Перевал заниженного общего числа (--list-tests показал теорию одной строкой): «из M» и
        // процента нет — полоса ушла, живая точка
        { label: 'перевал общего числа — без «из M» и без полосы', item: tests('k-t-over', dotnet, { startedAt: ago(AGO.tests), progress: { stage: 'running', label: '1312 · упало 1' }, stages: st(ago(AGO.tests), ['build', 'сборка', 102], ['running', 'тесты', null]) }) },
        { label: 'середина прогона — «осталось ≈» по темпу этапа', item: tests('k-t-mid', dotnet, { startedAt: ago(AGO.tests), progress: { stage: 'running', label: '3180 из 7951', percent: 40, exact: true }, stages: st(ago(AGO.tests), ['build', 'сборка', 102], ['running', 'тесты', null]) }) },
        // Длинный фильтр (как на бою): аргумент режется многоточием, «идёт M:SS» справа — никогда
        { label: 'длинный фильтр — аргумент режется первым, таймер целиком', item: tests('k-t-long', { target: 'backend/ClaudeHomeServer.Tests', filter: 'FullyQualifiedName~DevServer|FullyQualifiedName~DevServerPortMemory|FullyQualifiedName~DevServerLaunchPolicy|FullyQualifiedName~ProjectServicesApi' }, { startedAt: ago(AGO.tests), progress: { stage: 'running', label: '412 из 7951 · упало 2', percent: 5, exact: true }, stages: st(ago(AGO.tests), ['queued', 'очередь', 7], ['build', 'сборка', 102], ['running', 'тесты', null]) }) },
        // Длинные счётчики на 320 px: прошедшие этапы режутся многоточием, «упало K» справа —
        // никогда и не уезжает под полосу прокрутки ленты
        { label: 'длинные счётчики — режутся прошедшие этапы, «упало K» целиком', item: tests('k-t-long-counts', dotnet, { startedAt: ago(AGO.tests), progress: { stage: 'running', label: '7912 из 7951 · упало 12', percent: 99, exact: true }, stages: st(ago(AGO.tests), ['queued', 'очередь', 7], ['build', 'сборка', 102], ['running', 'тесты', null]) }) },
        { label: 'готово с упавшими — счётчики, этапы и упавшие без раскрытия', item: tests('k-t-done-fail', dotnet, { startedAt: t0 - 135_000, finishedAt: t0, result: 'dotnet test: есть упавшие тесты (код выхода 1) за 2:15.', stages: st(t0 - 135_000, ['build', 'сборка', 62], ['running', 'тесты', 73]), totals: { passed: 172, failed: 5, total: 177, failures: [
          { name: 'ClaudeHomeServer.Tests.Services.TestRuns.TestRunStagesTests.Итог_ИзОтчётов_СуммаПоСборкам', message: 'Expected TestRunStages.Totals(r) to be ToolRunTotals { Passed = 174 }, but found 173.' },
          { name: 'ClaudeHomeServer.Tests.Services.TurnAccumulatorTests.ЭтапыПереживаютF5', message: 'System.NullReferenceException: Object reference not set to an instance of an object.' },
          { name: 'ClaudeHomeServer.Tests.Mcp.McpToolsetStabilityTests.СоставНеЗависитОтХода' },
        ] } }) },
        { label: 'готово, всё прошло', item: tests('k-t-done', dotnet, { startedAt: t0 - 235_000, finishedAt: t0, result: 'dotnet test: все тесты прошли (код выхода 0) за 3:55.', stages: st(t0 - 235_000, ['build', 'сборка', 102], ['running', 'тесты', 133]), totals: { passed: 7951, failed: 0, total: 7951 } }) },
        { label: 'прервано на сборке — этап крестиком', item: tests('k-t-dead', dotnet, { startedAt: t0 - 77_000, stages: st(t0 - 77_000, ['queued', 'очередь', 12], ['build', 'сборка', 65]).map((s, i) => i === 1 ? { ...s, failed: true } : s) }) },
      ],
    },
    {
      title: 'Тесты · vitest и Playwright',
      demos: [
        { label: 'vitest — файлы', item: tests('k-vitest', { kind: 'vitest', target: 'frontend', files: ['src/lib/a.test.ts', 'src/lib/b.test.ts', 'src/lib/c.test.ts'] }, { startedAt: ago(AGO.vitest), progress: { stage: 'running', label: '87 из 171 файла · упало 2', percent: 50, exact: true }, stages: st(ago(AGO.vitest), ['running', 'тесты', null]) }) },
        { label: 'Playwright — стенд поднят из webServer', item: tests('k-pw', { kind: 'playwright', target: 'frontend', filter: 'офлайн' }, { startedAt: ago(AGO.pw), progress: { stage: 'running', label: '12 из 40 · упало 1', percent: 30, exact: true }, stages: st(ago(AGO.pw), ['stand', 'стенд', 9], ['running', 'тесты', null]) }) },
        { label: 'vitest — ошибка', item: tests('k-vitest-err', { kind: 'vitest', target: 'frontend' }, { startedAt: t0 - 38_000, finishedAt: t0, isError: true, result: 'vitest: есть упавшие тесты (код выхода 1) за 0:38.' }) },
      ],
    },
    {
      title: 'Локальная генерация (local-media)',
      demos: [
        { label: 'в очереди — только точка, полосы нет', item: tu('k-lm-queue', 'mcp__local-media__local_jobs_wait', { job_ids: ['lm_1'] }, { startedAt: ago(AGO.queue), progress: { stage: 'queued', queuePosition: 2 } }) },
        { label: 'оценка по ETA — полоса пунктиром, «≈»', item: tu('k-lm-est', 'mcp__local-media__local_jobs_wait', { job_ids: ['lm_2'] }, { startedAt: ago(AGO.lm), progress: { stage: 'running', percent: 40, etaSeconds: 75 } }) },
        { label: 'настоящие шаги — сплошная полоса', item: tu('k-lm-exact', 'mcp__local-media__local_jobs_wait', { job_ids: ['lm_3'] }, { startedAt: ago(AGO.lm), progress: { stage: 'running', label: 'шаг 8 из 20', percent: 40, exact: true, etaSeconds: 45 } }) },
        { label: 'готово', item: tu('k-lm-done', 'mcp__local-media__local_jobs_wait', { job_ids: ['lm_4', 'lm_5'] }, { startedAt: t0 - 95_000, finishedAt: t0, result: '{"all_done":true}' }) },
      ],
    },
  ];

  const all = groups.flatMap(g => g.demos.map(d => d.item));
  const dead = ['k-bash-dead', 'k-t-dead'];
  const live = all.filter(it => it.result == null && !dead.includes(it.id)).map(it => it.id);
  // Ход оборван в момент монтирования — длительность прерванных карточек до него
  const abortedAt = new Map(dead.map(id => [id, t0]));
  return { groups, live, dead, abortedAt };
}

// Подпись состояния над карточкой — как SubBlock, но мельче: карточка сама по себе мелкая
function StateRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div style={{ borderTop: `1px solid ${C.bgInset}`, paddingTop: SP.xs }}>
      <div style={{ fontSize: FS.xs, color: C.textMuted, opacity: 0.8 }}>{label}</div>
      {children}
    </div>
  );
}

function GroupTitle({ children }: { children: ReactNode }) {
  return <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: SP.sm }}>{children}</div>;
}

export function ToolProgressSection() {
  // Момент монтирования — точка отсчёта всех примеров
  const [t0] = useState(() => Date.now());
  const { groups, live, dead, abortedAt } = useMemo(() => buildDemos(t0), [t0]);

  // Группа «N действий» с живой карточкой: после неё уже встал видимый элемент, но живой
  // вызов держит её раскрытой. Рядом — такая же, но завершённая: сворачивается
  const groupLive = useMemo<ToolUseItem[]>(() => [
    { kind: 'tool_use', id: 'k-g-read', name: 'Read', input: { file_path: 'frontend/src/lib/design.ts' }, startedAt: t0 - 3_000, finishedAt: t0 - 2_800, result: '…' },
    { kind: 'tool_use', id: 'k-g-grep', name: 'Grep', input: { pattern: 'ProgressBar' }, startedAt: t0 - 2_500, finishedAt: t0 - 2_400, result: '…' },
    { kind: 'tool_use', id: 'k-g-bash', name: 'Bash', input: { command: 'npx tsc -b' }, startedAt: t0 - 27_000, started: true },
  ], [t0]);
  const groupDone = useMemo<ToolUseItem[]>(() => groupLive.map(it => it.result == null
    ? { ...it, id: 'k-gd-bash', finishedAt: t0, result: '' }
    : { ...it, id: it.id.replace('k-g-', 'k-gd-') }), [groupLive, t0]);

  const liveness: ToolLiveness = useMemo(
    () => ({ live: new Set([...live, 'k-g-bash']), dead: new Set(dead), abortedAt }),
    [live, dead, abortedAt]);
  const liveGroupDone = isToolGroupDone({ entries: groupLive, liveness, hasVisibleAfter: true, busy: true, awaitingPermission: false });
  const doneGroupDone = isToolGroupDone({ entries: groupDone, liveness, hasVisibleAfter: true, busy: true, awaitingPermission: false });

  return (
    <Island>
      <IslandHeader
        icon={<Timer size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ color: C.accent, flexShrink: 0 }} />}
        title="Карточка инструмента: прогресс"
        badge="chat/ToolUseView"
      />
      <ToolLivenessContext.Provider value={liveness}>
        {/* Ширина — как у колонки ленты чата: карточку смотрим в её настоящей ширине */}
        <div data-kit="tool-progress" style={{ padding: ISLAND.pad, maxWidth: CHAT_MAX_W, display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          {groups.map(g => (
            <div key={g.title} style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
              <GroupTitle>{g.title}</GroupTitle>
              {g.demos.map(d => (
                <StateRow key={d.item.id} label={d.label}>
                  <ToolUseView item={d.item} />
                </StateRow>
              ))}
            </div>
          ))}

          <GroupTitle>Группа «N действий»</GroupTitle>
          <StateRow label="живая карточка внутри — группа не сворачивается, хотя после неё уже есть ответ">
            <ToolGroupBlock isGroupDone={liveGroupDone} toolCount={groupLive.length}>
              {groupLive.map((it, i) => (
                <div key={it.id} style={i ? { borderTop: `1px solid ${C.bgInset}` } : undefined}><ToolUseView item={it} /></div>
              ))}
            </ToolGroupBlock>
          </StateRow>
          <StateRow label="та же группа, все вызовы завершены — свёрнута">
            <ToolGroupBlock isGroupDone={doneGroupDone} toolCount={groupDone.length}>
              {groupDone.map((it, i) => (
                <div key={it.id} style={i ? { borderTop: `1px solid ${C.bgInset}` } : undefined}><ToolUseView item={it} /></div>
              ))}
            </ToolGroupBlock>
          </StateRow>
        </div>
      </ToolLivenessContext.Provider>
    </Island>
  );
}
