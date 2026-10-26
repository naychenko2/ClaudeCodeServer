// Поповер и шторка интервала этапа (GC-F6).
//
// Проверяем то, что обещала карточка:
//   • c1 — клик по интервалу ЗАКРЕПЛЯЕТ поповер (5 кликов подряд — открыт 5 из 5,
//     переключения «клик — toggle» нет);
//   • c2 — Esc закрывает, фокус возвращается на интервал, превью заново не всплывает;
//   • c3 — превью/поповер живут в портале (в потоке ленты их нет — строки не едут);
//   • c4 — на 360 дорожка неинтерактивна (10 px не тач-цель), вход в интервалы —
//     шторка по кнопке 44 px;
//   • словами — состояние интервала, состав по taskIds («ждала задача X, в этом же
//     интервале работала Y») и число задач;
//   • связь — «Показать в ленте» (focusFeatureStage + вспышка), «Открыть задачу» и
//     «чат →» (с записью возврата).
//
// Мутации (в отчёте): (1) «click — toggle» (закрывать повторным кликом) → c1 краснеет;
// (2) не возвращать фокус на интервал при Esc → c2 краснеет; (3) рендерить превью в
// потоке, а не в портале → c3 краснеет; (4) оставить интервалы кнопками на 360 → c4
// краснеет; (5) убрать «Задач в интервале: N» → проверка слов краснеет.

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureTimelineTab } from './FeatureTimelineTab';
import {
  intervalStateWord, intervalTaskCountLabel, intervalCompositionLines,
  intervalWorkerLines, intervalPauseText, primaryLaneTask,
  type LaneIntervalDetails,
} from './FeatureLanePopover';
import { C } from '../../lib/design';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureRun, FeatureStage, FeatureStageExecutor, FeatureTask } from '../../types/featureRun';
import type { FeatureStats } from '../../types/featureStats';

// === Моки ===
const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>() }));
vi.mock('../../lib/personas', () => ({
  getPersonaById: (id: string) =>
    personaMap.has(id)
      ? { id, name: personaMap.get(id), avatar: { kind: 'initials' as const } }
      : undefined,
  usePersonasVersion: () => 0,
  personaInitials: (name: string) => (name ? name.trim().charAt(0).toUpperCase() : ''),
}));
vi.mock('../../lib/api', () => ({ api: { personas: { avatarUrl: () => '' } } }));
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});
vi.mock('../../lib/toast', () => ({ showToast: (..._a: unknown[]) => {} }));
const mockOpenChat = vi.hoisted(() => ({ calls: [] as string[] }));
vi.mock('../../lib/openChat', () => ({ openChatById: async (id: string) => { mockOpenChat.calls.push(id); return true; } }));
const mockOpenTask = vi.hoisted(() => ({ calls: [] as { id: string; projectId?: string }[] }));
vi.mock('../../lib/tasks', () => ({ openTaskInSection: (t: { id: string; projectId?: string }) => { mockOpenTask.calls.push(t); } }));
const mockFocusStage = vi.hoisted(() => ({ calls: [] as string[] }));
vi.mock('../../lib/featurePage', () => ({
  useFeatureStageFocus: () => null,
  clearFeatureStageFocus: () => {},
  focusFeatureStage: (key: string) => { mockFocusStage.calls.push(key); },
}));
const mockBackNav = vi.hoisted(() => ({ calls: [] as unknown[][] }));
vi.mock('../../lib/featureBackNav', () => ({
  recordFeatureReturn: (...args: unknown[]) => { mockBackNav.calls.push(args); },
}));

// === Фикстуры ===
const NOW = new Date('2026-10-20T12:00:00Z').getTime();

type FNodeLike = { nodeType: number; nodeName: string; childNodes: FNodeLike[]; attributes?: Record<string, string>; style?: Record<string, string> };

function mkStage(over: Partial<FeatureStage> = {}): FeatureStage {
  return {
    key: 'prod', state: 'pending', tone: null, passes: 0, taskIds: [], runningSince: null,
    rounds: [], longestStepRef: null, firstStartedAt: null, lastCompletedAt: null, barEnd: null,
    approxMinutes: 0, overlapsActive: false, executors: [], ...over,
  };
}

