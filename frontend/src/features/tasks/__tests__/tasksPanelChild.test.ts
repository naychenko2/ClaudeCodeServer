// Х-W4-35 (дефект a6ba38a0, ревью 57958a7e): детская панель «Задачи» берёт данные из разрешённой
// детской ручки GET /api/projects/{id}/tasks ([ChildAllowed], TasksController.cs:65-93), а не из
// взрослой GET /api/tasks — та ребёнку закрыта (403, доска пустая). Ответ ребёнка — обёртка
// { tasks, viewers }: панель unpack'ит её и берёт только tasks («Эту доску видят» несёт детская
// шапка урока, не панель). Взрослый путь без изменений: свой стор, своя ручка, детская не зовётся.
// Окружение — как tasksPanelRole.test.ts: реальный TasksPanel поверх linkedom.
//
// Стор задач и стор me — модульное состояние: каждый кейс начинает с чистых модулей
// (vi.resetModules + динамический импорт), иначе «взрослая ручка позвана» зависела бы от того,
// load'ил ли стор предыдущий кейс, а не от поведения панели.

import { describe, it, expect, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { Me, Project, Task } from '../../../types';

// Шпионы живут вне графа модулей — переживают resetModules и accumulate'ятся между кейсами,
// поэтому каждый кейс обнуляет их перед монтированием
const m = vi.hoisted(() => ({
  // взрослая ручка (GET /api/tasks) — ребёнку зваться не должна
  listAll: vi.fn<() => Promise<Task[]>>(async () => []),
  // разрешённая детская ручка (GET /projects/{id}/tasks) — обёртка, как у бэка
  created: null as unknown,
  listChild: vi.fn<(id: string) => Promise<{ tasks: Task[]; viewers: string[] }>>(async () => ({ tasks: [], viewers: [] })),
}));

vi.mock('../../../lib/api', () => ({
  api: {
    tasks: { listAll: () => m.listAll(), listByProjectForChild: (id: string) => m.listChild(id) },
    projects: { members: { list: async () => VIEW } },
  },
}));
// Диалог создания — заглушка: кнопка отдаёт панели созданную задачу через onCreated, как настоящий
// NewTaskDialog после успешного createTask (сам диалог и сеть здесь не проверяются)
vi.mock('../NewTaskDialog', () => ({
  NewTaskDialog: (p: { onCreated: (t: unknown, configure: boolean) => void }) =>
    createElement('button', { id: 'dlg-create', onClick: () => p.onCreated(m.created, false) }, 'Создать'),
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
// Список участников (useProjectPeople) — шпион на границе: имена здесь не нужны
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

const VIEW = {
  owner: { userId: 'u-child', username: 'masha', displayName: 'Маша' },
  members: [] as unknown[],
  myRole: 'owner',
};

const project = { id: 'p1', name: 'Математика', ownerId: 'u-child' } as Project;

const task = (i: number): Task => ({
  id: `t${i}`, title: `Задача ${i}`, status: 'todo', priority: 'medium', assignee: 'me',
  projectId: 'p1', linkedFiles: [], subtasks: [], labels: [], order: i,
  createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
}) as Task;

// Чистый граф модулей для кейса: стор задач и стор me — модульное состояние, их начальное
// состояние и есть проверяемое условие («ребёнок не load'ит взрослый стор», «взрослый load'ит»)
async function freshModules() {
  vi.resetModules();
  const { TasksPanel } = await import('../TasksPanel');
  const { setMeFromServer } = await import('../../../lib/defaultPersona');
  return { TasksPanel, setMeFromServer };
}

// Мобильная раскладка: список и контролы в теле панели — то, что читает ребёнок
async function mountPanel(TasksPanel: typeof import('../TasksPanel').TasksPanel) {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  await act(async () => {
    root.render(createElement(TasksPanel, { project, selectedTaskId: null, onSelect: () => {}, isMobile: true }));
  });
  await act(async () => { await Promise.resolve(); });
  return { host, root };
}

describe('панель «Задачи»: ребёнок — разрешённая детская ручка, взрослый — своя', () => {
  it('ребёнок: доска из обёртки { tasks, viewers } детской ручки, взрослая ручка не зовётся', async () => {
    const { TasksPanel, setMeFromServer } = await freshModules();
    setMeFromServer({ userId: 'u-child', isChild: true } as Me);
    m.listChild.mockResolvedValue({ tasks: [task(1), task(2)], viewers: ['Папа'] });
    m.listAll.mockClear(); m.listChild.mockClear();

    const { host, root } = await mountPanel(TasksPanel);
    try {
      expect(m.listChild, 'детская ручка не позвана').toHaveBeenCalledWith('p1');
      expect(m.listAll, 'взрослая GET /api/tasks у ребёнка не должна зваться').not.toHaveBeenCalled();
      // задачи из обёртки дошли до списка
      expect(host.textContent).toContain('Задача 1');
      expect(host.textContent).toContain('Задача 2');
      // обёртка не несётся в панель целиком: viewers — строка детской шапки, не панели
      expect(host.textContent).not.toContain('Папа');
    } finally {
      await act(async () => { root.unmount(); });
    }
  });

  it('взрослый: без изменений — своя ручка и свой стор, детская не зовётся', async () => {
    const { TasksPanel, setMeFromServer } = await freshModules();
    setMeFromServer({ userId: 'u-adult', isChild: false } as Me);
    m.listAll.mockResolvedValue([task(1)]);
    m.listAll.mockClear(); m.listChild.mockClear();

    const { host, root } = await mountPanel(TasksPanel);
    try {
      expect(m.listAll, 'взрослая ручка не позвана').toHaveBeenCalled();
      expect(m.listChild, 'детская ручка у взрослого не должна зваться').not.toHaveBeenCalled();
      expect(host.textContent).toContain('Задача 1');
    } finally {
      await act(async () => { root.unmount(); });
    }
  });

  it('ребёнок: созданная задача сразу появляется на доске без перемонтирования', async () => {
    const { TasksPanel, setMeFromServer } = await freshModules();
    setMeFromServer({ userId: 'u-child', isChild: true } as Me);
    m.listChild.mockResolvedValue({ tasks: [task(1)], viewers: [] });
    m.created = task(2);

    const { host, root } = await mountPanel(TasksPanel);
    try {
      expect(host.textContent).not.toContain('Задача 2');
      const open = Array.from(host.querySelectorAll('button')).find(b => b.textContent?.includes('Новая задача'))!;
      await act(async () => { open.dispatchEvent(new win.Event('click', { bubbles: true })); });
      await act(async () => {
        doc.getElementById('dlg-create')!.dispatchEvent(new win.Event('click', { bubbles: true }));
      });
      expect(host.textContent, 'созданная задача не попала на доску').toContain('Задача 2');
      expect(host.textContent).toContain('Задача 1');
    } finally {
      await act(async () => { root.unmount(); });
    }
  });

  it('ребёнок: отказ детской ручки — не молчаливая пустота, а ошибка', async () => {
    const { TasksPanel, setMeFromServer } = await freshModules();
    setMeFromServer({ userId: 'u-child', isChild: true } as Me);
    m.listChild.mockRejectedValue(new Error('403'));

    const { host, root } = await mountPanel(TasksPanel);
    try {
      expect(host.textContent, 'ошибка не показана').toContain('Не удалось загрузить задачи');
      expect(host.textContent).not.toContain('Задач пока нет');
    } finally {
      await act(async () => { root.unmount(); });
    }
  });
});
