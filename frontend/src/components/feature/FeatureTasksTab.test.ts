// Вкладка «Шаги» страницы фичи (Ш7): строки — из run.stepChains, корзина — бэковая.
//
// Проводка данных — рендер вкладки с НАСТОЯЩИМ агрегатом (stepChains + задачи), не прямой проп:
//   • строка шага: название · роль · корзина · «чат · задача»; шаг плана без задачи —
//     «не заведено» без навигации;
//   • фильтры = корзины stepProgress (нули скрыты), сумма строк по ним = stepProgress.total;
//     «в master» — отдельно, «служебные» — задачи вне цепочек;
//   • статус из task.status не выводится: задача inProgress в цепочке accepted — «принято».
//
// Мутации (в отчёте):
//   (1) bucket берётся из task.status, а не из корзины цепочки → «корзина из цепочки» краснеет;
//   (2) фильтр не фильтрует → «фильтр: принято» краснеет;
//   (3) «ждёт вас» вместо BRIEF_WAIT_WORD → «нужен ответ» краснеет;
//   (4) служебные идут в «все» → «служебные отдельно» краснеет.

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import {
  FeatureTasksTab, allFeatureTasks, buildStepsTab, stepsFilterChips, sortTasksByTime,
  TASKS_COLLAPSED_LIMIT,
} from './FeatureTasksTab';
import { BRIEF_WAIT_WORD } from './FeatureBriefModel';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import { __test as backNavTest } from '../../lib/featureBackNav';
import type { FeatureRun, FeatureStepChain, FeatureTask } from '../../types/featureRun';

// === Мок персон (lib/personas): id → имя; аватар — инициалы (без image) ===
const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>() }));
vi.mock('../../lib/personas', () => ({
  getPersonaById: (id: string) =>
    personaMap.has(id) ? { id, name: personaMap.get(id), avatar: { kind: 'initials' as const } } : undefined,
  usePersonasVersion: () => 0,
  personaInitials: (name: string) => (name ? name.trim().charAt(0).toUpperCase() : ''),
}));
vi.mock('../../lib/api', () => ({ api: { personas: { avatarUrl: () => '' } } }));
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});
vi.mock('../../lib/toast', () => ({ showToast: (..._a: unknown[]) => {} }));
// Навигация: журнал вызовов (chat / task).
const mockOpenChat = vi.hoisted(() => ({ calls: [] as string[] }));
vi.mock('../../lib/openChat', () => ({ openChatById: async (id: string) => { mockOpenChat.calls.push(id); return true; } }));
const mockOpenTask = vi.hoisted(() => ({ calls: [] as { id: string; projectId?: string }[] }));
vi.mock('../../lib/tasks', () => ({ openTaskInSection: (t: { id: string; projectId?: string }) => { mockOpenTask.calls.push(t); } }));

// ── Хелперы данных ──────────────────────────────────────────────────────────────
function task(over: Partial<FeatureTask> = {}): FeatureTask {
  return {
    id: 't1', title: 'Задача', status: 'todo', role: null,
    featureId: 'f1', stepRef: null, lane: null,
    personaId: null, createdAt: '2026-10-20T10:00:00Z', completedAt: null,
    claudeStartedAt: null, linkedSessionId: null,
    ...over,
  };
}

// Шаг-задача цепочки: старт задан, чтобы у цепочки был «старт» (карточка не «не начата»).
function stepTask(id: string, over: Partial<FeatureTask> = {}): FeatureTask {
  return task({ id, role: 'step', claudeStartedAt: '2026-10-20T10:00:00Z', ...over });
}

function chain(stepRef: string, taskIds: string[], over: Partial<FeatureStepChain> = {}): FeatureStepChain {
  return {
    stepRef, lane: 'front', title: `Шаг ${stepRef}`, personaId: null, taskIds,
    outcome: 'running', status: 'идёт', durationMinutes: 0, loops: 0, rounds: [], chatId: null,
    ...over,
  };
}

