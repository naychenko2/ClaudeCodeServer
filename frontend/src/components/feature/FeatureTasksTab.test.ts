// Вкладка «Задачи» страницы фичи (волна 3, Ф4; макет features-overview-3-final §4/§9а).
//
// Проводка данных — рендер вкладки с НАСТОЯЩИМИ задачами (FeatureTask из run), не прямой проп:
//   • строка: аватар исполнителя · название · роль (roleLabel) · статус (факт) ·
//     пара «чат · задача» (NavTargetPair: чат = linkedSessionId, задача = id);
//   • фильтр по статусу: «все» (всегда) + статусы с числом > 0;
//   • сортировка по времени (последняя активность desc);
//   • «Показать все» (порог 8): по умолчанию 8 последних, раскрытие — весь список;
//   • пустое: «Задач нет».
//
// Честный прогресс: статус — ФАКТ («готово», «в работе», «ждёт вас», «стоит», «впереди»),
// НЕ «принято N из M».
//
// Мутации (в отчёте):
//   (1) фильтр «Закрыты» (d) не фильтрует (всегда все) → «фильтр: закрытые» краснеет;
//   (2) «Показать все» не раскрывает остаток (всегда 8) → «показать все» краснеет;
//   (3) потерять taskId в NavTargetPair (taskId=null) → «переход в задачу» краснеет;
//   (4) слово статуса «принято» вместо «готово» → «статус: факт, не принято» краснеет.

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import {
  FeatureTasksTab, allFeatureTasks, taskStatusKey, taskStatusLabel,
  sortTasksByTime, filterTasksByStatus, taskFilterChips, TASKS_COLLAPSED_LIMIT,
} from './FeatureTasksTab';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import { __test as backNavTest } from '../../lib/featureBackNav';
import type { FeatureRun, FeatureTask } from '../../types/featureRun';

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

function runWith(tasks: FeatureTask[]): FeatureRun {
  return {
    featureId: 'f1', state: 'running',
    epic: null, steps: tasks, reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false,
  } as FeatureRun;
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
  personaMap.set('p-denis', 'Денис');
  personaMap.set('p-gleb', 'Глеб');
  mockOpenChat.calls = [];
  mockOpenTask.calls = [];
  backNavTest.reset();
});
afterEach(() => {
  unmountAll();
  uninstallFakeDom();
  vi.restoreAllMocks();
});

