import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { Task } from '../types';

// Снапшот tasks:all пишется целиком, поэтому не чаще раза в окно (10 с),
// а при уходе вкладки в фон отложенная запись дописывается сразу.

const mocks = vi.hoisted(() => ({ idbSet: vi.fn(async () => {}) }));

vi.mock('./api', () => ({ api: { tasks: {} } }));
vi.mock('./signalr', () => ({
  joinUser: vi.fn(async () => {}), onMessage: vi.fn(), onReconnected: vi.fn(),
}));
vi.mock('./themeMode', () => ({ getEffectiveTheme: () => 'light' }));
vi.mock('./idb', () => ({
  idbGet: vi.fn(async () => undefined), idbSet: mocks.idbSet,
  outboxAll: vi.fn(async () => []), outboxGet: vi.fn(async () => undefined),
  outboxPut: vi.fn(async () => {}), outboxDelete: vi.fn(async () => {}),
}));

type TasksModule = typeof import('./tasks');
let m: TasksModule;
let doc: EventTarget & { visibilityState: string };
let win: EventTarget;

const task = (id: string): Task => ({
  id, title: id, description: '', status: 'todo', priority: 'medium',
  linkedFiles: [], subtasks: [], labels: [], order: 1000,
  createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
} as Task);

const snapshotWrites = () => mocks.idbSet.mock.calls.filter(c => (c as unknown[])[0] === 'tasks:all');

beforeEach(async () => {
  vi.useFakeTimers();
  vi.resetModules();
  mocks.idbSet.mockClear();
  // Среда vitest — node: подставляем минимальные document/window до импорта модуля.
  doc = Object.assign(new EventTarget(), { visibilityState: 'visible' });
  win = new EventTarget();
  vi.stubGlobal('document', doc);
  vi.stubGlobal('window', win);
  m = await import('./tasks');
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('persistTasks — снапшот не чаще раза в окно', () => {
  it('1000 событий по разным id за окно 10 с → не больше одной записи tasks:all', () => {
    for (let i = 0; i < 1000; i++) {
      m.upsertTaskLocal(task(`t${i}`));
      vi.advanceTimersByTime(9); // 1000 × 9 мс = 9 с — внутри окна
    }
    expect(snapshotWrites().length).toBeLessThanOrEqual(1);
  });

  it('по истечении окна отложенная запись уходит ровно один раз', () => {
    m.upsertTaskLocal(task('a'));
    vi.advanceTimersByTime(10_000);
    expect(snapshotWrites()).toHaveLength(1);
  });

  it('visibilitychange → hidden при отложенной записи: ровно один idbSet сразу', () => {
    m.upsertTaskLocal(task('a'));
    expect(snapshotWrites()).toHaveLength(0);
    doc.visibilityState = 'hidden';
    doc.dispatchEvent(new Event('visibilitychange'));
    expect(snapshotWrites()).toHaveLength(1);
    vi.advanceTimersByTime(10_000);
    expect(snapshotWrites()).toHaveLength(1);
  });

  it('pagehide без отложенной записи ничего не пишет', () => {
    win.dispatchEvent(new Event('pagehide'));
    expect(snapshotWrites()).toHaveLength(0);
  });
});
