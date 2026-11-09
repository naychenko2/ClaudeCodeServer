import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// Разовая чистка накопленных записей пассивного GET-кэша: ключи /tasks/{id}, списки и фичи.

const mocks = vi.hoisted(() => {
  const store = new Map<string, unknown>();
  return {
    store,
    idbGet: vi.fn(async (k: string) => store.get(k)),
    idbSet: vi.fn(async (k: string, v: unknown) => { store.set(k, v); return 1 as unknown; }),
    idbKeys: vi.fn(async () => [...store.keys()]),
    idbDelete: vi.fn(async (k: string) => { store.delete(k); }),
  };
});

vi.mock('./idb', () => ({
  idbGet: mocks.idbGet, idbSet: mocks.idbSet, idbKeys: mocks.idbKeys, idbDelete: mocks.idbDelete,
  outboxAll: vi.fn(async () => []), outboxGet: vi.fn(async () => undefined),
  outboxPut: vi.fn(async () => {}), outboxDelete: vi.fn(async () => {}),
}));
vi.mock('./signalr', () => ({
  joinUser: vi.fn(async () => {}), onMessage: vi.fn(), onReconnected: vi.fn(),
}));
vi.mock('./themeMode', () => ({ getEffectiveTheme: () => 'light' }));

const entry = { data: { big: 'x' }, savedAt: 1 };
const seed = () => {
  for (const k of [
    '/tasks/abc', '/api/tasks/def', '/tasks/ghi?foo=1', '/tasks', '/api/tasks/features?sessionId=s1',
    '/notes/n1', '/projects', '/tasks/ai/classify', 'tasks:all',
  ]) mocks.store.set(k, entry);
};

beforeEach(() => {
  vi.resetModules();
  mocks.store.clear();
  Object.values(mocks).forEach(m => { if (typeof m === 'function') (m as ReturnType<typeof vi.fn>).mockClear(); });
  vi.stubGlobal('document', Object.assign(new EventTarget(), { visibilityState: 'visible' }));
  vi.stubGlobal('window', new EventTarget());
});
afterEach(() => { vi.unstubAllGlobals(); });

const NEW_TEMPLATES = [
  '/tasks?q=abc', '/tasks?personaId=p1', '/api/tasks?personaId=p1', '/projects/p1/tasks',
  '/api/projects/p1/tasks?x=1', '/tasks/features/f1', '/tasks/features/f1?eventsLimit=50',
  '/api/tasks/features/f1?eventsLimit=50',
  // sync-класс: осиротел после перехода под префикс владельца
  '/projects/p1/files?path=', '/projects/p1/files/content?path=a', '/projects/p1/sessions',
  '/tasks/features/index?projectId=p1',
  // прочие потребители пассивного кэша
  '/notifications?limit=50&offset=0', '/ai/caps', '/auth/cache-thresholds', '/tts/diag',
  '/projects/p1/device-agent/ticket', '/sessions/s1/context',
];
const KEEP = ['tasks:all', 'tasks:outbox:1', 'chat:continue:s1'];

describe('purgeLegacyTaskListCache — новые шаблоны', () => {
  it('сносит поиск, персону, задачи проекта, страницу фичи и осиротевшие голые ключи; остальное остаётся', async () => {
    for (const k of [...NEW_TEMPLATES, ...KEEP]) mocks.store.set(k, entry);
    const { purgeLegacyTaskListCache } = await import('./tasks');
    await purgeLegacyTaskListCache();
    const left = [...mocks.store.keys()].filter(k => !k.startsWith('tasks:legacy')).sort();
    expect(left).toEqual([...KEEP].sort());
  });

  it('при уже стоящем признаке v2 чистка v3 всё равно выполняется', async () => {
    mocks.store.set('tasks:legacy-cache-purged:v2', { data: true, savedAt: 1 });
    for (const k of [...NEW_TEMPLATES, ...KEEP]) mocks.store.set(k, entry);
    const { purgeLegacyTaskListCache } = await import('./tasks');
    await purgeLegacyTaskListCache();
    expect(NEW_TEMPLATES.some(k => mocks.store.has(k))).toBe(false);
    expect(mocks.store.has('tasks:legacy-cache-purged:v3')).toBe(true);
  });
});

describe('purgeLegacyTaskListCache — ключи одиночных задач', () => {
  it('сносит /tasks/{id} (с /api и query) и прочие голые URL-ключи; служебные остаются', async () => {
    seed();
    const { purgeLegacyTaskListCache } = await import('./tasks');
    await purgeLegacyTaskListCache();
    const left = [...mocks.store.keys()].filter(k => !k.startsWith('tasks:legacy')).sort();
    expect(left).toEqual(['tasks:all']);
  });

  it('разовая: после первого прогона повторные гидрации ключи не разбирают', async () => {
    seed();
    const { purgeLegacyTaskListCache } = await import('./tasks');
    await purgeLegacyTaskListCache();
    expect(mocks.idbKeys).toHaveBeenCalledTimes(1);
    mocks.store.set('/tasks/new', entry);
    await purgeLegacyTaskListCache();
    expect(mocks.idbKeys).toHaveBeenCalledTimes(1);
    expect(mocks.store.has('/tasks/new')).toBe(true);
  });

  it('при сбое чтения ключей признак не ставится — чистка повторится', async () => {
    mocks.idbKeys.mockRejectedValueOnce(new Error('idb недоступна'));
    const { purgeLegacyTaskListCache } = await import('./tasks');
    await purgeLegacyTaskListCache();
    expect([...mocks.store.keys()].some(k => k.startsWith('tasks:legacy'))).toBe(false);
  });
});
