// Витрина дизайн-системы — секция «Карточка инструмента: прогресс».
//
// Все состояния живой карточки инструмента рядом, на НАСТОЯЩЕМ ToolUseView и
// ToolGroupBlock (не макет из div): бегущая/сплошная/пунктирная полоса, «идёт M:SS»,
// «готово · M:SS», «прервано», строка подписи прогресса (сабагент, тесты, local-media,
// очередь сборок) и группа «N действий», не сворачивающаяся при живом инструменте.
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
import type { ServerMessage } from '../types';
import { ToolProgressCalmVariants } from './ToolProgressCalmVariants';

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
  const tests = (id: string, input: Record<string, unknown>, over: Partial<ToolUseItem>) =>
    tu(id, 'mcp__tests__run_tests', input, over);
  const dotnet = { target: 'backend/ClaudeHomeServer.Tests', filter: 'FullyQualifiedName~ToolProgress' };

  const groups = [
    {
      title: 'Команда (Bash)',
      demos: [
        // Старт «в будущем»: отсчёт прижат к нулю, карточка навсегда остаётся до порога 2 с
        { label: 'короче 2 с — только спиннер', item: tu('k-read', 'Read', { file_path: 'frontend/src/lib/toolTiming.ts' }, { startedAt: t0 + 24 * 3600_000 }) },
        { label: 'идёт — «идёт M:SS» и бегущая полоса, спиннер убран (место под него держится)', item: tu('k-bash-run', 'Bash', { command: 'dotnet build backend/ClaudeHomeServer.slnx' }, { startedAt: ago(AGO.bash), started: true }) },
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
        { label: 'ждёт очереди сборок — без «в очереди» и без бегущей полосы', item: tests('k-t-queue', dotnet, { startedAt: ago(AGO.queue), progress: { stage: 'queued', label: 'ждёт очереди сборок (занято 2)' } }) },
        { label: 'сборка', item: tests('k-t-build', dotnet, { startedAt: ago(AGO.build), progress: { stage: 'build', label: 'сборка' } }) },
        { label: 'подсчёт тестов', item: tests('k-t-list', dotnet, { startedAt: ago(AGO.list), progress: { stage: 'list', label: 'подсчёт тестов' } }) },
        { label: 'N из M с упавшими — сплошная, «упало K» красным, 5% видно', item: tests('k-t-run', dotnet, { startedAt: ago(AGO.tests), progress: { stage: 'running', label: '412 из 7951 · упало 2', percent: 5, exact: true } }) },
        { label: 'готово', item: tests('k-t-done', dotnet, { startedAt: t0 - 222_000, finishedAt: t0, result: 'dotnet test: все тесты прошли (код выхода 0) за 3:42.' }) },
      ],
    },
    {
      title: 'Тесты · vitest и Playwright',
      demos: [
        { label: 'vitest — файлы', item: tests('k-vitest', { kind: 'vitest', target: 'frontend', files: ['src/lib/a.test.ts', 'src/lib/b.test.ts', 'src/lib/c.test.ts'] }, { startedAt: ago(AGO.vitest), progress: { stage: 'running', label: '87 из 171 файла · упало 2', percent: 50, exact: true } }) },
        { label: 'Playwright', item: tests('k-pw', { kind: 'playwright', target: 'frontend', filter: 'офлайн' }, { startedAt: ago(AGO.pw), progress: { stage: 'running', label: '12 из 40 · упало 1', percent: 30, exact: true } }) },
        { label: 'vitest — ошибка', item: tests('k-vitest-err', { kind: 'vitest', target: 'frontend' }, { startedAt: t0 - 38_000, finishedAt: t0, isError: true, result: 'vitest: есть упавшие тесты (код выхода 1) за 0:38.' }) },
      ],
    },
    {
      title: 'Локальная генерация (local-media)',
      demos: [
        { label: 'в очереди — пустая дорожка, полоса не бежит', item: tu('k-lm-queue', 'mcp__local-media__local_jobs_wait', { job_ids: ['lm_1'] }, { startedAt: ago(AGO.queue), progress: { stage: 'queued', queuePosition: 2 } }) },
        { label: 'оценка по ETA — пунктир, «≈»', item: tu('k-lm-est', 'mcp__local-media__local_jobs_wait', { job_ids: ['lm_2'] }, { startedAt: ago(AGO.lm), progress: { stage: 'running', percent: 40, etaSeconds: 75 } }) },
        { label: 'настоящие шаги — сплошная', item: tu('k-lm-exact', 'mcp__local-media__local_jobs_wait', { job_ids: ['lm_3'] }, { startedAt: ago(AGO.lm), progress: { stage: 'running', label: 'шаг 8 из 20', percent: 40, exact: true, etaSeconds: 45 } }) },
        { label: 'готово', item: tu('k-lm-done', 'mcp__local-media__local_jobs_wait', { job_ids: ['lm_4', 'lm_5'] }, { startedAt: t0 - 95_000, finishedAt: t0, result: '{"all_done":true}' }) },
      ],
    },
  ];

  const all = groups.flatMap(g => g.demos.map(d => d.item));
  const dead = ['k-bash-dead'];
  const live = all.filter(it => it.result == null && !dead.includes(it.id)).map(it => it.id);
  // Ход оборван через 1:10 после старта прерванной карточки — её длительность
  const abortedAt = new Map([['k-bash-dead', t0]]);
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
          {/* Макет на выбор: спокойнее полоса и этапы прогона с длительностью */}
          <ToolProgressCalmVariants />
          <GroupTitle>Сейчас на бою</GroupTitle>
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
