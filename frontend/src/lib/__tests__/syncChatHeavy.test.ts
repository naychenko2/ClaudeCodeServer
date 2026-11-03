import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// F3-S4c: метка 'heavy' в записи ревизии чата и фильтр тяжёлых до slice.
//
// S4b не пишет ответ тяжелее CHAT_CLASS_MAX_BYTES в кэш вовсе — такой прогрев был трафиком
// впустую, занимающим места из 12 (CHAT_WARM_MAX). Теперь вес прогрева (тот же, что у
// request()/кэша) метит запись synced-chats:{projectId} суффиксом '#heavy'; при выборе
// кандидатов тяжёлые идут после лёгких: лёгкие получают все 12 всегда, тяжёлые добирают
// только пустой остаток — иначе метку ставшего лёгким чата нельзя было бы снять.
//
// Метка переживает перезагрузку (хранится в записи) и снимается прогревом с лёгким ответом.
// Старые записи без суффикса читаются как раньше.
//
// Мутации (по одной на критерий):
//  - c1: убрать onBytes в getHistoryTail → «тяжёлый чат метится» краснеет;
//  - c2: убрать фильтр тяжёлых (все кандидаты по свежести) → «12 лёгких: тяжёлый не прогрет» краснеет;
//  - c3: не переносить старую запись в nextRevs → «метка переживает перезагрузку» краснеет.

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

interface Chat { id: string; updatedAt: string; messageCount: number }

