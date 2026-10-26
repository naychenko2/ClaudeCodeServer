// Пометки «в архиве» / «задача закрыта» на переходах «чат / задача» (волна 3, Ф9b).
//
// ТОЛЬКО рендер с настоящими данными (run / chats / events / stats), не прямой проп
// chatArchived/taskClosed: пометка обязана ВЫТЕКАТЬ из данных в каждом месте, где есть
// NavTargetButton/NavTargetPair. Три эталонные строки (одинаково во всех местах):
//   A — чат живой,  задача открыта   → без пометок;
//   B — чат В АРХИВЕ, задача открыта → только «в архиве»;
//   C — чат живой,  задача ЗАКРЫТА   → только «задача закрыта».
// Разведённые строки ловят оба дефекта по отдельности (A/B/C не дают «всё или ничего»).
//
// Места: Хронология (исполнители этапов · меню карточек «Производства», W4-2F3),
// Чаты (живые · архивные), Задачи, События,
// Статистика (кнопки задач · «Дольше всех» · разрез), Планы (читалка · макет), тревога
// страницы (FeaturePageAlert), попап/панель (FeatureRunView), шторка (FeatureBannerSheet).
//
// Мутации (в отчёте):
//   (1) taskClosed не заполнен в месте X → строка C этого места краснеет;
//   (2) archived игнорируется (chatArchived не передан / marks.chatArchived → false) →
//       строка B краснеет;
//   (3) buildNavMarks не учитывает status 'done' / chats[].archived → red во всех местах.

import { act, createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect, afterEach, vi } from 'vitest';
import { FeatureTimelineTab } from './FeatureTimelineTab';
import { FeatureChatsTab } from './FeatureChatsTab';
import { FeatureTasksTab } from './FeatureTasksTab';
import { FeatureEventsTab } from './FeatureEventsTab';
import { FeatureStatsTab } from './FeatureStatsTab';
import { FeaturePlanReader } from './FeaturePlanReader';
import { MockupCard } from './FeatureMockups';
import { FeaturePageAlert } from './FeaturePageHeader';
import { FeatureRunView } from '../chat/feature/FeatureRunView';
import { FeatureBannerSheet } from '../chat/feature/FeatureBannerSheet';
import { buildNavMarks } from '../../lib/featureNavMarks';
import { __test as backNav } from '../../lib/featureBackNav';
import { __test as pageStore, setFeaturePageTab } from '../../lib/featurePage';
import { mount, installFakeDom, uninstallFakeDom, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureRun } from '../../types/featureRun';
import type { FeatureChatLink } from '../../types/featureChats';
import type { FeatureEvent } from '../../types/featureIndex';
import type { FeatureStats } from '../../types/featureStats';
import type { FeaturePlan, FeatureMockup } from '../../types/featurePlans';

// === Моки окружения (как у соседних тестов вкладок) ===
vi.mock('../../lib/api', () => ({ api: { personas: { avatarUrl: () => '' } } }));
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});
vi.mock('../../lib/toast', () => ({ showToast: () => {} }));
vi.mock('../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/personas')>();
  return {
    ...orig,
    usePersonas: () => [],
    usePersonasVersion: () => 0,
    getPersonaById: () => undefined,
    ensurePersonasLoaded: async () => {},
  };
});
vi.mock('../../lib/models', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/models')>();
  return { ...orig, useModels: () => [], modelLabel: (v: string | null | undefined) => v ?? '' };
});
vi.mock('../../lib/openChat', () => ({ openChatById: async () => true }));
// Полный мок через importOriginal (GC-F11R): FeatureStepDetails («Итог» карточки) зовёт
// useTasks/useEnsureFullTask — тесты здесь про пометки, не про «Итог», поэтому
// useEnsureFullTask остаётся инертным (реальный звал бы api.tasks.get, которого нет
// в местном моке api, и менял бы общий стор между тестами). Следующие новые экспорты
// lib/tasks этот мок не сломают.
vi.mock('../../lib/tasks', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/tasks')>();
  return {
    ...orig,
    openTaskInSection: () => {},
    useEnsureFullTask: () => ({ state: 'ready' as const, retry: () => {} }),
  };
});

