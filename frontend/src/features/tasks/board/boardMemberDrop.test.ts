// Доработка S2b-F5 (вердикт ревью 136d1069, п.2): бросок карточки на дорожку участника
// ('u:<id>') до S2b-2 отключён — бэк (TaskManager.UpdateTaskRequest) не принимает поле
// AssigneeUserId, и карточка возвращалась бы на прежнее место. Логика назначения
// сохранена за константой SHARED_ASSIGN_ENABLED=false. Проверка — настоящий onDragEnd
// TaskBoard: onDragEnd DndContext перехвачен шпионом (остальной dnd-kit — настоящий),
// запрос/оптимистичное движение/тост — шпионы на границах. Контроль: бросок на дорожку
// AI работает по-прежнему (иначе тест не отличал бы блок от поломки всей логики).

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { Project, ProjectMembersView, Task } from '../../../types';

const m = vi.hoisted(() => ({
  update: vi.fn<(id: string, dto: unknown) => Promise<void>>(async () => {}),
  upsert: vi.fn(),
  toast: vi.fn(),
  onDragEnd: undefined as ((e: unknown) => void) | undefined,
  view: {
    owner: { userId: 'u-own', displayName: 'Андрей' },
    members: [{ userId: 'u-mary', displayName: 'Мария' }],
    myRole: 'owner',
  } as ProjectMembersView,
}));

// Шпион DndContext: ловим onDragEnd реального TaskBoard, рендер — настоящий dnd-kit
vi.mock('@dnd-kit/core', async (importOriginal) => {
  const orig = await importOriginal<typeof import('@dnd-kit/core')>();
  const { createElement: hc } = await import('react');
  const Spy = (props: Record<string, unknown>) => {
    m.onDragEnd = props.onDragEnd as (e: unknown) => void;
    return hc(orig.DndContext as never, props as never);
  };
  return { ...orig, DndContext: Spy };
});
// Границы стора: движение карточки (upsertTaskLocal) и запрос (updateTask) — шпионы,
// остальная логика lib/tasks (дорожки, порядок, колонки) — настоящая
vi.mock('../../../lib/tasks', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/tasks')>();
  return {
    ...orig,
    updateTask: (id: string, dto: unknown) => { m.update(id, dto); return Promise.resolve(); },
    upsertTaskLocal: (t: Task) => { m.upsert(t); },
  };
});
vi.mock('../../../lib/toast', () => ({ showToast: (...a: unknown[]) => m.toast(...a) }));
vi.mock('../../../lib/api', () => ({
  api: {
    tasks: { search: async () => [] },
    projects: { members: { list: async () => m.view } },
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
const { setAssigneeFilter, setGroupBy } = await import('../../../lib/boardControls');

// === Фикстуры общего проекта: мои задачи (у владельца) — на своей дорожке, у Марии —
// на её, AI-задача — на дорожке AI (дорошки строит настоящий boardLanes по списку участников)

function mk(over: Partial<Task>): Task {
  return {
    id: 't', title: 'Т', description: '', status: 'todo', priority: 'medium',
    linkedFiles: [], subtasks: [], labels: [], order: 0,
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
    ...over,
  };
}
const tMine = mk({ id: 't-mine', title: 'Моя', assignee: 'me', assigneeUserId: 'u-own' });
const tMary = mk({ id: 't-mary', title: 'Мариина', assignee: 'me', assigneeUserId: 'u-mary' });
const tAi = mk({ id: 't-ai', title: 'AI-задача', assignee: 'claude' });
const P_SHARED = { id: 'p1', name: 'Общий', myRole: 'owner', members: { count: 2 } } as unknown as Project;

async function mountBoard() {
  m.onDragEnd = undefined;
  const root = createRoot(doc.getElementById('root') as unknown as HTMLElement);
  await act(async () => {
    root.render(createElement(TaskBoard, {
      tasks: [tMine, tMary, tAi], columns: DEFAULT_BOARD_COLUMNS,
      projectsById: new Map([[P_SHARED.id, P_SHARED]]),
      onOpenTask: () => {}, isMobile: true,
      quickAddProjectId: 'p1', scope: 'project', inlineToolbar: false,
    }));
    await Promise.resolve(); // список участников (useProjectPeople) приходит асинхронно
  });
  expect(m.onDragEnd, 'DndContext отрендерен, onDragEnd перехвачен').toBeTruthy();
  return root;
}

describe('Бросок на дорожку участника отключён до S2b-2 (вердикт 136d1069, п.2)', () => {
  beforeEach(() => { setGroupBy('assignee'); setAssigneeFilter('all'); });
  afterEach(() => { setGroupBy('none'); setAssigneeFilter('all'); });

  it('бросок на дорожку «Мария»: ни запроса, ни движения карточки; только тихая подсказка', async () => {
    const root = await mountBoard();
    await act(async () => { m.onDragEnd!({ active: { id: 't-mine' }, over: { id: 'u:u-mary::todo' } } as never); });
    expect(m.update, 'запроса на сервер нет').toHaveBeenCalledTimes(0);
    expect(m.upsert, 'карточка не двигается: оптимистичного движения нет').toHaveBeenCalledTimes(0);
    expect(m.toast, 'подсказка показана').toHaveBeenCalledWith(
      'Назначение участникам появится позже', 'Карточка осталась на своей дорожке', 'info');
    await act(async () => { root.unmount(); });
  });

  it('контроль: бросок на дорожку AI работает — это именно блок, а не сломанный drag-to-change', async () => {
    const root = await mountBoard();
    await act(async () => { m.onDragEnd!({ active: { id: 't-mine' }, over: { id: 'claude::todo' } } as never); });
    expect(m.update, 'AI-дорожка принимает карточку запросом').toHaveBeenCalledWith(
      't-mine', expect.objectContaining({ assignee: 'claude' }));
    await act(async () => { root.unmount(); });
  });
});