function mkExec(over: Partial<FeatureStageExecutor> = {}): FeatureStageExecutor {
  return {
    personaId: null, taskId: 't-prod', taskRole: null, stepRef: null, lane: null,
    status: 'running', chatId: null, chatArchived: false, modelLabel: null, modelFull: null,
    reviewOf: [], reviewOfOutside: 0, ...over,
  };
}

function mkTask(id: string, title: string): FeatureTask {
  return { id, title, projectId: 'p1' } as FeatureTask;
}

function mkRun(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'f1',
    state: 'running',
    epic: { id: 'f1', title: 'Фича' },
    steps: [mkTask('t-prod', 'Ревью фичи')],
    reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false,
    stages: [
      mkStage({
        key: 'prod', state: 'active', tone: 'run', firstStartedAt: '2026-10-20T10:00:00Z', approxMinutes: 60,
        executors: [mkExec({ personaId: 'p1', taskId: 't-prod', chatId: 'chat-1', modelLabel: 'Opus', modelFull: 'Opus 4.5' })],
      }),
      mkStage({ key: 'review', state: 'done', firstStartedAt: '2026-10-20T11:00:00Z', approxMinutes: 30 }),
    ],
    ...over,
  } as FeatureRun;
}

function iv(from: string, to: string, taskIds: string[] = ['t-prod'], live = false, lastOutcome: string | null = null) {
  return { from, to, minutes: 30, live, lastOutcome, taskIds, turns: [] };
}

function mkStats(intervalsByStage: Record<string, unknown[]> | null, current: unknown = null): FeatureStats {
  return {
    timeline: {
      from: '2026-10-20T10:00:00Z',
      to: '2026-10-20T12:00:00Z',
      stages: [],
      intervalsByStage,
      segments: [],
      current,
      totals: {},
    },
  } as unknown as FeatureStats;
}

// Две дорожки: prod (10:00–10:30) и review (11:00–11:30).
function laneStats(current: unknown = null): FeatureStats {
  return mkStats({
    prod: [iv('2026-10-20T10:00:00Z', '2026-10-20T10:30:00Z')],
    review: [iv('2026-10-20T11:00:00Z', '2026-10-20T11:30:00Z', ['t-rev'])],
  }, current);
}

function renderTab(run: FeatureRun, stats: FeatureStats | null, isMobile = false) {
  installFakeDom();
  const { container } = mount(createElement(FeatureTimelineTab, { run, stats, isMobile }));
  return container;
}

// Фейк-документ: те же поля, что у fakeDom, но без any (правило no-explicit-any).
interface FakeDoc {
  body: FNodeLike;
  activeElement: FNodeLike | null;
  listeners: Record<string, Set<(e: unknown) => void>>;
  createElement(tag: string): { focus(): void };
}

function doc(): FakeDoc { return (globalThis as unknown as { document: FakeDoc }).document; }

// Узел по data-testid в document.body (портал — превью/поповер/шторка).
function inBody(testId: string): FNodeLike | null {
  return findNodeByAttr(doc().body as never, 'data-testid', testId) as FNodeLike | null;
}

// Все узлы с data-testid в поддереве.
function allByTestId(root: FNodeLike, testId: string): FNodeLike[] {
  const out: FNodeLike[] = [];
  const walk = (n: FNodeLike) => {
    if (n.attributes?.['data-testid'] === testId) out.push(n);
    for (const ch of n.childNodes) walk(ch);
  };
  walk(root);
  return out;
}

// Клик/наведение по узлу интервала.
function fire(node: FNodeLike, kind: 'onClick' | 'onMouseEnter' | 'onMouseLeave') {
  const props = reactProps(node as never) as Record<string, ((e: unknown) => void) | undefined> | null;
  act(() => { props?.[kind]?.({ currentTarget: node, target: node }); });
}

// Esc на document (обработчики висят на document — как в ui/Modal).
function pressEscape() {
  const handlers = [...(doc().listeners?.keydown ?? new Set<(e: unknown) => void>())];
  act(() => { for (const h of handlers) h({ key: 'Escape', preventDefault: () => {}, defaultPrevented: false }); });
}