// === Обход дерева fakeDom ===
type N = { nodeType: number; nodeName: string; childNodes: N[]; getAttribute?: (a: string) => string | null };
const attr = (n: N, a: string): string => (n.nodeType === 1 ? n.getAttribute?.(a) ?? '' : '') ?? '';
function collect(root: N, pred: (n: N) => boolean, out: N[] = []): N[] {
  if (root.nodeType === 1 && pred(root)) out.push(root);
  for (const c of root.childNodes) collect(c, pred, out);
  return out;
}
const byId = (root: N, id: string) => collect(root, n => attr(n, 'data-testid') === id);
const byIdPrefix = (root: N, prefix: string) => collect(root, n => attr(n, 'data-testid').startsWith(prefix));
function textOf(n: N): string {
  if (n.nodeType === 3) return (n as unknown as { nodeValue: string }).nodeValue;
  return n.childNodes.map(textOf).join('');
}

// Состояние перехода в строке/блоке: первый чат и первая задача в поддереве.
interface Flags { chat: 'live' | 'archived' | 'none'; task: 'open' | 'closed' | 'none'; chatText: string; taskText: string }
function flagsOf(root: N): Flags {
  const chat = byId(root, 'nav-target-chat')[0];
  const task = byId(root, 'nav-target-task')[0];
  const noChat = byId(root, 'nav-target-no-chat')[0];
  const aria = (b: N | undefined) => (b ? attr(b, 'aria-label') : '');
  return {
    chat: chat ? (aria(chat).includes('(чат в архиве)') ? 'archived' : 'live') : (noChat ? 'none' : 'none'),
    task: task ? (aria(task).includes('(задача закрыта)') ? 'closed' : 'open') : 'none',
    chatText: chat ? textOf(chat) : '',
    taskText: task ? textOf(task) : '',
  };
}
// Эталоны: поведение пометки — по aria-label И по видимому тексту (десктоп).
const A = { chat: 'live', task: 'open' } as const;
const B = { chat: 'archived', task: 'open' } as const;
const C = { chat: 'live', task: 'closed' } as const;
function expectFlags(root: N, want: { chat: Flags['chat']; task: Flags['task'] }, where: string) {
  const f = flagsOf(root);
  expect(f.chat, `${where}: пометка чата`).toBe(want.chat);
  expect(f.task, `${where}: пометка задачи`).toBe(want.task);
  // Видимая подпись (десктоп): «в архиве» только у архивного чата, «закрыта» только у закрытой задачи.
  expect(f.chatText.includes('в архиве'), `${where}: видимое «в архиве»`).toBe(want.chat === 'archived');
  expect(f.taskText.includes('закрыта'), `${where}: видимое «закрыта»`).toBe(want.task === 'closed');
}

// === Данные ===
// Задачи: t-a (открыта, чат живой c-live), t-b (открыта, чат c-arch в архиве), t-c (done, чат живой).
const task = (id: string, status: 'todo' | 'inProgress' | 'done', over: Record<string, unknown> = {}) =>
  ({ id, title: id, status, projectId: 'p1', ...over });
const ex = (taskId: string, stepRef: string, chatId: string, chatArchived: boolean, status: 'running' | 'done') => ({
  personaId: null, taskId, taskRole: 'step', stepRef, lane: null, status,
  chatId, chatArchived, modelLabel: null, modelFull: null,
});
const chain = (stepRef: string, taskId: string, chatId: string, rounds: unknown[] = []) => ({
  stepRef, title: taskId, taskIds: [taskId], outcome: 'running', status: 'идёт', durationMinutes: 5,
  loops: 0, rounds, chatId, personaId: null,
});