// Агрегат: 4 корзины + снятый + «в master» + служебные. work=2 (r1, r2), review=1, accepted=2, notCreated=1 (из плана).
function fullRun(): FeatureRun {
  const done = (id: string) => stepTask(id, { status: 'done', completedAt: '2026-10-20T12:00:00Z' });
  const steps = [
    stepTask('r1', { status: 'inProgress' }),
    stepTask('r2', { status: 'inProgress', awaitingHumanSince: '2026-10-20T11:30:00Z' }),
    done('v1'), done('a1'), done('a2'), done('w1'),
  ];
  return {
    featureId: 'f1', state: 'running',
    epic: task({ id: 'epic', title: 'Фича', role: 'feature' }),
    steps,
    reviews: [task({ id: 'rv1', title: 'Ревью', role: 'review', status: 'done' })],
    qa: [], final: null, merge: null,
    plan: [task({
      id: 'plan1', title: 'План', role: 'plan', status: 'done',
      planSteps: [
        { stepRef: 'R1', title: 'Шаг R1' }, { stepRef: 'R2', title: 'Шаг R2' },
        { stepRef: 'V1', title: 'Шаг V1' }, { stepRef: 'A1', title: 'Шаг A1' }, { stepRef: 'A2', title: 'Шаг A2' },
        { stepRef: 'N1', title: 'Шаг N1' },
      ],
    } as Partial<FeatureTask>)],
    activePlanIds: ['plan1'],
    stepChains: [
      chain('R1', ['r1'], { chatId: 'c-r1', durationMinutes: 65 }),
      chain('R2', ['r2']),
      chain('V1', ['v1'], { outcome: 'pending', status: 'ждёт ревью' }),
      chain('A1', ['a1'], { outcome: 'accepted', status: 'принят', inMaster: true }),
      chain('A2', ['a2'], { outcome: 'accepted', status: 'принят', inMaster: true }),
      chain('W1', ['w1'], { outcome: 'withdrawn', status: 'снят с выкладки' }),
    ],
    stepProgress: { planState: 'partial', total: 6, accepted: 2, review: 1, work: 2, notCreated: 1, withdrawn: 1, unlinkedReworks: 0, inMaster: 2 },
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false,
  } as unknown as FeatureRun;
}

// Корни всех mount'ов: unmount в afterEach (иначе два живых корня дёргают общий стор).
const roots: import('react-dom/client').Root[] = [];
function render(props: { run: FeatureRun; isMobile?: boolean }) {
  installFakeDom();
  const { root, container } = mount(createElement(FeatureTasksTab, {
    run: props.run,
    featureId: 'f1',
    isMobile: props.isMobile ?? false,
  }));
  roots.push(root);
  return container;
}
function unmountAll() {
  for (const r of roots) act(() => { r.unmount(); });
  roots.length = 0;
}

beforeEach(() => {
  personaMap.clear();
  personaMap.set('p-kira', 'Кира');
  mockOpenChat.calls = [];
  mockOpenTask.calls = [];
  backNavTest.reset();
});
afterEach(() => {
  unmountAll();
  uninstallFakeDom();
  vi.restoreAllMocks();
});

const chipText = (c: ReturnType<typeof render>, key: string) => {
  const n = findNodeByAttr(c, 'data-testid', `tasks-filter-${key}`);
  return n ? textNodes(n).join('') : null;
};
const rowIds = (c: ReturnType<typeof render>, prefix: string) => {
  const ids: string[] = [];
  const walk = (x: { childNodes?: unknown; getAttribute?: (a: string) => string | null }) => {
    const id = x.getAttribute?.('data-testid') ?? '';
    if (id.startsWith(prefix)) ids.push(id);
    for (const ch of Array.from((x.childNodes ?? []) as ArrayLike<never>)) walk(ch);
  };
  walk(c as never);
  return ids;
};
async function click(c: ReturnType<typeof render>, testId: string) {
  const n = findNodeByAttr(c, 'data-testid', testId)!;
  await act(async () => { (reactProps(n)!.onClick as () => void)(); });
}