function styleOf(n: FNodeLike): Record<string, string> {
  return (n as unknown as { style: Record<string, string> }).style ?? {};
}

describe('FeatureLanePopover: слова и модель интервала (GC-F6)', () => {
  const details = (over: Partial<LaneIntervalDetails> = {}): LaneIntervalDetails => ({
    stageKey: 'prod', stageName: 'Производство', index: 0, total: 2,
    from: '2026-10-20T10:00:00Z', to: '2026-10-20T10:30:00Z', minutes: 30,
    state: 'live', lastOutcome: null, tasks: [], pauseAfterMinutes: 0, ...over,
  });

  it('состояние интервала — словом: работа / ждёт / стоит / прервано', () => {
    expect(intervalStateWord('live', null)).toBe('работа');
    expect(intervalStateWord('wait', null)).toBe('ждёт');
    expect(intervalStateWord('stalled', null)).toBe('стоит');
    expect(intervalStateWord('done', null)).toContain('работа');
    expect(intervalStateWord('done', 'cancelled')).toBe('прервано');
    expect(intervalStateWord('live', 'interrupted')).toBe('прервано');
  });

  it('состав интервала — по taskIds: «ждала X, в этом же интервале работала Y», число задач явно', () => {
    const d = details({
      state: 'wait',
      tasks: [
        { taskId: 't1', title: 'Ревью фичи', persona: 'Денис', model: 'Opus', chatId: null, waiting: true },
        { taskId: 't2', title: 'Правки', persona: 'Кира', model: 'Qwen', chatId: null, waiting: false },
      ],
    });
    const lines = intervalCompositionLines(d);
    expect(lines[0]).toBe('Задач в интервале: 2');
    expect(lines.join(' ')).toContain('Ждала задача «Ревью фичи»');
    expect(lines.join(' ')).toContain('В этом же интервале работала задача «Правки»');
    expect(lines.join(' '), 'неправда про весь интервал').not.toContain('весь интервал ждал');
    expect(intervalTaskCountLabel(d.tasks)).toBe('Задач в интервале: 2');
    expect(intervalWorkerLines(d)).toEqual(['Денис · Opus', 'Кира · Qwen']);
    expect(intervalPauseText(0)).toBe('Паузы нет');
    expect(intervalPauseText(36)).toBe('Пауза до следующего: 36 мин');
    expect(primaryLaneTask(d)?.taskId).toBe('t1');
  });

  it('мутация: убрать «Задач в интервале: N» → проверка чисел краснеет', () => {
    const d = details({ tasks: [{ taskId: 't1', title: 'A', persona: null, model: null, chatId: null, waiting: false }] });
    expect(intervalCompositionLines(d)[0]).toContain('Задач в интервале');
  });
});