function mkRun(over: Record<string, unknown> = {}): FeatureRun {
  return {
    featureId: 'f1', state: 'running',
    epic: task('f1', 'inProgress', { title: 'Моя фича' }),
    steps: [
      task('t-a', 'inProgress', { role: 'step', stepRef: 'Ш1', linkedSessionId: 'c-live' }),
      task('t-b', 'inProgress', { role: 'step', stepRef: 'Ш2', linkedSessionId: 'c-arch' }),
      task('t-c', 'done', { role: 'step', stepRef: 'Ш3', linkedSessionId: 'c-live' }),
    ],
    reviews: [task('t-r1', 'inProgress', { role: 'review' }), task('t-r2', 'done', { role: 'review' })],
    qa: [], final: task('t-fin', 'done', { role: 'final', linkedSessionId: 'c-arch' }), merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false,
    stages: [{
      key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: ['t-a', 't-b', 't-c'], rounds: [],
      firstStartedAt: null, lastCompletedAt: null, barEnd: null, approxMinutes: 0, overlapsActive: false,
      executors: [
        ex('t-a', 'Ш1', 'c-live', false, 'running'),
        ex('t-b', 'Ш2', 'c-arch', true, 'running'),
        ex('t-c', 'Ш3', 'c-live', false, 'done'),
      ],
    }],
    stepChains: [
      chain('Ш1', 't-a', 'c-live', [
        { n: 1, verdict: 'returned', reviewTaskId: 't-r1', reviewChatId: 'c-arch' },   // B
        { n: 2, verdict: 'accepted', reviewTaskId: 't-r2', reviewChatId: 'c-live' },  // C
      ]),
      chain('Ш2', 't-b', 'c-arch'),
      chain('Ш3', 't-c', 'c-live'),
    ],
    ...over,
  } as unknown as FeatureRun;
}

const chatLink = (id: string, taskId: string | null, archived: boolean): FeatureChatLink => ({
  id, kind: 'executor', taskId, archived, personaId: null, taskRole: null, stepRef: null,
  lane: null, modelLabel: null, modelFull: null,
});
// Чаты страницы: A (c-live·t-a), B (c-arch·t-b, архивный), C (c-c·t-c).
const CHATS: FeatureChatLink[] = [
  chatLink('c-live', 't-a', false),
  chatLink('c-arch', 't-b', true),
  chatLink('c-c', 't-c', false),
];

const evt = (kind: string, taskId: string, sessionId: string): FeatureEvent => ({
  at: '2026-10-20T10:00:00Z', kind, taskId, role: null, stepRef: null, personaId: null,
  sessionId, round: null, reason: null,
} as unknown as FeatureEvent);

// === Рендер ===
const roots: import('react-dom/client').Root[] = [];
function render(el: Parameters<typeof mount>[0]): N {
  installFakeDom();
  const { root, container } = mount(el);
  roots.push(root);
  return container as unknown as N;
}
afterEach(() => {
  for (const r of roots) act(() => { r.unmount(); });
  roots.length = 0;
  uninstallFakeDom();
});

// Компактная карточка свёрнута: кнопка кода с меню живёт в раскрытой части — сначала раскрываем.
function openCardMenu(c: N, stepRef: string) {
  act(() => { (reactProps(byId(c, `prod-card-${stepRef}`)[0] as never)!.onClick as () => void)(); });
  act(() => { (reactProps(byId(c, `prod-code-${stepRef}`)[0] as never)!.onClick as () => void)(); });
}

