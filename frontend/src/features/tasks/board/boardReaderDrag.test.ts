// F2-Q3 (разрез CONSULT-TASK-RIGHTS): доска общего проекта для читателя не даёт начать
// бросок. Запрет — на уровне активации dnd: useSortable(disabled) не отдаёт ни transform,
// ни активационных listeners (в DOM это aria-disabled="true" на карточке и отсутствие
// сенсорных обработчиков), поэтому карточка не едет за курсором и не возвращается после
// отпускания. Быстрое добавление в ячейках скрыто, подсказка «Перетащите сюда» не звучит.
// Клик по карточке — работает (выбор и открытие карточки не тронуты). Владелец и редактор —
// без изменений. Настоящий TaskBoard поверх linkedom; границы стора — шпионы.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { Project, ProjectMembersView, Task } from '../../../types';

const m = vi.hoisted(() => ({
  update: vi.fn<(id: string, dto: unknown) => Promise<void>>(async () => {}),
  upsert: vi.fn(),
  create: vi.fn(),
  view: {
    owner: { userId: 'u-own', displayName: 'Андрей' },
    members: [{ userId: 'u-mary', role: 'editor', displayName: 'Мария' }, { userId: 'u-read', role: 'reader', displayName: 'Чтец' }],
    myRole: 'owner',
  } as ProjectMembersView,
}));

vi.mock('../../../lib/tasks', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/tasks')>();
  return {
    ...orig,
    updateTask: (id: string, dto: unknown) => m.update(id, dto),
    upsertTaskLocal: (t: Task) => { m.upsert(t); },
    createTask: (...a: unknown[]) => { m.create(...a); return Promise.resolve(); },
  };
});
vi.mock('../../../lib/toast', () => ({ showToast: () => {} }));
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
vi.mock('../../chatContext/useContextButton', () => ({
  useContextButton: () => ({ available: false, inContext: false, title: '', toggle: () => {} }),
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
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const { TaskBoard } = await import('./TaskBoard');
const { DEFAULT_BOARD_COLUMNS } = await import('../../../lib/tasks');
const { setGroupBy } = await import('../../../lib/boardControls');

function mk(over: Partial<Task>): Task {
  return {
    id: 't', title: 'Т', description: '', status: 'todo', priority: 'medium',
    linkedFiles: [], subtasks: [], labels: [], order: 0,
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
    ...over,
  };
}
const tOne = mk({ id: 't-1', title: 'Одна', assignee: 'me' });

const P = (myRole: Project['myRole']): Project =>
  ({ id: 'p1', name: 'Общий', ownerId: 'u-own', myRole, members: { count: 2 } }) as unknown as Project;

async function mountBoard(p: Project, onOpenTask = () => {}) {
  m.create.mockReset();
  const root = createRoot(doc.getElementById('root') as unknown as HTMLElement);
  await act(async () => {
    root.render(createElement(TaskBoard, {
      tasks: [tOne], columns: DEFAULT_BOARD_COLUMNS,
      projectsById: new Map([[p.id, p]]),
      onOpenTask, isMobile: true,
      quickAddProjectId: p.id, scope: 'project', inlineToolbar: false,
    }));
    await Promise.resolve(); // список участников (useProjectPeople) приходит асинхронно
  });
  return root;
}

// Карточки доски: их отмечает aria-roledescription="sortable" (его dnd-kit ставит и
// активной, и выключенной карточке), а запрещённость броска — aria-disabled="true"
const sortableCards = () => Array.from(doc.querySelectorAll('[aria-roledescription="sortable"]'));
const hasButton = (text: string) =>
  Array.from(doc.querySelectorAll('button')).some(b => (b.textContent ?? '').trim() === text);

describe('F2-Q3: доска читателя общего проекта — бросок не начинается', () => {
  beforeEach(() => {
    m.update.mockReset(); m.update.mockImplementation(async () => {});
    m.upsert.mockReset(); m.create.mockReset();
    setGroupBy('none');
  });
  afterEach(() => { setGroupBy('none'); });

  it('читатель: карточка выключена для dnd (aria-disabled) — бросок не начнётся вовсе', async () => {
    const root = await mountBoard(P('reader'));
    const cards = sortableCards();
    expect(cards.length, 'карточка отрендерена — читаемость списка не пострадала').toBeGreaterThan(0);
    for (const c of cards) {
      expect(c.getAttribute('aria-disabled'), 'запрет на уровне активации сенсора, а не отказа после отпускания').toBe('true');
    }
    expect(m.update, 'без активации нет и onDragEnd — запроса на перемещение не было').not.toHaveBeenCalled();
    await act(async () => { root.unmount(); });
  });

  it('читатель: быстрого добавления в ячейках нет и «Перетащите сюда» не звучит', async () => {
    const root = await mountBoard(P('reader'));
    expect(hasButton('Добавить'), 'вход создания задачи скрыт').toBe(false);
    expect(doc.body.textContent, 'пустая колонка не обещает бросок').not.toContain('Перетащите сюда');
    await act(async () => { root.unmount(); });
  });

  it('владелец: карточка активна (без aria-disabled), «Добавить» и «Перетащите сюда» на месте', async () => {
    const root = await mountBoard(P('owner'));
    const cards = sortableCards();
    expect(cards.length).toBeGreaterThan(0);
    for (const c of cards) {
      expect(c.getAttribute('aria-disabled'), 'карточка перетаскивается, как раньше').not.toBe('true');
    }
    expect(hasButton('Добавить')).toBe(true);
    expect(doc.body.textContent).toContain('Перетащите сюда');
    await act(async () => { root.unmount(); });
  });

  it('редактор: та же доска, что у владельца', async () => {
    const root = await mountBoard(P('editor'));
    const cards = sortableCards();
    expect(cards.length).toBeGreaterThan(0);
    for (const c of cards) {
      expect(c.getAttribute('aria-disabled')).not.toBe('true');
    }
    expect(hasButton('Добавить')).toBe(true);
    await act(async () => { root.unmount(); });
  });

  it('запрет броска не ломает клик: карточка читателя открывает задачу', async () => {
    let opened: Task | null = null;
    const root = await mountBoard(P('reader'), (t) => { opened = t; });
    const cards = sortableCards();
    expect(cards.length).toBeGreaterThan(0);
    expect(cards[0].getAttribute('aria-disabled')).toBe('true');
    // Клик — по самому глубокому узлу с названием карточки: он внутри элемента с onClick,
    // событие всплывает до него (с самой карточки вниз оно бы не дошло)
    const titled = Array.from(doc.querySelectorAll('div,span')).filter(el => (el.textContent ?? '').trim() === 'Одна');
    const target = titled[titled.length - 1];
    expect(target, 'заголовок карточки на месте').toBeTruthy();
    await act(async () => { target.dispatchEvent(new win.Event('click', { bubbles: true })); });
    expect(opened, 'клик по карточке работает как раньше').not.toBeNull();
    expect(opened!.id).toBe('t-1');
    await act(async () => { root.unmount(); });
  });
});
