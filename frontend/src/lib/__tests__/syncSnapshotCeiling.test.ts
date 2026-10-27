import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// F2c-f: потолок пассивного кэша на пути записи МИМО счётчика.
//
// Проблема (замер QA3): потолок 200 записей на владельца ограничивал
// установившееся состояние, а не пик. Обслуживание тикало от записей, прошедших
// через request(), а листинги дерева писал снапшот прямо в IDB — мимо счётчика.
// Сразу после авто-снапшота диапазон владельца стоял на 957 записей (залп
// ~700–790 листингов по 55 проектам), после прохода обслуживания — ровно 180.
//
// Проба: снапшот пишет листинги МИМО request() (как это делает боевой код) —
// после прогона диапазон владельца не превышает потолок, и два снапшота подряд
// не растят его сверх потолка.
//
// Мутация: снять учёт листингов в счётчике диапазона (writePassiveRangeEntry
// перестаёт звать ensureRangeBudget) → целевой тест краснеет.
//
// idb заменён Map-хранилищем с теми же диапазонными операциями, что у настоящего:
// диапазон, который offline.ts передаёт в стор, — часть проверки.

const fake = vi.hoisted(() => {
  const store = new Map<string, { data: unknown; savedAt: number; usedAt?: number; bytes?: number }>();
  const calls = { count: 0, cursor: 0 };
  const inRange = (k: string, lo: string, hi: string) => k >= lo && k <= hi;
  return {
    store, calls,
    idbGet: async (k: string) => store.get(k),
    idbSet: async (k: string, v: { data: unknown; savedAt: number; usedAt?: number; bytes?: number }) => { store.set(k, v); return k; },
    idbDelete: async (k: string) => { store.delete(k); return undefined; },
    idbKeys: async () => [...store.keys()],
    idbCountRange: async (lo: string, hi: string) => { calls.count++; return [...store.keys()].filter(k => inRange(k, lo, hi)).length; },
    idbRangeStamps: async (lo: string, hi: string) => {
      calls.cursor++;
      return [...store.entries()].filter(([k]) => inRange(k, lo, hi))
        .map(([key, v]) => ({ key, savedAt: v.savedAt, usedAt: v.usedAt, bytes: v.bytes }));
    },
  };
});
vi.mock('../idb', () => ({
  idbGet: fake.idbGet, idbSet: fake.idbSet, idbDelete: fake.idbDelete, idbKeys: fake.idbKeys,
  idbCountRange: fake.idbCountRange, idbRangeStamps: fake.idbRangeStamps,
}));

const apiMock = vi.hoisted(() => ({
  listAll: vi.fn(async () => []),
  projectsList: vi.fn(async () => [] as { id: string }[]),
  sessionsList: vi.fn(async () => []),
  syncList: vi.fn(async () => []),
  tree: vi.fn(async (_projectId: string) => [] as unknown[]),
  getContent: vi.fn(async () => ({ content: 'x' })),
  offline: null as null | typeof import('../offline'),
}));
vi.mock('../api', () => ({
  api: {
    tasks: { listAll: apiMock.listAll },
    projects: { list: apiMock.projectsList },
    sessions: { list: apiMock.sessionsList, getHistory: vi.fn(async () => []) },
    sync: { list: apiMock.syncList },
    files: { tree: apiMock.tree, getContent: apiMock.getContent },
  },
}));
vi.mock('../subsystems', () => ({ isSubsystemEnabled: () => false }));
vi.mock('../notesOffline', () => ({ warmNote: vi.fn(), drainNotesOutbox: vi.fn() }));
vi.mock('../taskOutbox', () => ({ drainTaskOutbox: vi.fn() }));
vi.mock('../tasks', () => ({}));

function storageStub(init: Record<string, string> = {}) {
  const m = new Map(Object.entries(init));
  return {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => { m.set(k, v); },
    removeItem: (k: string) => { m.delete(k); },
  };
}

const PROJECTS = ['p1', 'p2', 'p3'];
// Папок в проекте: листингов на проект = 1 (корень) + DIRS
const DIRS = 80;