// ═══ 1. Чистые функции: статус, подпись, сортировка, фильтр, чипы ═══════════════
describe('Чистые функции (статус/сортировка/фильтр)', () => {
  it('taskStatusKey: done→d, inProgress+awaiting→w, inProgress+stopped→x, inProgress→r, todo→t', () => {
    expect(taskStatusKey(task({ status: 'done' }))).toBe('d');
    expect(taskStatusKey(task({ status: 'inProgress', awaitingHumanSince: '2026-10-20T11:00:00Z' }))).toBe('w');
    expect(taskStatusKey(task({ status: 'inProgress', executorStoppedAt: '2026-10-20T11:00:00Z' }))).toBe('x');
    expect(taskStatusKey(task({ status: 'inProgress' }))).toBe('r');
    expect(taskStatusKey(task({ status: 'todo' }))).toBe('t');
    expect(taskStatusKey(task({ status: null }))).toBe('t');
  });

  it('taskStatusLabel: факт, не «принято» (готово/в работе/ждёт вас/стоит/впереди)', () => {
    expect(taskStatusLabel('d')).toBe('готово');
    expect(taskStatusLabel('r')).toBe('в работе');
    expect(taskStatusLabel('w')).toBe('ждёт вас');
    expect(taskStatusLabel('x')).toBe('стоит');
    expect(taskStatusLabel('t')).toBe('впереди');
    // Мутация (4): «принято» вместо «готово» → краснеет.
    expect(taskStatusLabel('d')).not.toBe('принято');
  });

  it('sortTasksByTime: последняя активность (completedAt→claudeStartedAt→createdAt) desc', () => {
    const a = task({ id: 'a', completedAt: '2026-10-20T09:00:00Z' });
    const b = task({ id: 'b', claudeStartedAt: '2026-10-20T12:00:00Z' });
    const c = task({ id: 'c', createdAt: '2026-10-20T13:00:00Z' });
    const sorted = sortTasksByTime([a, b, c]);
    // c (13:00) → b (12:00) → a (09:00): desc.
    expect(sorted.map(t => t.id)).toEqual(['c', 'b', 'a']);
  });

  it('filterTasksByStatus: all — все; d — только done', () => {
    const done = task({ id: 'd', status: 'done' });
    const run = task({ id: 'r', status: 'inProgress' });
    expect(filterTasksByStatus([done, run], 'all').map(t => t.id)).toEqual(['d', 'r']);
    expect(filterTasksByStatus([done, run], 'd').map(t => t.id)).toEqual(['d']);
    expect(filterTasksByStatus([done, run], 'r').map(t => t.id)).toEqual(['r']);
  });

  it('taskFilterChips: «все» (всегда) + статусы с числом > 0 (r,w,x,d,t)', () => {
    const tasks = [
      task({ id: '1', status: 'done' }),
      task({ id: '2', status: 'inProgress' }),
      task({ id: '3', status: 'inProgress', awaitingHumanSince: '2026-10-20T11:00:00Z' }),
      task({ id: '4', status: 'todo' }),
    ];
    const chips = taskFilterChips(tasks);
    // «все» = 4; r=1, w=1, d=1, t=1; x=0 (нет).
    expect(chips.find(c => c.key === 'all')?.count).toBe(4);
    expect(chips.find(c => c.key === 'r')?.count).toBe(1);
    expect(chips.find(c => c.key === 'w')?.count).toBe(1);
    expect(chips.find(c => c.key === 'd')?.count).toBe(1);
    expect(chips.find(c => c.key === 't')?.count).toBe(1);
    // x=0 → чипа нет.
    expect(chips.find(c => c.key === 'x')).toBeUndefined();
  });

  it('allFeatureTasks: epic+steps+reviews+qa+final+merge+cut+plan', () => {
    const run = runWith([task({ id: 's1' })]);
    run.epic = task({ id: 'epic' });
    run.reviews = [task({ id: 'rv1' })];
    run.final = task({ id: 'fin' });
    expect(allFeatureTasks(run).map(t => t.id)).toEqual(['epic', 's1', 'rv1', 'fin']);
  });
});

// ═══ 2. Проводка: строка задачи (название, роль, статус, «чат · задача») ═════════
describe('Строка задачи (макет §9а)', () => {
  it('название, роль roleLabel, статус, пара «чат · задача»', () => {
    const t = task({
      id: 't-1', title: 'Ш2 · Применение шаблона',
      role: 'step', stepRef: 'Ш2', lane: 'front',
      personaId: 'p-kira', linkedSessionId: 'chat-1',
      status: 'inProgress', claudeStartedAt: '2026-10-20T10:00:00Z',
    });
    const container = render({ run: runWith([t]) });

    // Название.
    const title = findNodeByAttr(container, 'data-testid', 'task-title');
    expect(title).not.toBeNull();
    expect(textNodes(title!).join('')).toBe('Ш2 · Применение шаблона');

    // Роль: step + Ш2 + front → «Ш2 · фронт» (roleLabel).
    const role = findNodeByAttr(container, 'data-testid', 'task-role');
    expect(role).not.toBeNull();
    expect(textNodes(role!).join('')).toBe('Ш2 · фронт');

    // Статус: inProgress (без awaiting/stopped) → «в работе».
    const status = findNodeByAttr(container, 'data-testid', 'task-status');
    expect(status).not.toBeNull();
    expect(textNodes(status!).join('')).toBe('в работе');

    // Пара «чат · задача»: чат = linkedSessionId, задача = id.
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-chat')).not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-task')).not.toBeNull();
  });

  it('задача без чата (linkedSessionId=null) → «чата нет» (NavTargetPair showNoChat)', () => {
    const t = task({ id: 't-nochat', linkedSessionId: null, status: 'todo' });
    const container = render({ run: runWith([t]) });
    // «чата нет» — текст, не кнопка.
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-no-chat')).not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-chat')).toBeNull();
    // Задача есть.
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-task')).not.toBeNull();
  });

  it('done-задача → статус «готово» + «закрыта» на кнопке задачи (taskClosed)', () => {
    const t = task({ id: 't-done', status: 'done', completedAt: '2026-10-20T12:00:00Z' });
    const container = render({ run: runWith([t]) });
    // Статус — факт «готово» (не «принято»).
    const status = findNodeByAttr(container, 'data-testid', 'task-status');
    expect(textNodes(status!).join('')).toBe('готово');
    // Кнопка «задача» с пометкой «закрыта» (taskClosed=true → пунктир + «закрыта»).
    const taskBtn = findNodeByAttr(container, 'data-testid', 'nav-target-task');
    expect(taskBtn).not.toBeNull();
    expect(textNodes(taskBtn!).join('')).toContain('закрыта');
  });
});

