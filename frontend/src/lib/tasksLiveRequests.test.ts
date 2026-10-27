import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { Task } from '../types';

// Список задач и фич живёт в IndexedDB одним экземпляром (tasks:all): GET /tasks и
// /tasks/features идут с live и в пассивный GET-кэш не пишутся; старые записи кэша сносятся.

const mocks = vi.hoisted(() => ({
  idbSet: vi.fn(async () => 1 as unknown),
  idbGet: vi.fn(async (): Promise<unknown> => undefined),
  idbKeys: vi.fn(async (): Promise<string[]> => []),
  idbDelete: vi.fn(async () => undefined),
}));

vi.mock('./idb', () => ({
  idbGet: mocks.idbGet, idbSet: mocks.idbSet, idbKeys: mocks.idbKeys, idbDelete: mocks.idbDelete,
  outboxAll: vi.fn(async () => []), outboxGet: vi.fn(async () => undefined),
  outboxPut: vi.fn(async () => {}), outboxDelete: vi.fn(async () => {}),
}));
vi.mock('./signalr', () => ({
  joinUser: vi.fn(async () => {}), onMessage: vi.fn(), onReconnected: vi.fn(),
}));
vi.mock('./themeMode', () => ({ getEffectiveTheme: () => 'light' }));

const task = (id: string): Task => ({
  id, title: id, description: '', status: 'todo', priority: 'medium',
  linkedFiles: [], subtasks: [], labels: [], order: 1000,
  createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
} as Task);

beforeEach(() => {
  vi.resetModules();
  Object.values(mocks).forEach(m => m.mockClear());
  mocks.idbGet.mockResolvedValue(undefined);
  mocks.idbKeys.mockResolvedValue([]);
  // Среда vitest — node: минимальные window/document для tasks.ts
  vi.stubGlobal('document', Object.assign(new EventTarget(), { visibilityState: 'visible' }));
  vi.stubGlobal('window', new EventTarget());
});

afterEach(() => { vi.unstubAllGlobals(); });

describe('списки задач и фич — live-запросы', () => {
  it('listAll и getFeatures не пишут URL-ключ в IndexedDB', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('[]', { status: 200 })));
    const { api } = await import('./api');
    await api.tasks.listAll();
    await api.tasks.getFeatures('s1');
    const urlKeys = mocks.idbSet.mock.calls.filter(c => String((c as unknown[])[0]).startsWith('/'));
    expect(urlKeys).toEqual([]);
  });

  it('search, listByPersona, listByProject и getFeature не пишут URL-ключ в IndexedDB', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('[]', { status: 200 })));
    const { api } = await import('./api');
    await api.tasks.search('abc');
    await api.tasks.listByPersona('p1');
    await api.tasks.listByProject('pr1');
    await api.tasks.getFeature('f1', '50');
    await api.tasks.getFeature('f1', undefined, new AbortController().signal);
    const urlKeys = mocks.idbSet.mock.calls.filter(c => String((c as unknown[])[0]).startsWith('/'));
    expect(urlKeys).toEqual([]);
  });

  it('featuresIndex остаётся пассивным: ответ пишется в кэш по URL', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('{}', { status: 200 })));
    const { api } = await import('./api');
    await api.tasks.featuresIndex();
    const urlKeys = mocks.idbSet.mock.calls.filter(c => String((c as unknown[])[0]).startsWith('/'));
    expect(urlKeys.length).toBe(1);
  });

  it('открытие N задач подряд не оставляет в IndexedDB записей /tasks/{id}', async () => {
    const full = (id: string) => ({ ...task(id), description: 'x'.repeat(1000), resultMarkdown: 'y'.repeat(1000) });
    vi.stubGlobal('fetch', vi.fn(async (u: unknown) => {
      const id = String(u).split('/').pop()!;
      return new Response(JSON.stringify(full(id)), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }));
    const { api } = await import('./api');
    const N = 40;
    for (let i = 0; i < N; i++) await api.tasks.get(`t${i}`);
    const taskKeys = mocks.idbSet.mock.calls.filter(c => /^\/(api\/)?tasks\/[^/?]+$/.test(String((c as unknown[])[0])));
    expect(taskKeys).toEqual([]);
  });

  it('офлайн api.tasks.get бросает OfflineError, а не отдаёт устаревшую копию', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw new TypeError('Failed to fetch'); }));
    mocks.idbGet.mockResolvedValue({ data: task('t1'), savedAt: 1 });
    const { api } = await import('./api');
    const { OfflineError } = await import('./offline');
    await expect(api.tasks.get('t1')).rejects.toBeInstanceOf(OfflineError);
  });
});

describe('reloadTasks офлайн', () => {
  it('listAll бросает OfflineError → стор из tasks:all, исключения нет', async () => {
    vi.doMock('./api', async () => {
      const { OfflineError } = await import('./offline');
      return { api: { tasks: { listAll: vi.fn(async () => { throw new OfflineError(); }) } } };
    });
    mocks.idbGet.mockImplementation(async () => ({ data: [task('a'), task('b')], savedAt: 1 }));
    const m = await import('./tasks');
    await expect(m.ensureTasksLoaded()).resolves.toBeUndefined();
    expect(m.getTaskById('a')?.id).toBe('a');
    expect(m.getTaskById('b')?.id).toBe('b');
    vi.doUnmock('./api');
  });
});

describe('чистка старого пассивного кэша', () => {
  it('после инициализации записей /tasks, /tasks?…, /tasks/features… не остаётся', async () => {
    const keys = ['/tasks', '/tasks?from=1', '/api/tasks?q=x', '/tasks/features', '/tasks/features?sessionId=s1',
      '/tasks/features/index', '/tasks/t1', 'tasks:all', '/projects/p/tasks'];
    const store = new Set(keys);
    mocks.idbKeys.mockImplementation(async () => [...store]);
    mocks.idbDelete.mockImplementation(async (k: unknown) => { store.delete(k as string); return undefined; });
    vi.doMock('./api', () => ({ api: { tasks: { listAll: vi.fn(async () => []) } } }));
    const m = await import('./tasks');
    await m.ensureTasksLoaded();
    await vi.waitFor(() => expect(mocks.idbDelete).toHaveBeenCalled());
    await new Promise(r => setTimeout(r, 0));
    expect([...store].filter(k => k.startsWith('/tasks/features') && k !== '/tasks/features/index')).toEqual([]);
    expect([...store].filter(k => k === '/tasks' || k.startsWith('/tasks?') || k.startsWith('/api/tasks'))).toEqual([]);
    // /tasks/t1 (одиночная задача) тоже сносится — FIX-3; сам признак «чистили» не в счёт
    expect([...store].filter(k => !k.startsWith('tasks:legacy')).sort())
      .toEqual(['/projects/p/tasks', '/tasks/features/index', 'tasks:all']);
    vi.doUnmock('./api');
  });
});