// ═══ 1. Модель: корзины из цепочек, служебные отдельно ═══════════════════════════
describe('Модель вкладки «Шаги»', () => {
  it('корзина из цепочки, а не из task.status (мутация: вывод из task.status → red)', () => {
    // Задача шага inProgress, а цепочка бэка — accepted: строка «принято».
    const run = fullRun();
    run.steps = run.steps.map(t => (t.id === 'a1' ? { ...t, status: 'inProgress' as const } : t));
    const row = buildStepsTab(run).steps.find(r => r.card.stepRef === 'A1')!;
    expect(row.bucket).toBe('accepted');
    // И наоборот: все задачи закрыты, цепочка running + pending-ревью → «в ревью», не «принято».
    expect(buildStepsTab(run).steps.find(r => r.card.stepRef === 'V1')!.bucket).toBe('review');
  });

  it('сумма строк по корзинам = stepProgress.total; снятый и master — вне суммы', () => {
    const run = fullRun();
    const m = buildStepsTab(run);
    const by = (b: string) => m.steps.filter(r => r.bucket === b).length;
    expect(by('work') + by('review') + by('accepted') + by('notCreated')).toBe(run.stepProgress!.total);
    expect([by('work'), by('review'), by('accepted'), by('notCreated'), by('withdrawn')]).toEqual([2, 1, 2, 1, 1]);
    expect(m.steps.filter(r => r.inMaster)).toHaveLength(run.stepProgress!.inMaster!);
  });

  it('план с шагом kind=review (R9): строки по корзинам = total, «не заведено» = notCreated', () => {
    const run = fullRun();
    (run.plan[0] as unknown as { planSteps: unknown[] }).planSteps.push({ stepRef: 'R9', title: 'Ревью', kind: 'review' });
    const m = buildStepsTab(run);
    const by = (b: string) => m.steps.filter(r => r.bucket === b).length;
    expect(by('work') + by('review') + by('accepted') + by('notCreated')).toBe(run.stepProgress!.total);
    expect(by('notCreated')).toBe(run.stepProgress!.notCreated);
    expect(m.steps.some(r => r.card.stepRef === 'R9')).toBe(false);
  });

  it('служебные: эпик, план, ревью — вне цепочек; задачи шагов в них не попадают', () => {
    const m = buildStepsTab(fullRun());
    expect(m.service.map(t => t.id).sort()).toEqual(['epic', 'plan1', 'rv1']);
  });

  it('фильтры: корзины трекера с нулями скрыты, порядок «в работе · в ревью · не заведено · принято»', () => {
    const chips = stepsFilterChips(buildStepsTab(fullRun()));
    expect(chips.map(c => `${c.key}:${c.count}`)).toEqual([
      'all:7', 'work:2', 'review:1', 'notCreated:1', 'accepted:2', 'inMaster:2', 'withdrawn:1', 'service:3',
    ]);
    const only = fullRun();
    only.stepChains = only.stepChains!.filter(c => c.stepRef === 'A1');
    only.plan = [];
    const keys = stepsFilterChips(buildStepsTab(only)).map(c => c.key);
    expect(keys).not.toContain('work');
    expect(keys).not.toContain('review');
    expect(keys).not.toContain('notCreated');
    expect(keys).toContain('accepted');
  });

  it('sortTasksByTime: последняя активность (completedAt→claudeStartedAt→createdAt) desc', () => {
    const a = task({ id: 'a', completedAt: '2026-10-20T09:00:00Z' });
    const b = task({ id: 'b', claudeStartedAt: '2026-10-20T12:00:00Z' });
    const c = task({ id: 'c', createdAt: '2026-10-20T13:00:00Z' });
    expect(sortTasksByTime([a, b, c]).map(t => t.id)).toEqual(['c', 'b', 'a']);
  });

  it('allFeatureTasks: epic+steps+reviews+qa+final+merge+cut+plan', () => {
    const run = fullRun();
    run.final = task({ id: 'fin' });
    expect(allFeatureTasks(run).map(t => t.id)).toEqual(['epic', 'r1', 'r2', 'v1', 'a1', 'a2', 'w1', 'rv1', 'fin', 'plan1']);
  });
});