// ═══ 3. Фильтр по статусу (макет .cnts) ═════════════════════════════════════════
describe('Фильтр по статусу (макет §4)', () => {
  it('фильтр «Закрыты» (d) показывает только done (мутация: не фильтрует → red)', async () => {
    const done1 = task({ id: 'd1', status: 'done', completedAt: '2026-10-20T09:00:00Z' });
    const done2 = task({ id: 'd2', status: 'done', completedAt: '2026-10-20T10:00:00Z' });
    const run = task({ id: 'r1', status: 'inProgress', claudeStartedAt: '2026-10-20T11:00:00Z' });
    const container = render({ run: runWith([done1, done2, run]) });

    // Чип «готово» (d) есть (count=2).
    const chipD = findNodeByAttr(container, 'data-testid', 'tasks-filter-d');
    expect(chipD).not.toBeNull();
    expect(textNodes(chipD!).join('')).toContain('2');

    // Клик по «готово» → только done-задачи.
    const onClick = reactProps(chipD!)!.onClick as () => void;
    await act(async () => { onClick(); });

    // После фильтра: только d1, d2 (r1 скрыт).
    expect(findNodeByAttr(container, 'data-testid', 'task-row-d1')).not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'task-row-d2')).not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'task-row-r1')).toBeNull();
  });

  it('фильтр «все» (по умолчанию) показывает все задачи', () => {
    const a = task({ id: 'a', status: 'done' });
    const b = task({ id: 'b', status: 'inProgress' });
    const container = render({ run: runWith([a, b]) });
    // По умолчанию filter='all' → обе задачи.
    expect(findNodeByAttr(container, 'data-testid', 'task-row-a')).not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'task-row-b')).not.toBeNull();
    // Чип «все» active (aria-pressed=true).
    const chipAll = findNodeByAttr(container, 'data-testid', 'tasks-filter-all');
    expect(chipAll!.getAttribute('aria-pressed')).toBe('true');
  });

  it('фильтр по статусу: «в работе» (r) показывает только inProgress', async () => {
    const done = task({ id: 'd', status: 'done', completedAt: '2026-10-20T09:00:00Z' });
    const run = task({ id: 'r', status: 'inProgress', claudeStartedAt: '2026-10-20T11:00:00Z' });
    const container = render({ run: runWith([done, run]) });
    // Клик по «в работе» (r) → только inProgress-задача (done скрыт).
    const chipR = findNodeByAttr(container, 'data-testid', 'tasks-filter-r');
    expect(chipR).not.toBeNull();
    const onClick = reactProps(chipR!)!.onClick as () => void;
    await act(async () => { onClick(); });
    expect(findNodeByAttr(container, 'data-testid', 'task-row-r')).not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'task-row-d')).toBeNull();
  });
});

