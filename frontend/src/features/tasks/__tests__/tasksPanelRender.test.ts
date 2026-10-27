// Производительность списка «Задачи»: изменение одной задачи не должно перерисовывать
// все карточки, а группа «Готово» рисуется окном по 50. Реальный TasksPanel рендерим
// через react-dom/client поверх linkedom (jsdom в проекте нет), перерисовки TaskCard
// считаем по вызовам useContextButton — он зовётся ровно один раз за рендер карточки.
import { describe, it, expect, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { Task } from '../../../types';

const m = vi.hoisted(() => ({
  listAll: vi.fn<() => Promise<unknown[]>>(async () => []),
  cardRenders: vi.fn(),   // рендеры тела настоящего TaskCard (по useContextButton)
  outerRenders: vi.fn(),  // рендеры слоя НАД TaskCard (TaskRow / BoardCard): обёртка-шпион ниже
}));

vi.mock('../../../lib/api', () => ({ api: { tasks: { listAll: () => m.listAll() } } }));
vi.mock('../../../lib/signalr', () => ({
  joinUser: async () => {}, onMessage: () => () => {}, onReconnected: () => () => {},
}));
vi.mock('../../../lib/idb', () => ({
  idbGet: async () => undefined, idbSet: async () => {}, idbClear: async () => {},
  idbKeys: async () => [], idbDelete: async () => {},
  outboxAll: async () => [], outboxGet: async () => undefined,
  outboxPut: async () => {}, outboxDelete: async () => {},
}));
vi.mock('../../chatContext/useContextButton', () => ({
  useContextButton: () => {
    m.cardRenders();
    return { available: false, inContext: false, title: '', toggle: () => {} };
  },
}));

// Шпион слоя над карточкой: стоит между TaskRow/BoardCard и настоящим TaskCard и исполняется
// ровно тогда, когда отрендерился вызывающий слой. Так memo TaskRow/BoardCard проверяется
// отдельно от memo TaskCard (последний в списке прикрывает остальные). RealTaskCard —
// настоящий memo-компонент для прямой проверки его собственного memo.
vi.mock('../TaskCard', async importOriginal => {
  const orig = await importOriginal<typeof import('../TaskCard')>();
  const { createElement: h } = await import('react');
  const Spy = (props: React.ComponentProps<typeof orig.TaskCard>) => {
    m.outerRenders();
    return h(orig.TaskCard, props);
  };
  return { ...orig, TaskCard: Spy, RealTaskCard: orig.TaskCard };
});

// DOM для react-dom: linkedom + недостающие браузерные API
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

// Динамический импорт ПОСЛЕ стабов: модули читают window/document при загрузке
const { TasksPanel } = await import('../TasksPanel');
const { TaskBoard } = await import('../board/TaskBoard');
const { BoardCell } = await import('../board/BoardColumn');
const { RealTaskCard } = await import('../TaskCard') as unknown as { RealTaskCard: typeof import('../TaskCard').TaskCard };
const { upsertTaskLocal, DEFAULT_BOARD_COLUMNS } = await import('../../../lib/tasks');

function task(i: number, over: Partial<Task> = {}): Task {
  return {
    id: `t${i}`, title: `Задача ${i}`, status: 'todo', priority: 'medium', assignee: 'claude',
    projectId: 'p1', linkedFiles: [], subtasks: [], labels: [], order: i,
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', ...over,
  } as Task;
}

const TODO = 10;
const DONE = 1990;

function seed(): Task[] {
  const list: Task[] = [];
  for (let i = 0; i < TODO; i++) list.push(task(i));
  // completedAt растёт с номером: самые свежие — с большим i
  for (let i = 0; i < DONE; i++) {
    // Валидная ISO-дата: i секунд от 2026-03-01 (до ~33 минут, без переполнения полей)
    const completedAt = new Date(Date.UTC(2026, 2, 1, 0, 0, i)).toISOString();
    list.push(task(TODO + i, { status: 'done', completedAt }));
  }
  return list;
}

describe('список задач: перерисовки и окно «Готово»', () => {
  it('одна изменённая задача → одна перерисовка карточки; «Готово» окном по 50', async () => {
    const all = seed();
    m.listAll.mockResolvedValue(all);

    const root = createRoot(doc.getElementById('root') as unknown as HTMLElement);
    const project = { id: 'p1', name: 'P' } as never;
    await act(async () => {
      root.render(createElement(TasksPanel, {
        project, selectedTaskId: null, onSelect: () => {}, isMobile: true,
      }));
    });
    await act(async () => { await Promise.resolve(); });

    // Окно: 10 «к выполнению» + 50 «готово», а не 2000
    expect(m.cardRenders).toHaveBeenCalledTimes(TODO + 50);
    expect(doc.body.textContent).toContain('Показать ещё 50');

    // Меняем одну видимую завершённую задачу (самую свежую)
    const newest = all[all.length - 1];
    m.cardRenders.mockClear();
    await act(async () => { upsertTaskLocal({ ...newest, title: 'Переименована' }); });
    expect(m.cardRenders).toHaveBeenCalledTimes(1);
    expect(doc.body.textContent).toContain('Переименована');

    // «Показать ещё 50» добавляет ровно следующие 50 карточек
    m.cardRenders.mockClear();
    const btn = [...doc.querySelectorAll('button')].find(b => (b.textContent ?? '').includes('Показать ещё'));
    expect(btn).toBeTruthy();
    await act(async () => { (btn as unknown as HTMLElement).click(); });
    expect(m.cardRenders).toHaveBeenCalledTimes(50);

    await act(async () => { root.unmount(); });
  });
  // Рендер панели с заданным onSelect; возвращает root для перерисовки и снятия
  async function mountPanel(all: Task[]) {
    m.listAll.mockResolvedValue(all);
    const root = createRoot(doc.getElementById('root') as unknown as HTMLElement);
    const project = { id: 'p1', name: 'P' } as never;
    const render = (onSelect: () => void) => act(async () => {
      root.render(createElement(TasksPanel, { project, selectedTaskId: null, onSelect, isMobile: true }));
    });
    await render(() => {});
    await act(async () => { await Promise.resolve(); });
    return { root, render };
  }

  it('слой TaskRow: перерисовка панели с новым onSelect не трогает строки (inline-колбэк / снятие memo краснят)', async () => {
    const { root, render } = await mountPanel(seed());
    m.outerRenders.mockClear(); m.cardRenders.mockClear();
    await render(() => {});   // новая функция onSelect, остальные пропсы те же
    expect(m.outerRenders).toHaveBeenCalledTimes(0);
    expect(m.cardRenders).toHaveBeenCalledTimes(0);
    await act(async () => { root.unmount(); });
  });

  it('слой TaskCard: тот же набор пропсов при перерисовке родителя не перерисовывает тело карточки', async () => {
    const t = task(9001);
    const noop = () => {};
    const root = createRoot(doc.getElementById('root') as unknown as HTMLElement);
    const render = () => act(async () => { root.render(createElement(RealTaskCard, { task: t, onClick: noop })); });
    await render();
    m.cardRenders.mockClear();
    await render();   // новый элемент, пропсы по ссылкам прежние
    expect(m.cardRenders).toHaveBeenCalledTimes(0);
    await act(async () => { root.unmount(); });
  });

  it('доска: новый inline onOpenTask у вызывающего не перерисовывает BoardCard и карточки', async () => {
    const tasks = Array.from({ length: 5 }, (_, i) => task(i));
    const root = createRoot(doc.getElementById('root') as unknown as HTMLElement);
    const projectsById = new Map();   // стабильная ссылка, как у useMemo вызывающего
    const render = (onOpenTask: (t: Task) => void) => act(async () => {
      root.render(createElement(TaskBoard, {
        tasks, columns: DEFAULT_BOARD_COLUMNS, projectsById, onOpenTask, isMobile: true,
      }));
    });
    await render(() => {});
    expect(m.outerRenders).toHaveBeenCalled();
    m.outerRenders.mockClear(); m.cardRenders.mockClear();
    await render(() => {});   // inline-стрелка: каждый рендер родителя — новая ссылка
    expect(m.outerRenders).toHaveBeenCalledTimes(0);
    expect(m.cardRenders).toHaveBeenCalledTimes(0);
    await act(async () => { root.unmount(); });
  });
  it('окно ячейки доски выводится из windowSize: смена пропса пересчитывает лимит и кнопку', async () => {
    const cards = Array.from({ length: 5 }, (_, i) => task(i));
    const root = createRoot(doc.getElementById('root') as unknown as HTMLElement);
    const render = (windowSize?: number) => act(async () => {
      root.render(createElement(BoardCell, {
        cellId: 'l::c', cards, windowSize, projectNameOf: () => undefined, onOpen: () => {}, minEmptyHeight: 10,
      }));
    });
    const more = () => [...doc.querySelectorAll('button')].some(b => (b.textContent ?? '').includes('Показать ещё'));
    await render(undefined);          // без окна: видны все 5
    m.outerRenders.mockClear();
    await render(2);                  // колонка стала «Готово»: окно 2 + кнопка
    expect(more()).toBe(true);
    expect(doc.querySelectorAll('[aria-roledescription="sortable"]').length).toBe(2);
    await act(async () => { root.unmount(); });
  });
});
