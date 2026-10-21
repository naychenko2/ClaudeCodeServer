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
  cardRenders: vi.fn(),
}));

vi.mock('../../../lib/api', () => ({ api: { tasks: { listAll: () => m.listAll() } } }));
vi.mock('../../../lib/signalr', () => ({
  joinUser: async () => {}, onMessage: () => () => {}, onReconnected: () => () => {},
}));
vi.mock('../../../lib/idb', () => ({
  idbGet: async () => undefined, idbSet: async () => {}, idbClear: async () => {},
  outboxAll: async () => [], outboxGet: async () => undefined,
  outboxPut: async () => {}, outboxDelete: async () => {},
}));
vi.mock('../../chatContext/useContextButton', () => ({
  useContextButton: () => {
    m.cardRenders();
    return { available: false, inContext: false, title: '', toggle: () => {} };
  },
}));

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
vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
vi.stubGlobal('sessionStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

// Динамический импорт ПОСЛЕ стабов: модули читают window/document при загрузке
const { TasksPanel } = await import('../TasksPanel');
const { upsertTaskLocal } = await import('../../../lib/tasks');

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
    const day = String(1 + Math.floor(i / 100)).padStart(2, '0');
    const sec = String(i % 100).padStart(2, '0');
    list.push(task(TODO + i, { status: 'done', completedAt: `2026-03-${day}T00:00:${sec}Z` }));
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
});