describe('FeatureLanePopover: поповер на дорожке (GC-F6)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    personaMap.clear();
    personaMap.set('p1', 'Денис');
    mockOpenChat.calls.length = 0;
    mockOpenTask.calls.length = 0;
    mockFocusStage.calls.length = 0;
    mockBackNav.calls.length = 0;
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // === c1: клик закрепляет поповер, 5 кликов подряд ===
  it('c1: наведение + клик 5 раз подряд — поповер открыт 5 из 5', () => {
    const c = renderTab(mkRun(), laneStats());
    const iv0 = findNodeByAttr(c as never, 'data-testid', 'lane-interval-prod-0')!;
    let opened = 0;
    for (let i = 0; i < 5; i += 1) {
      fire(iv0 as never, 'onMouseEnter');
      fire(iv0 as never, 'onClick');
      if (inBody('lane-popover')) opened += 1;
    }
    expect(opened, 'поповер должен быть открыт после каждого клика').toBe(5);
  });

  // Мутация «click — toggle»: повторный клик по тому же интервалу закрывает поповер.

  it('c1: поповер — диалог 288 px с состоянием, составом и кнопками', () => {
    const c = renderTab(mkRun(), laneStats());
    fire(findNodeByAttr(c as never, 'data-testid', 'lane-interval-prod-0')! as never, 'onClick');
    const pop = inBody('lane-popover');
    expect(pop, 'поповер открыт').not.toBeNull();
    expect(pop!.attributes?.['role']).toBe('dialog');
    expect(styleOf(pop!).width).toBe('288px');
    expect(styleOf(pop!).position).toBe('fixed');
    const text = textNodes(pop! as never).join(' ');
    expect(text).toContain('работа');
    expect(text, 'число задач сказано явно').toContain('Задач в интервале: 1');
    expect(text).toContain('Работала задача «Ревью фичи»');
    expect(text, 'кто работал и модель').toContain('Денис · Opus');
    expect(text, 'пауза до следующего').toContain('ПАУЗА ДО СЛЕДУЮЩЕГО');
    expect(inBody('lane-popover-show-in-feed'), 'кнопка «Показать в ленте»').not.toBeNull();
    expect(inBody('lane-popover-open-task'), 'кнопка «Открыть задачу»').not.toBeNull();
    expect(inBody('lane-popover-open-chat'), 'кнопка «чат →»').not.toBeNull();
  });

  // === c2: Esc закрывает, фокус на интервале, превью не всплывает заново ===
  it('c2: Esc закрывает поповер, фокус — на интервале, превью не открылось повторно', () => {
    installFakeDom();
    const proto = Object.getPrototypeOf(doc().createElement('div')) as { focus(): void };
    const origFocus = proto.focus;
    proto.focus = function (this: FNodeLike) { doc().activeElement = this; };

    const c = renderTab(mkRun(), laneStats());
    const iv0 = findNodeByAttr(c as never, 'data-testid', 'lane-interval-prod-0')!;
    fire(iv0 as never, 'onMouseEnter');
    fire(iv0 as never, 'onClick');
    expect(inBody('lane-popover'), 'поповер открыт').not.toBeNull();

    pressEscape();
    expect(inBody('lane-popover'), 'Esc закрыл поповер').toBeNull();
    expect(doc().activeElement, 'фокус вернулся на интервал').toBe(iv0);
    expect(inBody('lane-tip'), 'превью не всплыло заново').toBeNull();

    proto.focus = origFocus;
  });

  // === c3: превью и поповер — в портале, в потоке ленты их нет ===
  it('c3: превью/поповер вне потока ленты (портал), строка «Ревью» не сдвинулась', () => {
    const c = renderTab(mkRun(), laneStats());
    const reviewBefore = findNodeByAttr(c as never, 'data-testid', 'timeline-stage-review')!;
    const styleBefore = { ...styleOf(reviewBefore as never) };

    const iv0 = findNodeByAttr(c as never, 'data-testid', 'lane-interval-prod-0')!;
    fire(iv0 as never, 'onMouseEnter');
    expect(inBody('lane-tip'), 'превью показано').not.toBeNull();
    expect(allByTestId(c as never, 'lane-tip'), 'в потоке ленты превью нет').toHaveLength(0);

    fire(iv0 as never, 'onClick');
    expect(inBody('lane-popover'), 'поповер открыт').not.toBeNull();
    expect(allByTestId(c as never, 'lane-popover'), 'в потоке ленты поповера нет').toHaveLength(0);

    const reviewAfter = findNodeByAttr(c as never, 'data-testid', 'timeline-stage-review')!;
    expect(reviewAfter, 'строка «Ревью» на месте').not.toBeNull();
    expect(styleOf(reviewAfter as never), 'стиль строки не изменился').toEqual(styleBefore);
  });

  // === Связь: «Показать в ленте» / «Открыть задачу» / «чат →» ===
  it('«Показать в ленте» — focusFeatureStage этапа + вспышка строки', () => {
    const c = renderTab(mkRun(), laneStats());
    fire(findNodeByAttr(c as never, 'data-testid', 'lane-interval-prod-0')! as never, 'onClick');
    fire(inBody('lane-popover-show-in-feed')! as never, 'onClick');
    expect(mockFocusStage.calls, 'этап показан в ленте').toContain('prod');
    const prod = findNodeByAttr(c as never, 'data-testid', 'timeline-stage-prod')!;
    expect(styleOf(prod as never).background, 'строка вспыхнула').toBe(C.accentLight);
  });

  it('«Открыть задачу» — переход к задаче с записью возврата', () => {
    const c = renderTab(mkRun(), laneStats());
    fire(findNodeByAttr(c as never, 'data-testid', 'lane-interval-prod-0')! as never, 'onClick');
    fire(inBody('lane-popover-open-task')! as never, 'onClick');
    expect(mockOpenTask.calls).toEqual([{ id: 't-prod', projectId: 'p1' }]);
    expect(mockBackNav.calls[0]?.[0]).toBe('f1');
    expect(mockBackNav.calls[0]?.[1]).toEqual({ kind: 'task', id: 't-prod' });
  });

  it('«чат →» — открывает чат исполнителя с записью возврата', () => {
    const c = renderTab(mkRun(), laneStats());
    fire(findNodeByAttr(c as never, 'data-testid', 'lane-interval-prod-0')! as never, 'onClick');
    fire(inBody('lane-popover-open-chat')! as never, 'onClick');
    expect(mockOpenChat.calls).toEqual(['chat-1']);
    expect(mockBackNav.calls[0]?.[1]).toEqual({ kind: 'chat', id: 'chat-1' });
  });

  it('«ждёт» — поповер говорит словами, кто ждал и кто работал в том же интервале', () => {
    const stats = mkStats({
      prod: [iv('2026-10-20T10:00:00Z', '2026-10-20T11:00:00Z', ['t-prod', 't-other'], true)],
    }, { kind: 'waiting', taskId: 't-prod' });
    const c = renderTab(mkRun(), stats);
    fire(findNodeByAttr(c as never, 'data-testid', 'lane-interval-prod-0')! as never, 'onClick');
    const text = textNodes(inBody('lane-popover')! as never).join(' ');
    expect(text).toContain('ждёт');
    expect(text).toContain('Задач в интервале: 2');
    expect(text).toContain('Ждала задача «Ревью фичи»');
    expect(text).toContain('В этом же интервале работала задача');
  });
});