// ═══ Хронология ══════════════════════════════════════════════════════════════
// «Производство» — лента карточек (W4-2F3): строки исполнителей остаются в других
// этапах, переходы шага живут в меню карточки («старт · чат · задача») — пометки
// проверяются там.
describe('пометки: Хронология', () => {
  const EXEC = [
    ex('t-a', 'Ш1', 'c-live', false, 'running'),
    ex('t-b', 'Ш2', 'c-arch', true, 'running'),
    ex('t-c', 'Ш3', 'c-live', false, 'done'),
  ];

  it('исполнители этапа «Ревью»: A / B / C из данных run (без marks — из самого агрегата)', () => {
    const run = mkRun({
      stages: [
        { key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: ['t-a', 't-b', 't-c'], rounds: [],
          firstStartedAt: null, lastCompletedAt: null, barEnd: null, approxMinutes: 0, overlapsActive: false, executors: EXEC },
        { key: 'review', state: 'active', tone: 'run', passes: 0, taskIds: [], rounds: [],
          firstStartedAt: null, lastCompletedAt: null, barEnd: null, approxMinutes: 0, overlapsActive: false, executors: EXEC },
      ],
    });
    const c = render(createElement(FeatureTimelineTab, { run }));
    // «Производство» раскрыто (текущий): строк исполнителей там нет — лента карточек,
    // по одной карточке на цепочку.
    const prodBody = byId(c, 'timeline-stage-body-prod')[0];
    expect(byId(prodBody, 'timeline-executor').length).toBe(0);
    expect(byId(prodBody, 'prod-card-Ш1').length + byId(prodBody, 'prod-card-Ш2').length + byId(prodBody, 'prod-card-Ш3').length).toBe(3);
    // «Ревью» открываем кликом (один этап за раз) — строки исполнителей здесь.
    act(() => { (reactProps(byId(c, 'timeline-stage-header-review')[0] as never)!.onClick as () => void)(); });
    const rows = byId(byId(c, 'timeline-stage-body-review')[0], 'timeline-executor');
    expect(rows.length).toBe(3);
    expectFlags(rows[0], A, 'исполнитель Ш1');
    expectFlags(rows[1], B, 'исполнитель Ш2');
    expectFlags(rows[2], C, 'исполнитель Ш3');
  });

  it('меню карточки: «чат» помечен «в архиве» (чат шага архивный в агрегате)', () => {
    const c = render(createElement(FeatureTimelineTab, { run: mkRun() }));
    // Ш2: чат шага c-arch — chatArchived исполнителя в агрегате.
    openCardMenu(c, 'Ш2');
    const body = (globalThis as { document: { body: never } }).document.body as N;
    expect(textOf(byId(body, 'prod-menu-chat')[0]).includes('в архиве'), 'Ш2: видимое «в архиве»').toBe(true);
  });

  it('меню карточки: «задача» помечена «закрыта» (задача шага done)', () => {
    const c = render(createElement(FeatureTimelineTab, { run: mkRun() }));
    // Ш3: задача шага t-c — status done → «закрыта».
    openCardMenu(c, 'Ш3');
    const body = (globalThis as { document: { body: never } }).document.body as N;
    expect(textOf(byId(body, 'prod-menu-task')[0]).includes('закрыта'), 'Ш3: видимое «закрыта»').toBe(true);
  });

  it('marks из страницы (с chats) перекрывают расчёт по агрегату: чат без executors помечен по chats[]', () => {
    // В агрегате c-extra не встречается — архивность известна только из chats страницы.
    const run = mkRun({ stepChains: [chain('Ш1', 't-a', 'c-extra')] });
    const marks = buildNavMarks(run, [chatLink('c-extra', 't-a', true)]);
    const c = render(createElement(FeatureTimelineTab, { run, marks }));
    openCardMenu(c, 'Ш1');
    const body = (globalThis as { document: { body: never } }).document.body as N;
    expect(textOf(byId(body, 'prod-menu-chat')[0]).includes('в архиве'), 'меню: «в архиве» по marks страницы').toBe(true);
  });
});

// ═══ Чаты ════════════════════════════════════════════════════════════════════
describe('пометки: Чаты', () => {
  const page = () => ({ run: mkRun(), events: [], eventsTotal: 0, chats: CHATS }) as never;

  it('живые строки: A и C; архивная (после раскрытия «Архивные») — B', () => {
    const c = render(createElement(FeatureChatsTab, { page: page(), featureId: 'f1' }));
    expectFlags(byId(c, 'chat-row-c-live')[0], A, 'чат A');
    expectFlags(byId(c, 'chat-row-c-c')[0], C, 'чат C');
    const arch = byId(c, 'chats-archived')[0];
    const toggle = collect(arch, n => n.nodeName === 'BUTTON')[0];
    act(() => { (reactProps(toggle as never)!.onClick as () => void)(); });
    expectFlags(byId(c, 'chat-row-c-arch')[0], B, 'чат B');
  });
});

// ═══ Задачи ══════════════════════════════════════════════════════════════════
describe('пометки: Задачи', () => {
  it('строки задач: A / B / C (чат — linkedSessionId, задача — по status)', () => {
    const c = render(createElement(FeatureTasksTab, { run: mkRun(), featureId: 'f1' }));
    expectFlags(byId(c, 'task-row-t-a')[0], A, 'задача A');
    expectFlags(byId(c, 'task-row-t-b')[0], B, 'задача B');
    expectFlags(byId(c, 'task-row-t-c')[0], C, 'задача C');
  });
});

