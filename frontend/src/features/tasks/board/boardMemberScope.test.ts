// Доработка S2b-F5 (вердикт ревью 136d1069, п.1): фильтр «Участники» не может остаться
// активным и невидимым вне общего проекта. Стор boardControls общий для хаб-доски и всех
// проектных досок, поэтому TaskBoard при смене контекста доски обязан сбросить 'member':
// выход из общего проекта (хаб, личный проект) и смена проекта — даже между двумя
// общими (выбранный id участника принадлежит прежнему проекту). Иначе matchTaskAssignee
// отсекает все задачи — доска личного проекта пустая, а в тулбаре ни одна кнопка не
// подсвечена. Рендер — настоящий TaskBoard поверх linkedom (путь tasksPanelRender.test.ts);
// тело TaskCard заменён заглушкой: здесь проверяется состав карточек, а не карточка.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { Project, ProjectMembersView, Task } from '../../../types';

const h = vi.hoisted(() => ({
  members: {
    'p-a': {
      owner: { userId: 'u-own-a', displayName: 'Анна А' },
      members: [{ userId: 'u-mary', displayName: 'Мария' }],
      myRole: 'member',
    },
    'p-b': { owner: { userId: 'u-own-b', displayName: 'Анна Б' }, members: [], myRole: 'member' },
  } as Record<string, ProjectMembersView>,
}));

vi.mock('../../../lib/api', () => ({
  api: {
    tasks: { search: async () => [] },
    projects: { members: { list: async (id: string) => h.members[id] } },
  },
}));
vi.mock('../../../lib/signalr', () => ({
  joinUser: async () => {}, onMessage: () => () => {}, onReconnected: () => () => {},
  onProjectMembersChanged: () => () => {},
}));
vi.mock('../../../lib/idb', () => ({
  idbGet: async () => undefined, idbSet: async () => {}, idbClear: async () => {},
  idbKeys: async () => [], idbDelete: async () => {},
  outboxAll: async () => [], outboxGet: async () => undefined,
  outboxPut: async () => {}, outboxDelete: async () => {},
}));
// Тело карточки не предмет теста: виден только её заголовок
vi.mock('../TaskCard', () => ({
  TaskCard: ({ task }: { task: Task }) => createElement('div', { 'data-testid': 'board-card' }, task.title),
}));

// boardControls читает localStorage при загрузке модуля — стабим ДО динамического импорта
(globalThis as { localStorage?: unknown }).localStorage = { getItem: () => null, setItem: () => {}, removeItem: () => {} };

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
Object.assign(win, {
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
  requestAnimationFrame: (cb: () => void) => setTimeout(cb, 0),
  cancelAnimationFrame: (id: ReturnType<typeof setTimeout>) => clearTimeout(id),
});
Object.assign(globalThis, {
  window: win, document: doc,
  HTMLElement: win.HTMLElement, Node: win.Node, Event: win.Event,
  ResizeObserver: class { observe() {} unobserve() {} disconnect() {} },
});
// navigator в Node — со встроенным getter'ом, присвоить нельзя (путь tasksPanelRender.test.ts)
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const { TaskBoard } = await import('./TaskBoard');
const { DEFAULT_BOARD_COLUMNS } = await import('../../../lib/tasks');
const { setAssigneeFilter, useBoardControls } = await import('../../../lib/boardControls');

// === Фикстуры ===

let seq = 0;
function mk(over: Partial<Task> = {}): Task {
  seq++;
  return {
    id: `t${seq}`, title: `Задача ${seq}`, description: '',
    status: 'todo', priority: 'medium', linkedFiles: [], subtasks: [], labels: [],
    order: 0, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
    ...over,
  };
}
const TASKS = [mk(), mk({ title: 'Вторая' })];

