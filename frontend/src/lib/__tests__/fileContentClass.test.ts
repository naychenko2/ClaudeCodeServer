import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// F2c-d, пункт 2: содержимое файлов — отдельный класс с бюджетом ПО ВЕСУ.
//
// Проверяются три свойства:
//   1) содержимое лежит в своём диапазоне ('offlinefile:'), а не в общем LRU;
//   2) помеченное к синхронизации содержимое не вытесняется содержимым НЕпомеченных
//      файлов (у непомеченного — свой небольшой бюджет, и вытесняется оно первым);
//   3) невытесненное помеченное содержимое читается офлайн как раньше.

const fake = vi.hoisted(() => {
  const store = new Map<string, { data: unknown; savedAt: number; usedAt?: number; bytes?: number }>();
  const inRange = (k: string, lo: string, hi: string) => k >= lo && k <= hi;
  return {
    store,
    idbGet: async (k: string) => store.get(k),
    idbSet: async (k: string, v: { data: unknown; savedAt: number; usedAt?: number; bytes?: number }) => { store.set(k, v); return k; },
    idbDelete: async (k: string) => { store.delete(k); return undefined; },
    idbKeys: async () => [...store.keys()],
    idbCountRange: async (lo: string, hi: string) => [...store.keys()].filter(k => inRange(k, lo, hi)).length,
    idbRangeStamps: async (lo: string, hi: string) => [...store.entries()].filter(([k]) => inRange(k, lo, hi))
      .map(([key, v]) => ({ key, savedAt: v.savedAt, usedAt: v.usedAt, bytes: v.bytes })),
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

const jsonResponse = (data: unknown) => ({
  ok: true, status: 200, statusText: 'OK',
  json: async () => data,
  text: async () => JSON.stringify(data),
  headers: new Headers(),
});

const KEEP = 'offlinefile:A:p1:k::';
const FREE = 'offlinefile:A:p1:f::';
const contentUrl = (path: string) => `/projects/p1/files/content?path=${encodeURIComponent(path)}`;

let offline: typeof import('../offline');
let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(async () => {
  vi.resetModules();
  fake.store.clear();
  fetchMock = vi.fn(async () => jsonResponse({ content: 'данные' }));
  vi.stubGlobal('fetch', fetchMock);
  vi.stubGlobal('navigator', { onLine: true });
  vi.stubGlobal('window', { addEventListener: vi.fn(), dispatchEvent: () => true });
  vi.stubGlobal('localStorage', storageStub({ cc_token: 't', cc_user_id: 'A' }));
  vi.stubGlobal('sessionStorage', storageStub());
  offline = await import('../offline');
});
afterEach(() => { vi.unstubAllGlobals(); });

async function fetchContent(path: string) {
  fetchMock.mockResolvedValueOnce(jsonResponse({ content: 'данные' }));
  await offline.request(contentUrl(path));
}

// Записать содержимое напрямую в класс (обход сети): нужно для точного веса.
async function putContent(path: string, keep: boolean, bytes: number, savedAt: number) {
  const key = `${keep ? KEEP : FREE}${encodeURIComponent(path)}`;
  await fake.idbSet(key, { data: { content: path }, savedAt, usedAt: savedAt, bytes });
}

describe('класс содержимого файлов', () => {
  it('содержимое лежит в своём диапазоне, а не в общем LRU', async () => {
    await fetchContent('a.txt');
    expect([...fake.store.keys()]).toEqual([`${FREE}a.txt`]);
    expect([...fake.store.keys()].filter(k => k.startsWith('offline:A:'))).toHaveLength(0);
  });

  it('помеченный файл кладётся под «k», непомеченный — под «f»', async () => {
    offline.setKeptFilePaths('p1', ['keep-x.txt']);
    await fetchContent('keep-x.txt');
    await fetchContent('free-y.txt');
    expect(fake.store.has(`${KEEP}keep-x.txt`)).toBe(true);
    expect(fake.store.has(`${FREE}keep-x.txt`)).toBe(false);
    expect(fake.store.has(`${FREE}free-y.txt`)).toBe(true);
  });

  it('помеченное содержимое не вытесняется содержимым непомеченных файлов', async () => {
    // Помеченных — 20 файлов по 1000 байт, непомеченных — 100 файлов по 1000 байт.
    // Бюджет непомеченного — 10 файлов, помеченного — 100 файлов.
    for (let i = 0; i < 20; i++) await putContent(`keep-${i}.txt`, true, 1000, 1000 + i);
    for (let i = 0; i < 100; i++) await putContent(`free-${i}.txt`, false, 1000, 2000 + i);
    offline.setFileClassBudget({ maxBytes: 100_000, freeMaxBytes: 10_000, maxRecords: 100_000 });

    await offline.runPassiveMaintenance();

    // Все помеченные на месте — непомеченные их не вытеснили
    for (let i = 0; i < 20; i++) {
      expect(fake.store.has(`${KEEP}keep-${i}.txt`)).toBe(true);
    }
    // Непомеченные ужаты до своего бюджета
    const free = [...fake.store.keys()].filter(k => k.startsWith(FREE));
    expect(free.length).toBeLessThan(100);
    expect(free.length).toBeGreaterThan(0);
  });

  it('вытесняется самое старое содержимое', async () => {
    for (let i = 0; i < 30; i++) await putContent(`free-${i}.txt`, false, 1000, 1000 + i);
    offline.setFileClassBudget({ maxBytes: 100_000, freeMaxBytes: 10_000, maxRecords: 100_000 });

    await offline.runPassiveMaintenance();

    expect(fake.store.has(`${FREE}free-0.txt`)).toBe(false);
    expect(fake.store.has(`${FREE}free-29.txt`)).toBe(true);
  });

  it('невытесненное помеченное содержимое читается офлайн как раньше', async () => {
    offline.setKeptFilePaths('p1', ['keep-x.txt']);
    await fetchContent('keep-x.txt');
    await fetchContent('free-y.txt');
    // Помеченная запись лежит под 'k' — переписываем её содержимое напрямую,
    // чтобы проверить чтение офлайн именно помеченной записи.
    await putContent('keep-x.txt', true, 100, 1);
    await fake.idbDelete(`${FREE}keep-x.txt`);

    // Сеть отвалилась — ответ берётся из кэша
    fetchMock.mockRejectedValue(new TypeError('failed'));
    const res = await offline.request(contentUrl('keep-x.txt')).catch(e => e);
    expect(res).toEqual({ content: 'keep-x.txt' });
  });

  it('помеченное содержимое читается офлайн, а непомеченное — по своей записи', async () => {
    offline.setKeptFilePaths('p1', ['keep-x.txt']);
    await fetchContent('keep-x.txt');
    await fetchContent('free-y.txt');
    await fake.idbDelete(`${FREE}keep-x.txt`);

    fetchMock.mockRejectedValue(new TypeError('failed'));
    expect(await offline.request(contentUrl('keep-x.txt'))).toEqual({ content: 'данные' });
    expect(await offline.request(contentUrl('free-y.txt'))).toEqual({ content: 'данные' });
  });
});
