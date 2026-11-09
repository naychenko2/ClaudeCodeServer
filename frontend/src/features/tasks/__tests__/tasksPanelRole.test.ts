// F2-Q3 (разрез CONSULT-TASK-RIGHTS): панель «Задачи» не обещает того, что читатель
// общего проекта не может. Читатель: ни залитой «Задача» в шапке карточки (pinned-слот),
// ни «Новая задача» в теле панели (мобильная раскладка без шапки); пустое состояние
// вместо «поставьте первую задачу» несёт ту же строку, что панель деталей. Редактор и
// владелец — без изменений. Реальный TasksPanel поверх linkedom; шапка — настоящим
// PanelHeaderSlotContext (portal в свой DOM-узел), как это делает PanelShell.
import { describe, it, expect, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { Project, Task } from '../../../types';

const m = vi.hoisted(() => ({ listAll: vi.fn<() => Promise<Task[]>>(async () => []) }));

vi.mock('../../../lib/api', () => ({
  api: { tasks: { listAll: () => m.listAll() }, projects: { members: { list: async () => VIEW } } },
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
// Список участников (useProjectPeople) и кэш проектов — шпионы на границе: право решается
// по myRole вызываемого проекта, имена участников здесь не нужны
vi.mock('../../projects/members/MessageAuthorLine', () => ({ loadView: () => Promise.resolve(VIEW) }));
vi.mock('../../projects/useAllProjects', () => ({ getCachedProject: () => undefined }));
vi.mock('../../chatContext/useContextButton', () => ({
  useContextButton: () => ({ available: false, inContext: false, title: '', toggle: () => {} }),
}));

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
Object.assign(win, {
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
  requestAnimationFrame: (cb: () => void) => setTimeout(cb, 0),
  cancelAnimationFrame: (id: ReturnType<typeof setTimeout>) => clearTimeout(id),
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
vi.stubGlobal('sessionStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const { TasksPanel } = await import('../TasksPanel');
const { reloadTasks } = await import('../../../lib/tasks');
const { PanelHeaderSlotContext } = await import('../../../components/ui/panelHeaderSlotContext');
const { TASK_WRITE_DENIED } = await import('../../projects/members/participantGate');

const VIEW = {
  owner: { userId: 'u-owner', username: 'andrey', displayName: 'Андрей' },
  members: [{ userId: 'u-m', role: 'editor', displayName: 'Мария' }, { userId: 'u-r', role: 'reader', displayName: 'Чтец' }],
  myRole: 'owner',
};

const project = (myRole: Project['myRole']): Project =>
  ({ id: 'p1', name: 'Общий', ownerId: 'u-owner', myRole, members: { count: 2 } }) as Project;

const task = (i: number): Task => ({
  id: `t${i}`, title: `Задача ${i}`, status: 'todo', priority: 'medium', assignee: 'me',
  projectId: 'p1', linkedFiles: [], subtasks: [], labels: [], order: i,
  createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
}) as Task;

const buttonsByText = (host: Element, text: string) =>
  Array.from(host.querySelectorAll('button')).filter(b => (b.textContent ?? '').trim() === text);

// Стор задач общий на файл: ensureTasksLoaded грузит один раз, поэтому набор задаёт
// reloadTasks перед каждым монтированием — иначе следующий тест видел бы список предыдущего
async function seedTasks(tasks: Task[]) {
  m.listAll.mockResolvedValue(tasks);
  await act(async () => { await reloadTasks(); });
}

// Мобильная раскладка (панели без шапки): контролы живут в теле панели
async function mountMobile(p: Project, tasks: Task[]) {
  await seedTasks(tasks);
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  await act(async () => { root.render(createElement(TasksPanel, { project: p, selectedTaskId: null, onSelect: () => {}, isMobile: true })); });
  await act(async () => { await Promise.resolve(); });
  return { host, root };
}

// Десктопная раскладка: та же панель под настоящим PanelHeaderSlotContext —
// «Задача» уходит порталом в залитый слот шапки (pinned)
async function mountWithHeader(p: Project, tasks: Task[]) {
  await seedTasks(tasks);
  const host = doc.createElement('div');
  const header = doc.createElement('div');
  const headerLeft = doc.createElement('div');
  const headerPinned = doc.createElement('div');
  host.append(header, headerLeft, headerPinned);
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  const ctx = { hasHeader: true, el: header as unknown as HTMLElement, elLeft: headerLeft as unknown as HTMLElement, elPinned: headerPinned as unknown as HTMLElement, hold: () => {} };
  await act(async () => {
    root.render(createElement(PanelHeaderSlotContext.Provider, { value: ctx },
      createElement(TasksPanel, { project: p, selectedTaskId: null, onSelect: () => {}, isMobile: false })));
  });
  await act(async () => { await Promise.resolve(); });
  return { host, headerPinned, root };
}

describe('панель «Задачи»: вход создания только у того, кто правит задачи', () => {
  it('читатель: «Задача» в шапке нет (pinned-слот пуст)', async () => {
    const { headerPinned, root } = await mountWithHeader(project('reader'), [task(1)]);
    expect(buttonsByText(headerPinned, 'Задача')).toHaveLength(0);
    expect(headerPinned.textContent ?? '').not.toContain('Задача');
    await act(async () => { root.unmount(); });
  });

  it('редактор и владелец: «Задача» в шапке на месте', async () => {
    for (const role of ['editor', 'owner'] as const) {
      const { headerPinned, root } = await mountWithHeader(project(role), [task(1)]);
      expect(buttonsByText(headerPinned, 'Задача'), role).toHaveLength(1);
      await act(async () => { root.unmount(); });
    }
  });

  it('читатель (мобила): «Новая задача» в теле панели нет, фильтр остаётся', async () => {
    const { host, root } = await mountMobile(project('reader'), [task(1)]);
    expect(buttonsByText(host, 'Новая задача')).toHaveLength(0);
    // список при этом читается: карточка задачи на месте
    expect(host.textContent).toContain('Задача 1');
    await act(async () => { root.unmount(); });
  });

  it('редактор и владелец (мобила): «Новая задача» на месте', async () => {
    for (const role of ['editor', 'owner'] as const) {
      const { host, root } = await mountMobile(project(role), [task(1)]);
      expect(buttonsByText(host, 'Новая задача'), role).toHaveLength(1);
      await act(async () => { root.unmount(); });
    }
  });

  it('пустое состояние: читателю — та же строка, что в панели деталей; остальным — «поставьте первую»', async () => {
    const reader = await mountMobile(project('reader'), []);
    expect(reader.host.textContent).toContain('Задач пока нет');
    expect(reader.host.textContent).toContain(TASK_WRITE_DENIED);
    expect(reader.host.textContent).not.toContain('Поставьте первую задачу');
    await act(async () => { reader.root.unmount(); });

    const owner = await mountMobile(project('owner'), []);
    expect(owner.host.textContent).toContain('Поставьте первую задачу по проекту.');
    expect(owner.host.textContent).not.toContain(TASK_WRITE_DENIED);
    await act(async () => { owner.root.unmount(); });
  });
});