// ═══ 2. Рендер: строки, фильтры ═══════════════════════════════════════════════
describe('Рендер вкладки', () => {
  it('«все»: строки шагов (включая план без задачи), служебных среди них нет (мутация: служебные в «все» → red)', () => {
    const c = render({ run: fullRun() });
    expect(rowIds(c, 'step-row-').sort()).toEqual(
      ['step-row-A1', 'step-row-A2', 'step-row-N1', 'step-row-R1', 'step-row-R2', 'step-row-V1', 'step-row-W1'].sort());
    expect(rowIds(c, 'task-row-')).toEqual([]);
  });

  it('строка шага: название, роль, корзина, длительность единым форматом, пара «чат · задача»', () => {
    const c = render({ run: fullRun() });
    const row = findNodeByAttr(c, 'data-testid', 'step-row-R1')!;
    expect(textNodes(findNodeByAttr(row, 'data-testid', 'task-title')!).join('')).toBe('Шаг R1');
    expect(textNodes(findNodeByAttr(row, 'data-testid', 'task-role')!).join('')).toBe('R1 · фронт');
    expect(textNodes(findNodeByAttr(row, 'data-testid', 'task-status')!).join('')).toBe('в работе');
    expect(textNodes(findNodeByAttr(row, 'data-testid', 'task-meta')!).join('')).toBe('1 ч 05 мин');
    expect(findNodeByAttr(row, 'data-testid', 'nav-target-chat')).not.toBeNull();
    expect(findNodeByAttr(row, 'data-testid', 'nav-target-task')).not.toBeNull();
  });

  it('шаг плана без задачи: «не заведено», без кнопок навигации', () => {
    const c = render({ run: fullRun() });
    const row = findNodeByAttr(c, 'data-testid', 'step-row-N1')!;
    expect(textNodes(findNodeByAttr(row, 'data-testid', 'task-status')!).join('')).toBe('не заведено');
    expect(findNodeByAttr(row, 'data-testid', 'task-open')).toBeNull();
    expect(findNodeByAttr(row, 'data-testid', 'nav-target-task')).toBeNull();
    expect(findNodeByAttr(row, 'data-testid', 'nav-target-no-chat')).toBeNull();
  });

  it('ожидание человека — «нужен ответ» из общего источника, не «ждёт вас» (мутация: литерал → red)', () => {
    const c = render({ run: fullRun() });
    const wait = findNodeByAttr(findNodeByAttr(c, 'data-testid', 'step-row-R2')!, 'data-testid', 'task-wait')!;
    expect(textNodes(wait).join('')).toBe(BRIEF_WAIT_WORD);
    expect(BRIEF_WAIT_WORD).toBe('нужен ответ');
    expect(textNodes(c).join('')).not.toContain('ждёт вас');
    // У шага без ожидания тега нет.
    expect(findNodeByAttr(findNodeByAttr(c, 'data-testid', 'step-row-R1')!, 'data-testid', 'task-wait')).toBeNull();
  });

  it('чипы: числа и порядок; «принято» показывает только принятые (мутация: не фильтрует → red)', async () => {
    const c = render({ run: fullRun() });
    expect(chipText(c, 'work')).toContain('2');
    expect(chipText(c, 'review')).toContain('1');
    expect(chipText(c, 'notCreated')).toContain('1');
    expect(chipText(c, 'accepted')).toContain('2');
    await click(c, 'tasks-filter-accepted');
    expect(rowIds(c, 'step-row-').sort()).toEqual(['step-row-A1', 'step-row-A2']);
  });

  it('«в master» — отдельный срез поверх корзин', async () => {
    const c = render({ run: fullRun() });
    await click(c, 'tasks-filter-inMaster');
    expect(rowIds(c, 'step-row-').sort()).toEqual(['step-row-A1', 'step-row-A2']);
  });

  it('«служебные»: только задачи вне цепочек, шаги не показываются', async () => {
    const c = render({ run: fullRun() });
    await click(c, 'tasks-filter-service');
    expect(rowIds(c, 'step-row-')).toEqual([]);
    expect(rowIds(c, 'task-row-').sort()).toEqual(['task-row-epic', 'task-row-plan1', 'task-row-rv1']);
  });

  it('«все» по умолчанию: aria-pressed', () => {
    const c = render({ run: fullRun() });
    expect(findNodeByAttr(c, 'data-testid', 'tasks-filter-all')!.getAttribute('aria-pressed')).toBe('true');
  });
});