// ═══ События ═════════════════════════════════════════════════════════════════
describe('пометки: События', () => {
  it('строки событий: A / B / C (marks страницы: задачи run + чаты)', () => {
    const run = mkRun();
    const marks = buildNavMarks(run, CHATS);
    const events = [evt('started', 't-a', 'c-live'), evt('stopped', 't-b', 'c-arch'), evt('accepted', 't-c', 'c-live')];
    const c = render(createElement(FeatureEventsTab, { events, eventsTotal: 3, featureId: 'f1', marks }));
    expectFlags(byIdPrefix(c, 'event-row-started-t-a-')[0], A, 'событие A');
    expectFlags(byIdPrefix(c, 'event-row-stopped-t-b-')[0], B, 'событие B');
    expectFlags(byIdPrefix(c, 'event-row-accepted-t-c-')[0], C, 'событие C');
  });
});

// ═══ Статистика ══════════════════════════════════════════════════════════════
function mkStats(): FeatureStats {
  const seg = (from: string, to: string, stage: string, taskId: string) =>
    ({ from, to, kind: 'work', track: 'main', stage, taskId });
  return {
    source: 'journal', truncated: false,
    timeline: {
      from: '2026-10-19T17:20:00Z', to: '2026-10-20T12:30:00Z',
      stages: [
        { key: 'plan', startedAt: '2026-10-19T17:58:00Z', barEnd: '2026-10-19T18:30:00Z' },
        { key: 'prod', startedAt: '2026-10-20T10:05:00Z', barEnd: null },
      ],
      segments: [
        seg('2026-10-19T17:58:00Z', '2026-10-19T18:30:00Z', 'plan', 't-b'),   // открыта
        seg('2026-10-20T10:05:00Z', '2026-10-20T11:00:00Z', 'prod', 't-c'),   // закрыта
      ],
      current: null,
      totals: {
        elapsedMinutes: 0, workWallMinutes: 0, agentsSumMinutes: 0, notWorkingMinutes: 0, waitingMinutes: null,
        idleMinutes: null, waitingCount: null, longestWaitMinutes: null, errorCount: null, errorMinutes: null,
      },
    },
    breakdown: {
      personas: [{ key: 'p1', minutes: 10, tokens: null, costUsd: null, chatIds: ['c-live', 'c-arch'] }],
      models: [], roles: [],
    },
    longestNow: { taskId: 't-b', chatId: 'c-arch', personaId: null, stepRef: 'Ш2', minutes: 70, tokens: null, share: null },
    spend: null,
  } as unknown as FeatureStats;
}