// ═══ 4. «Показать все» (порог 8) ═══════════════════════════════════════════════
describe('«Показать все» (макет: порог 8)', () => {
  it('по умолчанию 8 последних; «Показать все» раскрывает остаток (мутация: не раскрывает → red)', async () => {
    // 12 задач (все done, разные completedAt → desc).
    const tasks: FeatureTask[] = [];
    for (let i = 0; i < 12; i++) {
      tasks.push(task({
        id: `t${i}`,
        status: 'done',
        completedAt: `2026-10-20T${String(10 + i).padStart(2, '0')}:00:00Z`,
      }));
    }
    const container = render({ run: runWith(tasks) });

    // По умолчанию: 8 последних (t11..t4, desc), кнопка «Показать все · 12».
    expect(findNodeByAttr(container, 'data-testid', 'task-row-t11')).not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'task-row-t0')).toBeNull();
    const showAll = findNodeByAttr(container, 'data-testid', 'tasks-show-all');
    expect(showAll).not.toBeNull();
    expect(textNodes(showAll!).join('')).toContain('12');

    // Клик → все 12.
    const onClick = reactProps(showAll!)!.onClick as () => void;
    await act(async () => { onClick(); });
    expect(findNodeByAttr(container, 'data-testid', 'task-row-t0')).not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'task-row-t11')).not.toBeNull();
    // Кнопка → «Свернуть».
    expect(textNodes(showAll!).join('')).toContain('Свернуть');
  });

  it('≤ 8 задач → кнопки «Показать все» нет', () => {
    const tasks: FeatureTask[] = [];
    for (let i = 0; i < 5; i++) tasks.push(task({ id: `t${i}`, status: 'done' }));
    const container = render({ run: runWith(tasks) });
    expect(findNodeByAttr(container, 'data-testid', 'tasks-show-all')).toBeNull();
  });

  it('порог = 8 (TASKS_COLLAPSED_LIMIT)', () => {
    expect(TASKS_COLLAPSED_LIMIT).toBe(8);
  });
});

// ═══ 5. Навигация: «чат · задача» ══════════════════════════════════════════════
describe('Навигация «чат · задача» (макет §9а)', () => {
  it('клик «задача» → openTaskInSection(id); клик «чат» → openChatById(linkedSessionId)', async () => {
    const t = task({
      id: 't-nav', title: 'Ш1', role: 'step', stepRef: 'Ш1', lane: 'back',
      personaId: 'p-denis', linkedSessionId: 'chat-nav',
      status: 'inProgress', claudeStartedAt: '2026-10-20T10:00:00Z',
    });
    const container = render({ run: runWith([t]) });

    const taskBtn = findNodeByAttr(container, 'data-testid', 'nav-target-task')!;
    const taskClick = reactProps(taskBtn)!.onClick as () => void;
    await act(async () => { taskClick(); });
    expect(mockOpenTask.calls).toEqual([{ id: 't-nav' }]);

    const chatBtn = findNodeByAttr(container, 'data-testid', 'nav-target-chat')!;
    const chatClick = reactProps(chatBtn)!.onClick as () => void;
    await act(async () => { chatClick(); });
    expect(mockOpenChat.calls).toEqual(['chat-nav']);
  });

  it('мутация: потерять taskId (null) → кнопки задачи нет (red)', () => {
    // taskId=null → NavTargetPair не рисует «задача» (hasTarget(null)=false).
    // «Потеря» моделируется id=null (cast: в данных id — string, но fail-closed — null).
    const t = { ...task({ id: 't-notask', linkedSessionId: 'c1' }), id: null as unknown as string };
    const container = render({ run: runWith([t]) });
    expect(findNodeByAttr(container, 'data-testid', 'nav-target-task')).toBeNull();
  });
});

// ═══ 6. Пустое состояние ═══════════════════════════════════════════════════════
describe('Пустое состояние', () => {
  it('нет задач → «Задач нет» + пояснение', () => {
    const container = render({ run: runWith([]) });
    const empty = findNodeByAttr(container, 'data-testid', 'tasks-empty');
    expect(empty).not.toBeNull();
    expect(textNodes(empty!).join('')).toContain('Задач нет');
    expect(textNodes(empty!).join('')).toContain('конвейер ещё не запускался');
  });
});