// ═══ 3. «Показать все» (порог 8) ═══════════════════════════════════════════════
describe('«Показать все» (порог 8)', () => {
  const manySteps = (n: number): FeatureRun => {
    const run = fullRun();
    run.plan = [];
    run.stepChains = [];
    run.steps = [];
    for (let i = 0; i < n; i++) {
      run.steps.push(stepTask(`d${i}`, { status: 'done', completedAt: '2026-10-20T12:00:00Z' }));
      run.stepChains.push(chain(`S${i}`, [`d${i}`], { outcome: 'accepted', status: 'принят' }));
    }
    return run;
  };

  it('12 шагов: по умолчанию 8, «Показать все · 12» раскрывает остаток', async () => {
    const c = render({ run: manySteps(12) });
    expect(rowIds(c, 'step-row-')).toHaveLength(8);
    const showAll = findNodeByAttr(c, 'data-testid', 'tasks-show-all')!;
    expect(textNodes(showAll).join('')).toContain('12');
    await click(c, 'tasks-show-all');
    expect(rowIds(c, 'step-row-')).toHaveLength(12);
    expect(textNodes(showAll).join('')).toContain('Свернуть');
  });

  it('≤ 8 шагов — кнопки нет; порог = 8', () => {
    const c = render({ run: manySteps(5) });
    expect(findNodeByAttr(c, 'data-testid', 'tasks-show-all')).toBeNull();
    expect(TASKS_COLLAPSED_LIMIT).toBe(8);
  });
});

// ═══ 4. Навигация «чат · задача» ═══════════════════════════════════════════════
describe('Навигация «чат · задача»', () => {
  it('клик «задача» → openTaskInSection(голова цепочки); клик «чат» → openChatById(chain.chatId)', async () => {
    const c = render({ run: fullRun() });
    const row = findNodeByAttr(c, 'data-testid', 'step-row-R1')!;
    await act(async () => { (reactProps(findNodeByAttr(row, 'data-testid', 'nav-target-task')!)!.onClick as () => void)(); });
    expect(mockOpenTask.calls).toEqual([{ id: 'r1' }]);
    await act(async () => { (reactProps(findNodeByAttr(row, 'data-testid', 'nav-target-chat')!)!.onClick as () => void)(); });
    expect(mockOpenChat.calls).toEqual(['c-r1']);
  });

  it('шаг с доработкой в работе: «задача» и «чат» ведут в доработку, а не в голову', async () => {
    const run = fullRun();
    run.steps = [...run.steps, stepTask('rw1', { status: 'inProgress', role: 'rework', linkedSessionId: 'c-rw1' })];
    run.stepChains = run.stepChains!.map(c => (c.stepRef === 'V1' ? { ...c, taskIds: ['v1', 'rw1'] } : c));
    const row = findNodeByAttr(render({ run }), 'data-testid', 'step-row-V1')!;
    await act(async () => { (reactProps(findNodeByAttr(row, 'data-testid', 'nav-target-task')!)!.onClick as () => void)(); });
    expect(mockOpenTask.calls).toEqual([{ id: 'rw1' }]);
    await act(async () => { (reactProps(findNodeByAttr(row, 'data-testid', 'nav-target-chat')!)!.onClick as () => void)(); });
    expect(mockOpenChat.calls).toEqual(['c-rw1']);
  });

  it('закрытая задача шага — «закрыта» на кнопке задачи', () => {
    const c = render({ run: fullRun() });
    const btn = findNodeByAttr(findNodeByAttr(c, 'data-testid', 'step-row-A1')!, 'data-testid', 'nav-target-task')!;
    expect(textNodes(btn).join('')).toContain('закрыта');
  });
});

// ═══ 5. Пустое состояние ═══════════════════════════════════════════════════════
describe('Пустое состояние', () => {
  it('нет шагов и задач → «Шагов нет» + пояснение', () => {
    const run = { featureId: 'f1', state: 'running', steps: [], reviews: [], qa: [], final: null, merge: null,
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 }, progress: 0, ready: false } as unknown as FeatureRun;
    const c = render({ run });
    const empty = findNodeByAttr(c, 'data-testid', 'tasks-empty')!;
    expect(textNodes(empty).join('')).toContain('Шагов нет');
    expect(textNodes(empty).join('')).toContain('конвейер ещё не запускался');
  });
});