describe('пометки: Статистика', () => {
  const stats = mkStats();
  const marks = () => buildNavMarks(mkRun(), CHATS);

  it('кнопки задач по этапам: plan→t-b открыта, prod→t-c закрыта', () => {
    const c = render(createElement(FeatureStatsTab, { stats, featureId: 'f1', marks: marks() }));
    const btns = byId(byId(c, 'stats-tasks')[0], 'nav-target-task');
    expect(btns.map(b => attr(b, 'aria-label').includes('(задача закрыта)'))).toEqual([false, true]);
  });

  // Вкладка «Пульс» (ST-F14a) рисуется только с run: блоки «Сейчас работают»,
  // «Главные потери» и разрез «Кто и на чём работал» получают пометки и контекст возврата.
  const NOW_ROW = { taskId: 't-b', stage: 'prod', personaId: null, stepRef: 'Ш2', title: 't-b', startedAt: '2026-10-20T10:00:00Z', linkedSessionId: 'c-arch' };
  const pulseStats = (): FeatureStats => {
    const st = mkStats();
    st.breakdown.personas.push({ key: 'p2', minutes: 5, tokens: null, costUsd: null, chatIds: [] } as never);
    return st;
  };
  const renderTab = (extra: Record<string, unknown> = {}) => render(createElement(FeatureStatsTab, {
    stats: pulseStats(), run: mkRun({ now: [NOW_ROW] }), featureId: 'f1', featureTitle: 'Моя фича', marks: marks(), ...extra,
  }));
  const click = (n: N) => act(() => { (reactProps(n as never)!.onClick as () => void)(); });
  const expectBack = (to: { kind: 'chat' | 'task'; id: string }) => {
    expect(backNav.getBackNav()).toMatchObject({
      from: 'f1', fromKind: 'feature', fromTab: 'stats', fromTitle: 'Моя фича', to,
    });
  };

  it('«Дольше всех сейчас» (карточка 3 «Потерь»): чат c-arch в архиве, задача t-b открыта → B', () => {
    const c = renderTab();
    expectFlags(byId(c, 'loss-longest-nav')[0], B, 'дольше всех');
  });

  it('«Дольше всех сейчас»: клик по «чат» и по «задача» пишет точку возврата с вкладкой «Статистика»', () => {
    pageStore.reset(); backNav.reset();
    setFeaturePageTab('stats');
    const c = renderTab();
    const nav = byId(c, 'loss-longest-nav')[0];
    click(byId(nav, 'nav-target-chat')[0]);
    expectBack({ kind: 'chat', id: 'c-arch' });
    backNav.reset();
    click(byId(nav, 'nav-target-task')[0]);
    expectBack({ kind: 'task', id: 't-b' });
  });

  it('«Сейчас работают»: чат c-arch помечен «в архиве»', () => {
    const c = renderTab();
    expectFlags(byId(c, 'now-row-t-b')[0], { chat: 'archived', task: 'none' }, 'сейчас работают');
  });

  it('«Сейчас работают»: клик по чату пишет точку возврата с вкладкой «Статистика»', () => {
    pageStore.reset(); backNav.reset();
    setFeaturePageTab('stats');
    const c = renderTab();
    click(byId(byId(c, 'now-row-t-b')[0], 'nav-target-chat')[0]);
    expectBack({ kind: 'chat', id: 'c-arch' });
  });

  it('разрез (чаты группы): c-live живой, c-arch в архиве', () => {
    const c = renderTab();
    click(byId(c, 'breakdown-chats-toggle-p1')[0]);
    expect(byIdPrefix(c, 'breakdown-chat-').length, 'строк чатов группы').toBe(2);
    const live = byId(c, 'breakdown-chat-c-live')[0];
    const arch = byId(c, 'breakdown-chat-c-arch')[0];
    expect([flagsOf(live).chat, flagsOf(arch).chat]).toEqual(['live', 'archived']);
  });

  it('клик по чату в разрезе пишет точку возврата с вкладкой «Статистика»', () => {
    pageStore.reset(); backNav.reset();
    setFeaturePageTab('stats');
    const c = renderTab();
    click(byId(c, 'breakdown-chats-toggle-p1')[0]);
    click(byId(byId(c, 'breakdown-chat-c-arch')[0], 'nav-target-chat')[0]);
    expectBack({ kind: 'chat', id: 'c-arch' });
  });

  it('разрез по персонам: недоступная персона — «персона недоступна», не сырой UUID', () => {
    const st = mkStats();
    st.breakdown.personas = [
      { key: '11111111-aaaa-bbbb-cccc-000000000001', minutes: 20, tokens: null, costUsd: null, chatIds: [] },
      { key: '22222222-aaaa-bbbb-cccc-000000000002', minutes: 10, tokens: null, costUsd: null, chatIds: [] },
    ] as never;
    const c = renderTab({ stats: st });
    const text = textOf(byId(c, 'feature-breakdown-block')[0]);
    expect(text).toContain('персона недоступна');
    expect(text).not.toContain('11111111-aaaa');
    expect(text).not.toContain('22222222-aaaa');
  });
});