const P_SHARED_A: Project = { id: 'p-a', name: 'Общий A', myRole: 'owner', members: { count: 2 } } as unknown as Project;
const P_SHARED_B: Project = { id: 'p-b', name: 'Общий B', myRole: 'owner', members: { count: 1 } } as unknown as Project;
// Личный проект: myRole нет, участников нет → isSharedProjectView false
const P_PERSONAL: Project = { id: 'p-p', name: 'Личный' } as unknown as Project;
const MAPS = new Map<string, Project>([[P_SHARED_A.id, P_SHARED_A], [P_SHARED_B.id, P_SHARED_B], [P_PERSONAL.id, P_PERSONAL]]);

// Проб состояния стора: то же состояние, что читает тулбар (общий синглтон)
function StoreProbe() {
  const c = useBoardControls();
  return createElement('span', { 'data-testid': 'store-probe' }, `${c.assignee}|${c.assigneeUserId ?? ''}`);
}

const probeText = () => (doc.querySelector('[data-testid="store-probe"]')?.textContent ?? '');
const visibleTitles = () => [...doc.querySelectorAll('[data-testid="board-card"]')].map(n => n.textContent ?? '');

async function mountBoard() {
  const root = createRoot(doc.getElementById('root') as unknown as HTMLElement);
  const render = (projectId: string | null, scope: 'hub' | 'project') => act(async () => {
    root.render(createElement('div', null,
      createElement(TaskBoard, {
        tasks: TASKS, columns: DEFAULT_BOARD_COLUMNS, projectsById: MAPS,
        onOpenTask: () => {}, isMobile: true,
        quickAddProjectId: scope === 'project' ? projectId : null, scope, inlineToolbar: false,
      }),
      createElement(StoreProbe),
    ));
    await Promise.resolve(); // список участников (useProjectPeople) приходит асинхронно
  });
  return { root, render };
}

describe('Сброс «Участников» при смене контекста доски (вердикт 136d1069, п.1)', () => {
  beforeEach(() => { setAssigneeFilter('all'); });
  afterEach(() => { setAssigneeFilter('all'); });

  it('общий → личный: «member» сброшен, доска личного проекта не пустая', async () => {
    const { root, render } = await mountBoard();
    await render('p-a', 'project');
    act(() => { setAssigneeFilter('member', 'u-mary'); });   // пользователь выбрал участника в общем
    expect(probeText(), 'в общем проекте фильтр живёт').toBe('member|u-mary');

    await render('p-p', 'project');                          // тот же экран — доска личного проекта
    expect(probeText(), 'вне общего проекта «member» сброшен').toBe('all|');
    expect(visibleTitles(), 'доска личного проекта показывает задачи').toEqual(['Задача 1', 'Вторая']);
    await act(async () => { root.unmount(); });
  });

  it('общий A → общий B: «member» сброшен (id участника принадлежит прежнему проекту)', async () => {
    const { root, render } = await mountBoard();
    await render('p-a', 'project');
    act(() => { setAssigneeFilter('member', 'u-mary'); });
    expect(probeText()).toBe('member|u-mary');

    await render('p-b', 'project');                          // смена общего проекта
    expect(probeText(), 'при смене проекта «member» сброшен').toBe('all|');
    await act(async () => { root.unmount(); });
  });

  it('хаб-доска: оставшийся от проектной доски «member» сброшен', async () => {
    act(() => { setAssigneeFilter('member', 'u-mary'); });   // остаток от прежней доски
    const { root, render } = await mountBoard();
    await render(null, 'hub');
    expect(probeText(), 'в хаб-доске «member» не может быть').toBe('all|');
    expect(visibleTitles(), 'хаб-доска показывает задачи').toEqual(['Задача 1', 'Вторая']);
    await act(async () => { root.unmount(); });
  });

  it('та же общая доска, тот же проект: сброса нет (фильтр живёт, пока он уместен)', async () => {
    const { root, render } = await mountBoard();
    await render('p-a', 'project');
    act(() => { setAssigneeFilter('member', 'u-mary'); });
    await render('p-a', 'project');                          // перерисовка той же доски
    expect(probeText(), 'на той же общей доске фильтр не трогается').toBe('member|u-mary');
    await act(async () => { root.unmount(); });
  });
});