describe('FeatureLanePopover: 360 — шторка, дорожка не тач-цель (GC-F6)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    personaMap.clear();
    personaMap.set('p1', 'Денис');
    mockFocusStage.calls.length = 0;
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  it('c4: на 360 дорожка без интерактивных целей, вход — кнопка 44 px', () => {
    const c = renderTab(mkRun(), laneStats(), true);
    const lane = findNodeByAttr(c as never, 'data-testid', 'lane-prod')!;
    const buttons = allByTestId(lane as never, 'lane-interval-prod-0');
    expect(buttons, 'интервал на дорожке есть (полоска)').toHaveLength(1);
    expect(buttons[0]!.nodeName, 'интервал на 360 — не кнопка').not.toBe('BUTTON');
    const btn = findNodeByAttr(c as never, 'data-testid', 'timeline-stage-intervals-prod')!;
    expect(btn, 'кнопка входа в шторку есть').not.toBeNull();
    expect(styleOf(btn as never).minHeight, 'кнопка — тач-цель 44 px').toBe('44px');
  });

  it('шторка: по кнопке открывается, внутри — «Все интервалы этапа · N» строками по 44 px', () => {
    const c = renderTab(mkRun(), laneStats(), true);
    fire(findNodeByAttr(c as never, 'data-testid', 'timeline-stage-intervals-prod')! as never, 'onClick');
    const sheet = inBody('lane-sheet-list');
    expect(sheet, 'шторка открыта').not.toBeNull();
    const row = findNodeByAttr(sheet as never, 'data-testid', 'lane-sheet-interval-0')!;
    expect(row, 'строка интервала есть').not.toBeNull();
    expect(styleOf(row as never).minHeight, 'строка — тач-цель 44 px').toBe('44px');
    const text = textNodes(inBody('lane-sheet-details')! as never).join(' ');
    expect(text).toContain('Задач в интервале: 1');
    expect(inBody('lane-sheet-show-in-feed'), 'кнопка «Показать в ленте»').not.toBeNull();
  });
});