// ═══ Планы и макеты ══════════════════════════════════════════════════════════
describe('пометки: Планы и макеты', () => {
  const plan = (taskId: string): FeaturePlan => ({
    taskId, role: 'plan', title: 'План', authorPersonaId: null, createdAt: '2026-10-20T10:00:00Z',
    completedAt: null, hasText: false, mockupPaths: [],
  });
  const marks = () => buildNavMarks(mkRun(), CHATS);

  it('читалка плана «без текста»: ВСЕ кнопки задачи (шапка + блок «без текста») помечены по данным', () => {
    const taskFlags = (c: N) => byId(c, 'nav-target-task').map(b => attr(b, 'aria-label').includes('(задача закрыта)'));
    const open = render(createElement(FeaturePlanReader, { plan: plan('t-a'), mockups: [], marks: marks() }));
    expect(taskFlags(open)).toEqual([false, false]);
    const closed = render(createElement(FeaturePlanReader, { plan: plan('t-c'), mockups: [], marks: marks() }));
    expect(taskFlags(closed)).toEqual([true, true]);
  });

  it('карточка макета: задача макета закрыта / открыта', () => {
    const mk = (taskId: string): FeatureMockup => ({
      path: 'docs/m.html', title: 'Макет', status: null, files: [], source: 'header',
      taskId, authorPersonaId: null, modifiedAt: null,
    });
    const closed = render(createElement(MockupCard, { mockup: mk('t-c'), plans: [], marks: marks() }));
    expect(flagsOf(closed).task).toBe('closed');
    const open = render(createElement(MockupCard, { mockup: mk('t-a'), plans: [], marks: marks() }));
    expect(flagsOf(open).task).toBe('open');
  });
});

// ═══ Тревога страницы, попап/панель, шторка ══════════════════════════════════
function navFlagsFromHtml(html: string): { chats: boolean[]; tasks: boolean[] } {
  const tags = html.match(/<button[^>]*data-testid="nav-target-(?:chat|task)"[^>]*>/g) ?? [];
  const chats: boolean[] = [];
  const tasks: boolean[] = [];
  for (const t of tags) {
    const aria = /aria-label="([^"]*)"/.exec(t)?.[1] ?? '';
    if (t.includes('nav-target-chat')) chats.push(aria.includes('(чат в архиве)'));
    else tasks.push(aria.includes('(задача закрыта)'));
  }
  return { chats, tasks };
}

describe('пометки: тревога страницы (FeaturePageAlert)', () => {
  it('тревога «мерж»: чат финала в архиве, задача финала закрыта', () => {
    const run = mkRun({
      state: 'merge_pending',
      attention: { kind: 'merge', taskId: 't-fin', since: null, reason: null, personaId: null, linkedSessionId: 'c-arch' },
    });
    const html = renderToStaticMarkup(createElement(FeaturePageAlert, { run, feature: null, isMobile: false }));
    expect(navFlagsFromHtml(html)).toEqual({ chats: [true], tasks: [true] });
  });
});

describe('пометки: попап и панель (FeatureRunView)', () => {
  it('«мерж ждёт согласия»: пара финала + исполнители этапа в порядке DOM', () => {
    const run = mkRun({ state: 'merge_pending' });
    const html = renderToStaticMarkup(createElement(FeatureRunView, { run }));
    const f = navFlagsFromHtml(html);
    // Порядок: пара финала (c-arch · t-fin), затем исполнители A, B, C.
    expect(f.chats).toEqual([true, false, true, false]);
    expect(f.tasks).toEqual([true, false, false, true]);
  });

  it('тревога «ждёт вас»: кнопка задачи тревоги помечена закрытой', () => {
    const run = mkRun({
      state: 'waiting',
      attention: { kind: 'wait', taskId: 't-c', since: null, reason: null, personaId: null, linkedSessionId: 'c-live' },
    });
    const html = renderToStaticMarkup(createElement(FeatureRunView, { run }));
    // Тревога (первая задача в разметке) → t-c закрыта; далее исполнители A, B, C.
    expect(navFlagsFromHtml(html).tasks).toEqual([true, false, false, true]);
  });
});

describe('пометки: шторка (FeatureBannerSheet)', () => {
  it('исполнители текущего этапа: A / B / C', async () => {
    const c = render(createElement(FeatureBannerSheet, { runs: [mkRun()], isMobile: false, onClose: () => {} }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    const rows = byId(c, 'feature-sheet-executor');
    expect(rows.length).toBe(3);
    expectFlags(rows[0], A, 'шторка A');
    expectFlags(rows[1], B, 'шторка B');
    expectFlags(rows[2], C, 'шторка C');
  });
});
