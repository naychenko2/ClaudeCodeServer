// S2b-F6: бэк принял AssigneeUserId (6824ba720, S2b-2a принят) — бросок на дорожку участника
// включён. Контракт (TasksController + TaskManager.Update): id = назначить (бэк ставит и
// Assignee=Me), "" = снять (обратно к владельцу), не слать = не менять. Отказ двери (не участник /
// читатель / личная задача) — обычный путь ошибки: тост с серверным текстом + reloadTasks, это
// и есть откат оптимистичного движения. Броски на 'me'/'claude' (личная доска/хаб) теперь тоже
// шлют "": иначе прежний участник остаётся в записи — карточка на дорожке AI показывала бы чип
// человека вместо значка Claude. Проверка — настоящий onDragEnd TaskBoard: onDragEnd DndContext
// перехвачен шпионом (остальной dnd-kit — настоящий), запрос/движение/тост/reload — шпионы на
// границах. Контроль: броски прочих групп (priority) не изменились.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { Project, ProjectMembersView, Task } from '../../../types';

const m = vi.hoisted(() => ({
  update: vi.fn<(id: string, dto: unknown) => Promise<void>>(async () => {}),
  upsert: vi.fn(),
  toast: vi.fn(),
  reload: vi.fn(),
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
// Границы стора: движение карточки (upsertTaskLocal), запрос (updateTask) и откат (reloadTasks)
// — шпионы, остальная логика lib/tasks (дорожки, порядок, колонки) — настоящая
vi.mock('../../../lib/tasks', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/tasks')>();
  return {
    ...orig,
    updateTask: (id: string, dto: unknown) => m.update(id, dto),
    upsertTaskLocal: (t: Task) => { m.upsert(t); },
    reloadTasks: () => { m.reload(); },
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

// === Фикстуры: задачи общего проекта — у владельца (свои), у Марии (assigneeUserId), AI.
// В хабе те же задачи разложены по прежним дорожкам 'me'/'claude' (по assignee) — в хабе
// карточка участника стоит на 'me', отдельной дорожки участника там нет.
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
const tUrgent = mk({ id: 't-urgent', title: 'Срочная', assignee: 'me', assigneeUserId: 'u-own', priority: 'urgent' });
const P_SHARED = { id: 'p1', name: 'Общий', myRole: 'owner', members: { count: 2 } } as unknown as Project;

async function mountBoard(tasks: Task[] = [tMine, tMary, tAi], scope: 'hub' | 'project' = 'project') {
  m.onDragEnd = undefined;
  const root = createRoot(doc.getElementById('root') as unknown as HTMLElement);
  await act(async () => {
    root.render(createElement(TaskBoard, {
      tasks, columns: DEFAULT_BOARD_COLUMNS,
      projectsById: new Map([[P_SHARED.id, P_SHARED]]),
      onOpenTask: () => {}, isMobile: true,
      quickAddProjectId: scope === 'project' ? 'p1' : undefined,
      scope, inlineToolbar: false,
    }));
    await Promise.resolve(); // список участников (useProjectPeople) приходит асинхронно
  });
  expect(m.onDragEnd, 'DndContext отрендерен, onDragEnd перехвачен').toBeTruthy();
  return root;
}

describe('S2b-F6: бросок на дорожку участника включён', () => {
  beforeEach(() => {
    m.update.mockReset(); m.update.mockImplementation(async () => {});
    m.upsert.mockReset(); m.toast.mockReset(); m.reload.mockReset();
    setGroupBy('assignee'); setAssigneeFilter('all');
  });
  afterEach(() => { setGroupBy('none'); setAssigneeFilter('all'); });

  it('бросок на дорожку «Мария»: шлёт assignee:me + assigneeUserId:id и двигает карточку оптимистично', async () => {
    const root = await mountBoard();
    await act(async () => { m.onDragEnd!({ active: { id: 't-mine' }, over: { id: 'u:u-mary::todo' } } as never); });
    expect(m.update, 'назначения на участника').toHaveBeenCalledWith(
      't-mine', expect.objectContaining({ assignee: 'me', assigneeUserId: 'u-mary' }));
    expect(m.upsert, 'карточка двигается сразу, до ответа сервера').toHaveBeenCalledWith(
      expect.objectContaining({ id: 't-mine', assignee: 'me', assigneeUserId: 'u-mary' }));
    expect(m.toast, 'никакой подсказки — бросок работает').not.toHaveBeenCalled();
    await act(async () => { root.unmount(); });
  });

  it('бросок участник→AI: шлёт assignee:claude + assigneeUserId:"" и оптимистично снимает участника', async () => {
    const root = await mountBoard();
    await act(async () => { m.onDragEnd!({ active: { id: 't-mary' }, over: { id: 'claude::todo' } } as never); });
    expect(m.update, 'снятие участника вместе со сменой на AI').toHaveBeenCalledWith(
      't-mary', expect.objectContaining({ assignee: 'claude', assigneeUserId: '' }));
    expect(m.upsert, 'чип участника с карточки снимается сразу').toHaveBeenCalledWith(
      expect.objectContaining({ id: 't-mary', assignee: 'claude', assigneeUserId: '' }));
    await act(async () => { root.unmount(); });
  });

  it('бросок на me/claude в хабе: шлёт assigneeUserId:"" (иначе участник остаётся в записи)', async () => {
    // Хаб: карточки общего проекта разложены по прежним дорожкам 'me'/'claude' — здесь
    // обе 'me'/'claude'-ветки достижимы настоящим броском
    const root = await mountBoard([tMine, tMary, tAi], 'hub');
    await act(async () => { m.onDragEnd!({ active: { id: 't-mary' }, over: { id: 'claude::todo' } } as never); });
    expect(m.update, 'участник → AI').toHaveBeenCalledWith(
      't-mary', expect.objectContaining({ assignee: 'claude', assigneeUserId: '' }));
    await act(async () => { m.onDragEnd!({ active: { id: 't-ai' }, over: { id: 'me::todo' } } as never); });
    // у AI-задачи участника нет: "" для бэка — снятие несуществующего поля (no-op), безопасно
    expect(m.update, 'AI → человек: тот же dto с ""').toHaveBeenCalledWith(
      't-ai', expect.objectContaining({ assignee: 'me', assigneeUserId: '' }));
    await act(async () => { root.unmount(); });
  });

  it('отказ сервера: тост с его текстом и reloadTasks (откат), карточка двигалась оптимистично', async () => {
    m.update.mockImplementationOnce(() => Promise.reject(new Error('«u-mary» не участник проекта задачи')));
    const root = await mountBoard();
    await act(async () => {
      m.onDragEnd!({ active: { id: 't-mine' }, over: { id: 'u:u-mary::todo' } } as never);
      await Promise.resolve(); await Promise.resolve(); // прогнать отказ через очередь микротaskов до проверок
    });
    expect(m.upsert, 'оптимистичное движение было до отказа').toHaveBeenCalledTimes(1);
    expect(m.toast, 'текст сервера показывается как есть').toHaveBeenCalledWith(
      'Не удалось переместить карточку', '«u-mary» не участник проекта задачи', 'error');
    expect(m.reload, 'перезагрузка стора откатывает движение').toHaveBeenCalledTimes(1);
    await act(async () => { root.unmount(); });
  });

  it('контроль: бросок по приоритету не изменился — ни assignee-, ни assigneeUserId-полей', async () => {
    setGroupBy('priority');
    const root = await mountBoard([tMine, tMary, tAi, tUrgent]);
    await act(async () => { m.onDragEnd!({ active: { id: 't-mine' }, over: { id: 'urgent::todo' } } as never); });
    expect(m.update, 'смена приоритета').toHaveBeenCalledWith(
      't-mine', expect.objectContaining({ priority: 'urgent' }));
    const dto = m.update.mock.calls[0][1] as Record<string, unknown>;
    expect(dto, 'приоритетный бросок не трогает исполнителя').not.toHaveProperty('assignee');
    expect(dto).not.toHaveProperty('assigneeUserId');
    await act(async () => { root.unmount(); });
  });
});
