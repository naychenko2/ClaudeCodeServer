import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// Пассивный GET-кэш offline.ts: ключ с владельцем, вытеснение по usedAt, троттлинг, эпоха сессии.
// idb заменён Map-хранилищем с теми же диапазонными операциями, что у настоящего: диапазон,
// который offline.ts передаёт в стор, — предмет проверки (мутация «весь стор» должна краснеть).

const fake = vi.hoisted(() => {
  const store = new Map<string, { data: unknown; savedAt: number; usedAt?: number; bytes?: number }>();
  const calls = { count: 0, cursor: 0, set: 0 };
  const inRange = (k: string, lo: string, hi: string) => k >= lo && k <= hi;
  return {
    store, calls,
    idbGet: async (k: string) => store.get(k),
    idbSet: async (k: string, v: { data: unknown; savedAt: number; usedAt?: number; bytes?: number }) => { calls.set++; store.set(k, v); return k; },
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

function storageStub(init: Record<string, string> = {}) {
  const m = new Map(Object.entries(init));
  return {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => { m.set(k, v); },
    removeItem: (k: string) => { m.delete(k); },
  };
}
const json = (data: unknown) => ({ ok: true, status: 200, statusText: 'OK', json: async () => data, text: async () => JSON.stringify(data) });
const own = (owner: string) => [...fake.store.keys()].filter(k => k.startsWith(`offline:${owner}:`));

let offline: typeof import('../offline');
let fetchMock: ReturnType<typeof vi.fn>;
let ls: ReturnType<typeof storageStub>;

const login = (id: string) => { ls.setItem('cc_token', 't'); ls.setItem('cc_user_id', id); };
const flush = () => vi.advanceTimersByTimeAsync(0);
async function get(url: string) {
  fetchMock.mockResolvedValueOnce(json({ url }));
  const r = await offline.request(url);
  await flush();
  return r;
}
async function getOffline(url: string) {
  fetchMock.mockRejectedValue(new TypeError('failed'));
  const p = offline.request(url);
  const settled = p.then(v => ({ v }), (e: unknown) => ({ e }));
  await vi.advanceTimersByTimeAsync(15_000);
  const res = await settled;
  await flush();
  fetchMock.mockReset();
  return res;
}
const seedOwn = (owner: string, n: number, mk: (i: number) => { savedAt: number; usedAt?: number }) => {
  for (let i = 0; i < n; i++) fake.store.set(`offline:${owner}:/p/${i}`, { data: { i }, ...mk(i) });
};

beforeEach(async () => {
  vi.resetModules();
  vi.useFakeTimers();
  fake.store.clear();
  fake.calls.count = 0; fake.calls.cursor = 0; fake.calls.set = 0;
  fetchMock = vi.fn();
  vi.stubGlobal('fetch', fetchMock);
  vi.stubGlobal('navigator', { onLine: true });
  vi.stubGlobal('window', { addEventListener: vi.fn(), dispatchEvent: () => true });
  ls = storageStub();
  vi.stubGlobal('localStorage', ls);
  vi.stubGlobal('sessionStorage', storageStub());
  login('A');
  offline = await import('../offline');
});
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('ключ с владельцем', () => {
  it('пишет под offline:<owner>:<url> и читает только по своему владельцу', async () => {
    await get('/things');
    expect([...fake.store.keys()]).toEqual(['offline:A:/things']);
    expect(await getOffline('/things')).toEqual({ v: { url: '/things' } });
    login('B');
    expect(await getOffline('/things')).toMatchObject({ e: expect.any(offline.OfflineError) });
  });

  it('без владельца пассивный кэш не читается и не пишется', async () => {
    ls.removeItem('cc_user_id');
    await get('/things');
    expect(fake.store.size).toBe(0);
    fake.store.set('offline:A:/things', { data: 1, savedAt: 1 });
    expect(await getOffline('/things')).toMatchObject({ e: expect.any(offline.OfflineError) });
  });

  it('ключ без префикса не отдаётся при чтении (без отката на старый ключ)', async () => {
    fake.store.set('/things', { data: { old: true }, savedAt: 1 });
    expect(await getOffline('/things')).toMatchObject({ e: expect.any(offline.OfflineError) });
  });

  it('sync-класс (листинги файлов, индекс фич, список чатов) живёт под ключом с владельцем и читается офлайн как раньше', async () => {
    await get('/projects/p1/files?path=a');
    await get('/tasks/features/index?projectId=p1');
    await get('/projects/p1/sessions');
    expect([...fake.store.keys()].sort()).toEqual([
      'offline:A:/projects/p1/files?path=a',
      'offline:A:/projects/p1/sessions',
      'offline:A:/tasks/features/index?projectId=p1',
    ]);
    expect(await getOffline('/projects/p1/files?path=a')).toEqual({ v: { url: '/projects/p1/files?path=a' } });
  });

  it('ответ, пришедший после выхода или смены аккаунта, в кэш не ложится', async () => {
    let resolve!: (v: unknown) => void;
    fetchMock.mockReturnValueOnce(new Promise(r => { resolve = r; }));
    const p = offline.request('/slow');
    ls.removeItem('cc_user_id'); ls.removeItem('cc_token');
    resolve(json({ late: true }));
    await p; await flush();
    expect(fake.store.size).toBe(0);

    login('A');
    let resolve2!: (v: unknown) => void;
    fetchMock.mockReturnValueOnce(new Promise(r => { resolve2 = r; }));
    const p2 = offline.request('/slow2');
    login('B');
    resolve2(json({ late: true }));
    await p2; await flush();
    expect(fake.store.size).toBe(0);
  });

  it('вход другого аккаунта сносит чужие offline:*', async () => {
    seedOwn('A', 3, i => ({ savedAt: i + 1 }));
    seedOwn('B', 2, i => ({ savedAt: i + 1 }));
    login('B');
    await offline.runPassiveMaintenance();
    expect(own('A')).toHaveLength(0);
    expect(own('B')).toHaveLength(2);
  });

  it('первая запись после смены владельца сама запускает проход со сносом чужого', async () => {
    seedOwn('A', 3, i => ({ savedAt: i + 1 }));
    login('B');
    await get('/x');
    await flush();
    expect(own('A')).toHaveLength(0);
    expect(own('B')).toEqual(['offline:B:/x']);
  });
});

describe('вытеснение', () => {
  const NEIGHBORS: Record<string, unknown> = {
    'tasks:all': { data: [{ id: 't1' }], savedAt: 5 },
    'tasks:legacy-cache-purged:v3': { data: true, savedAt: 6 },
    'notes:localKey-index': { data: { a: 'b' }, savedAt: 7 },
    'notes-mtimes': { data: { n: '1' }, savedAt: 8 },
    'synced-mtimes:p1': { data: { f: '1' }, savedAt: 9 },
    'synced-chats:p1': { data: { c: '1' }, savedAt: 10 },
    'chat:continue:s1': { data: { c: '2' }, savedAt: 11 },
    'offline:B:/чужой': { data: 1, savedAt: 1 },
  };
  // Осиротевшие голые ключи sync-класса: после смены контракта их больше не читают
  const ORPHANED = [
    '/tasks/features/index?projectId=p1',
    '/projects/p1/files?path=a',
    '/projects/p1/files/content?path=a%2Fb.txt',
    '/projects/p1/sessions',
  ];

  it('соседи в «meta» побайтно не меняются, свои записи ужимаются ровно до 180', async () => {
    for (const [k, v] of Object.entries(NEIGHBORS)) fake.store.set(k, structuredClone(v) as never);
    for (const k of ORPHANED) fake.store.set(k, { data: 1, savedAt: 12 });
    seedOwn('A', 250, i => ({ savedAt: 1000 + i }));
    await offline.runPassiveMaintenance();
    expect(own('A')).toHaveLength(180);
    // вытеснены самые давние, остались самые свежие
    expect(fake.store.has('offline:A:/p/249')).toBe(true);
    expect(fake.store.has('offline:A:/p/0')).toBe(false);
    for (const [k, v] of Object.entries(NEIGHBORS)) {
      if (k === 'offline:B:/чужой') continue; // чужой владелец сносится отдельным правилом
      expect(JSON.stringify(fake.store.get(k))).toBe(JSON.stringify(v));
    }
    // осиротевшие голые ключи sync-класса недостижимы для чтения — сносятся
    for (const k of ORPHANED) expect(fake.store.has(k)).toBe(false);
  });

  it('LRU по доступу: давно записанная, но недавно прочитанная выживает; свежая, но не читанная — уходит', async () => {
    seedOwn('A', 201, i => ({ savedAt: 6000 + i }));
    fake.store.set('offline:A:/p/read', { data: 'r', savedAt: 1, usedAt: 99_999 });
    fake.store.set('offline:A:/p/unread', { data: 'u', savedAt: 5000 });
    await offline.runPassiveMaintenance();
    expect(fake.store.has('offline:A:/p/read')).toBe(true);
    expect(fake.store.has('offline:A:/p/unread')).toBe(false);
    expect(own('A')).toHaveLength(180);
  });

  it('у записей без usedAt (старые) порядок берётся из savedAt', async () => {
    seedOwn('A', 201, i => ({ savedAt: 100 + i }));
    await offline.runPassiveMaintenance();
    expect(fake.store.has('offline:A:/p/0')).toBe(false);
    expect(fake.store.has('offline:A:/p/200')).toBe(true);
  });

  it('запас снизу: после прохода ровно 180, а 200 записей проход не запускает', async () => {
    seedOwn('A', 300, i => ({ savedAt: i + 1 }));
    await offline.runPassiveMaintenance();
    expect(own('A')).toHaveLength(180);
    for (let i = 0; i < 20; i++) fake.store.set(`offline:A:/n/${i}`, { data: i, savedAt: 1 });
    expect(own('A')).toHaveLength(200);
    await offline.runPassiveMaintenance();
    // свои 200 записей не ужимаются (потолок не превышен)
    expect(own('A')).toHaveLength(200);
  });

  it('50 GET подряд не дают больше одного прохода (вызовов count)', async () => {
    for (let i = 0; i < 50; i++) await get(`/g/${i}`);
    expect(fake.calls.count).toBeLessThanOrEqual(1);
    expect(own('A')).toHaveLength(50);
  });

  it('по истечении минуты следующая запись запускает ровно один проход', async () => {
    await get('/first');
    const before = fake.calls.count;
    await vi.advanceTimersByTimeAsync(61_000);
    await get('/second');
    await get('/third');
    expect(fake.calls.count).toBe(before + 1);
  });
});

describe('usedAt при чтении', () => {
  it('обновляет usedAt, savedAt не трогает; повторно — не чаще раза в 5 минут', async () => {
    fake.store.set('offline:A:/r', { data: { r: 1 }, savedAt: 123 });
    await getOffline('/r');
    const first = fake.store.get('offline:A:/r')!;
    expect(first.savedAt).toBe(123);
    expect(first.usedAt).toBeGreaterThan(123);
    const writes = fake.calls.set;
    await vi.advanceTimersByTimeAsync(60_000);
    await getOffline('/r');
    expect(fake.calls.set).toBe(writes);
    await vi.advanceTimersByTimeAsync(5 * 60_000);
    await getOffline('/r');
    expect(fake.calls.set).toBe(writes + 1);
    expect(fake.store.get('offline:A:/r')!.savedAt).toBe(123);
  });
});

describe('класс содержимого файлов', () => {
  it('префикс содержимого — свой диапазон под владельцем (sync.ts чистит и читает по нему)', () => {
    expect(offline.fileContentCachePrefix('p1')).toBe('offlinefile:A:p1:');
    ls.removeItem('cc_user_id');
    expect(offline.fileContentCachePrefix('p1')).toBeNull();
  });

  it('пишется и читается в своём классе; чужой владелец не читает и не пишет', async () => {
    const url = '/projects/p1/files/content?path=a.txt';
    await get(url);
    expect([...fake.store.keys()]).toEqual(['offlinefile:A:p1:f::a.txt']);
    expect(await getOffline(url)).toEqual({ v: { url } });

    // Чужой владелец: чужую запись не читает
    login('B');
    expect(await getOffline(url)).toMatchObject({ e: expect.any(offline.OfflineError) });
    // …и пишет под своим ключом (запись прежнего владельца сносится проходом обслуживания)
    await get(url);
    await offline.runPassiveMaintenance();
    expect(fake.store.has('offlinefile:B:p1:f::a.txt')).toBe(true);
    expect(fake.store.has('offlinefile:A:p1:f::a.txt')).toBe(false);
  });

  it('N просмотренных файлов не занимают место в общем LRU — у них свой класс', async () => {
    const N = 250;
    for (let i = 0; i < N; i++) {
      await get(`/projects/p1/files/content?path=f${i}.txt`);
      await vi.advanceTimersByTimeAsync(1); // разные метки времени → LRU проверяем по доступу
    }
    await offline.runPassiveMaintenance();

    // общий класс пуст: содержимое живёт в своём диапазоне
    expect(own('A')).toHaveLength(0);
    const content = [...fake.store.keys()].filter(k => k.startsWith('offlinefile:A:p1:'));
    expect(content).toHaveLength(N);
  });

  it('старое содержимое из общего класса (прежний контракт) сносится проходом', async () => {
    fake.store.set('offline:A:/projects/p1/files/content?path=a.txt', { data: { old: true }, savedAt: 1 });
    await offline.runPassiveMaintenance();
    expect(fake.store.has('offline:A:/projects/p1/files/content?path=a.txt')).toBe(false);
  });
});

describe('старые записи без префикса', () => {
  const KEEP = [
    'tasks:all', 'tasks:legacy-cache-purged:v3', 'notes:localKey-index', 'notes-mtimes', 'synced-mtimes:p1',
    'synced-chats:p1', 'chat:continue:s1',
  ];
  const OLD = [
    '/tasks', '/tasks?q=1', '/api/tasks/abc', '/tasks/features/f1?eventsLimit=50', '/projects/p1/tasks',
    // sync-класс: осиротел после перехода под префикс владельца
    '/projects/p1/files?path=', '/projects/p1/files/content?path=a', '/projects/p1/sessions', '/tasks/features/index',
    // прочие потребители пассивного кэша
    '/notifications?limit=50&offset=0', '/ai/caps', '/auth/cache-thresholds', '/tts/diag',
    '/projects/p1/device-agent/ticket', '/sessions/s1/context',
  ];

  it('сносятся на проходе; повторный проход идемпотентен; «дописанные» сносятся на следующем', async () => {
    for (const k of [...KEEP, ...OLD]) fake.store.set(k, { data: 1, savedAt: 1 });
    await offline.runPassiveMaintenance();
    expect([...fake.store.keys()].sort()).toEqual([...KEEP].sort());
    await offline.runPassiveMaintenance();
    expect([...fake.store.keys()].sort()).toEqual([...KEEP].sort());
    fake.store.set('/tasks/zzz', { data: 1, savedAt: 1 }); // вкладка со старым бандлом дописала
    await offline.runPassiveMaintenance();
    expect(fake.store.has('/tasks/zzz')).toBe(false);
    expect([...fake.store.keys()].sort()).toEqual([...KEEP].sort());
  });

  it('проход запускается и на старте приложения (initConnectivity)', async () => {
    fake.store.set('/tasks/abc', { data: 1, savedAt: 1 });
    offline.initConnectivity();
    await flush();
    expect(fake.store.has('/tasks/abc')).toBe(false);
  });
});