const treeFor = () => {
  const entries: unknown[] = [];
  for (let i = 0; i < DIRS; i++) {
    entries.push({ name: `d${i}`, path: `d${i}`, isDirectory: true, modified: 'M1', isModified: false });
    entries.push({ name: `f${i}.txt`, path: `d${i}/f${i}.txt`, isDirectory: false, modified: 'M1', isModified: false });
  }
  return entries;
};

// Записи общего класса пассивного кэша владельца A (листинги дерева лежат тут)
const ownKeys = () => [...fake.store.keys()].filter(k => k.startsWith('offline:A:'));

let offline: typeof import('../offline');
let sync: typeof import('../sync');
let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(async () => {
  vi.resetModules();
  fake.store.clear();
  fake.calls.count = 0; fake.calls.cursor = 0;
  apiMock.tree.mockImplementation(async () => treeFor());
  apiMock.projectsList.mockResolvedValue(PROJECTS.map(id => ({ id })));
  fetchMock = vi.fn(async () => ({
    ok: true, status: 200, statusText: 'OK',
    json: async () => ({}), text: async () => '{}', headers: new Headers(),
  }));
  vi.stubGlobal('fetch', fetchMock);
  vi.stubGlobal('navigator', { onLine: true });
  vi.stubGlobal('window', { addEventListener: vi.fn(), dispatchEvent: () => true });
  vi.stubGlobal('localStorage', storageStub({ cc_token: 't', cc_user_id: 'A' }));
  vi.stubGlobal('sessionStorage', storageStub());
  offline = await import('../offline');
  apiMock.offline = offline;
  sync = await import('../sync');
});
afterEach(() => { vi.unstubAllGlobals(); });

describe('потолок пассивного кэша на пути записи мимо счётчика', () => {
  it('запись мимо request() не обходит потолок: диапазон владельца не выше потолка', async () => {
    const listings = PROJECTS.length * (DIRS + 1);
    expect(listings).toBeGreaterThan(offline.PASSIVE_CACHE_MAX); // залп заведомо больше потолка

    await sync.runOfflineSnapshot();

    // Листинги писались мимо request() — потолок обязан держать и их
    expect(ownKeys().length).toBeLessThanOrEqual(offline.PASSIVE_CACHE_MAX);
  });

  it('два снапшота подряд не растят диапазон сверх потолка', async () => {
    await sync.runOfflineSnapshot();
    const afterFirst = ownKeys().length;
    expect(afterFirst).toBeLessThanOrEqual(offline.PASSIVE_CACHE_MAX);

    await sync.runOfflineSnapshot();
    expect(ownKeys().length).toBeLessThanOrEqual(offline.PASSIVE_CACHE_MAX);
  });

  it('параллельные записи request() тоже не обходят потолок', async () => {
    await Promise.all(Array.from({ length: 300 }, (_, i) => offline.request(`/g/${i}`)));
    expect(ownKeys().length).toBeLessThanOrEqual(offline.PASSIVE_CACHE_MAX);
  });

  it('офлайн-дерево продолжает работать: листинги на месте и читаются', async () => {
    await sync.runOfflineSnapshot();

    // Свежий проект (записан последним) — его листинги уцелели
    const last = PROJECTS[PROJECTS.length - 1];
    const rootKey = `offline:A:/projects/${last}/files?path=`;
    expect(fake.store.has(rootKey)).toBe(true);
    const entries = fake.store.get(rootKey)!.data as { path: string; isDirectory: boolean }[];
    expect(Array.isArray(entries)).toBe(true);
    expect(entries.length).toBe(DIRS); // в корне — папки проекта

    // Листинг папки тоже на месте и читается
    const dirKey = `offline:A:/projects/${last}/files?path=d0`;
    expect(fake.store.has(dirKey)).toBe(true);
    expect((fake.store.get(dirKey)!.data as { path: string }[])[0].path).toBe('d0/f0.txt');
  });
});