const apiMock = vi.hoisted(() => ({
  projects: [] as { id: string }[],
  chats: new Map<string, Chat[]>(),
  // Вес ответа по чату: настоящая api передаёт onBytes в request(), тот мерит тело;
  // мок зовёт переданный колбэк с заданным весом. Если sync не передаёт колбэк —
  // он не зовётся вовсе, и метки не ставится (проверка c1).
  weights: new Map<string, number>(),
  getHistoryTail: vi.fn(async (_p: string, s: string, _q: { after: number }, onBytes?: (n: number) => void) => {
    if (onBytes) onBytes(apiMock.weights.get(s) ?? 100);
    return {};
  }),
}));
vi.mock('../api', () => ({
  api: {
    tasks: { listAll: async () => [] },
    projects: { list: async () => apiMock.projects },
    sessions: {
      list: async (projectId: string) => apiMock.chats.get(projectId) ?? [],
      getHistoryTail: apiMock.getHistoryTail,
    },
    sync: { list: async () => [] },
    files: { tree: async () => [], getContent: async () => ({ content: '' }) },
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

// Чаты проекта: updatedAt растёт с номером чата (c0 — самый старый).
const makeChats = (projectId: string, n: number): Chat[] =>
  Array.from({ length: n }, (_, i) => ({
    id: `${projectId}-c${i}`,
    updatedAt: `2026-10-01T00:00:${String(i).padStart(2, '0')}Z`,
    messageCount: 5,
  }));

const revs = () => (fake.store.get('synced-chats:p1')?.data ?? {}) as Record<string, string>;
const warmed = () => new Set(apiMock.getHistoryTail.mock.calls.map(c => c[1]));

let sync: typeof import('../sync');
let MAX: number; // CHAT_CLASS_MAX_BYTES из offline.ts

beforeEach(async () => {
  vi.resetModules();
  fake.store.clear();
  apiMock.getHistoryTail.mockClear();
  apiMock.chats.clear();
  apiMock.projects = [];
  apiMock.weights.clear();
  vi.stubGlobal('fetch', vi.fn());
  vi.stubGlobal('navigator', { onLine: true });
  vi.stubGlobal('window', { addEventListener: vi.fn(), dispatchEvent: () => true });
  vi.stubGlobal('localStorage', storageStub({ cc_token: 't', cc_user_id: 'A' }));
  vi.stubGlobal('sessionStorage', storageStub());
  MAX = (await import('../offline')).CHAT_CLASS_MAX_BYTES;
  sync = await import('../sync');
});
afterEach(() => { vi.unstubAllGlobals(); });

describe('метка heavy в записи ревизии', () => {
  it('прогрев тяжёлого чата метит запись; лёгкий пишется без метки', async () => {
    apiMock.projects = [{ id: 'p1' }];
    const chats = makeChats('p1', 2);
    apiMock.chats.set('p1', chats);
    apiMock.weights.set('p1-c0', MAX + 1);

    await sync.runOfflineSnapshot();

    expect(warmed()).toEqual(new Set(['p1-c0', 'p1-c1']));
    expect(revs()['p1-c0']).toBe(`${chats[0].updatedAt}|${chats[0].messageCount}#heavy`);
    expect(revs()['p1-c1']).toBe(`${chats[1].updatedAt}|${chats[1].messageCount}`);
  });

  // Мера одна с записью кэша (S4b): ровно на границе — не тяжёлый (records пишется при bytes == MAX).
  it('ровно CHAT_CLASS_MAX_BYTES — не тяжёлый: метки нет', async () => {
    apiMock.projects = [{ id: 'p1' }];
    const chats = makeChats('p1', 1);
    apiMock.chats.set('p1', chats);
    apiMock.weights.set('p1-c0', MAX);

    await sync.runOfflineSnapshot();

    expect(revs()['p1-c0']).toBe(`${chats[0].updatedAt}|${chats[0].messageCount}`);
  });

  it('старая запись без метки читается как раньше: прогретый чат не прогревается заново', async () => {
    apiMock.projects = [{ id: 'p1' }];
    const chats = makeChats('p1', 1);
    apiMock.chats.set('p1', chats);
    fake.store.set('synced-chats:p1', { data: { 'p1-c0': `${chats[0].updatedAt}|${chats[0].messageCount}` }, savedAt: 1 });

    await sync.runOfflineSnapshot();

    expect(apiMock.getHistoryTail).not.toHaveBeenCalled();
  });

  it('тяжёлый чат с неизменной историей не прогревается заново (трафик впустую кончен)', async () => {
    apiMock.projects = [{ id: 'p1' }];
    const chats = makeChats('p1', 1);
    apiMock.chats.set('p1', chats);
    fake.store.set('synced-chats:p1', { data: { 'p1-c0': `${chats[0].updatedAt}|${chats[0].messageCount}#heavy` }, savedAt: 1 });

    await sync.runOfflineSnapshot();

    expect(apiMock.getHistoryTail).not.toHaveBeenCalled();
    expect(revs()['p1-c0']).toBe(`${chats[0].updatedAt}|${chats[0].messageCount}#heavy`);
  });
});

describe('фильтр тяжёлых до slice', () => {
  it('14 чатов, 5 тяжёлых: все 9 лёгких прогреты; тяжёлые добирают только пустой остаток', async () => {
    apiMock.projects = [{ id: 'p1' }];
    const chats = makeChats('p1', 14);
    apiMock.chats.set('p1', chats);
    // 5 самых свежих (c9..c13) помечены записями со СТАРОЙ ревизией (messageCount 4) —
    // чаты устаревшие; без фильтра именно они заняли бы верх 12 по свежести.
    const seeded: Record<string, string> = {};
    for (let i = 9; i < 14; i++) seeded[`p1-c${i}`] = `${chats[i].updatedAt}|4#heavy`;
    fake.store.set('synced-chats:p1', { data: seeded, savedAt: 1 });
    for (let i = 9; i < 14; i++) apiMock.weights.set(`p1-c${i}`, MAX + 1);

    await sync.runOfflineSnapshot();

    const ids = [...warmed()];
    const light = Array.from({ length: 9 }, (_, i) => `p1-c${i}`);
    const heavy = Array.from({ length: 5 }, (_, i) => `p1-c${i + 9}`);
    // Было: 12 свежих = 5 тяжёлых + 7 лёгких. Стало: все 9 лёгких, тяжёлые — только остаток 3.
    expect(ids.filter(id => light.includes(id)).sort()).toEqual([...light].sort());
    expect(ids.filter(id => heavy.includes(id)).sort()).toEqual(['p1-c11', 'p1-c12', 'p1-c13']);
    expect(ids).toHaveLength(12);
    // Прогретые тяжёлые перемечены новой ревизией; непрогретые остаются со старой записью и меткой.
    expect(revs()['p1-c13']).toBe(`${chats[13].updatedAt}|${chats[13].messageCount}#heavy`);
    expect(revs()['p1-c9']).toBe(`${chats[9].updatedAt}|4#heavy`);
  });

  it('12 лёгких кандидатов: тяжёлый не прогрет вовсе — места из 12 не занимают', async () => {
    apiMock.projects = [{ id: 'p1' }];
    const chats = makeChats('p1', 13);
    apiMock.chats.set('p1', chats);
    // Самый свежий (c12) — помеченный и устаревший: без фильтра он был бы первым в 12.
    fake.store.set('synced-chats:p1', { data: { 'p1-c12': `${chats[12].updatedAt}|4#heavy` }, savedAt: 1 });
    apiMock.weights.set('p1-c12', MAX + 1);

    await sync.runOfflineSnapshot();

    const ids = [...warmed()];
    expect(ids).toHaveLength(12);
    expect(ids.includes('p1-c12')).toBe(false);
    expect(ids.filter(id => id !== 'p1-c12').sort())
      .toEqual(Array.from({ length: 12 }, (_, i) => `p1-c${i}`).sort());
  });
});

describe('жизнь метки', () => {
  it('метка переживает перезагрузку: следующий прогон её видит и не прогревает чат заново', async () => {
    apiMock.projects = [{ id: 'p1' }];
    const chats = makeChats('p1', 1);
    apiMock.chats.set('p1', chats);
    apiMock.weights.set('p1-c0', MAX + 1);

    await sync.runOfflineSnapshot();
    expect(revs()['p1-c0']).toBe(`${chats[0].updatedAt}|${chats[0].messageCount}#heavy`);

    apiMock.getHistoryTail.mockClear();
    await sync.runOfflineSnapshot();
    expect(apiMock.getHistoryTail).not.toHaveBeenCalled();
    expect(revs()['p1-c0']).toBe(`${chats[0].updatedAt}|${chats[0].messageCount}#heavy`);
  });

  it('метка снимается, когда чат стал лёгким: повторный прогрев пишет запись без метки', async () => {
    apiMock.projects = [{ id: 'p1' }];
    const chats = makeChats('p1', 1);
    apiMock.chats.set('p1', chats);
    apiMock.weights.set('p1-c0', MAX + 1);

    await sync.runOfflineSnapshot();
    expect(revs()['p1-c0']).toBe(`${chats[0].updatedAt}|${chats[0].messageCount}#heavy`);

    // Чат стал лёгким: история изменилась (новая ревизия), ответ теперь лёгкий.
    chats[0].messageCount = 6;
    apiMock.weights.set('p1-c0', 100);
    apiMock.getHistoryTail.mockClear();
    await sync.runOfflineSnapshot();

    expect(warmed()).toEqual(new Set(['p1-c0']));
    expect(revs()['p1-c0']).toBe(`${chats[0].updatedAt}|6`);
  });
});
