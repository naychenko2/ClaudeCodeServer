import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// F3-S4a: потолок прогрева историй чатов — 12 на ВЕСЬ прогон снапшота, а не на проект.
//
// Было: 8 на проект → на 33 проектах до 264 полных загрузок ?after=-1 за прогон.
// Порядок: приоритетный проект первым, внутри проекта — самые свежие по updatedAt.
// Ревизии непрогретых чатов не пишутся: следующий прогон их догоняет.
//
// Мутации (по одной на критерий):
//  - c1: вернуть `.slice(0, CHAT_WARM_MAX)` вместо `chatWarmLeft` → тест «ровно 12» краснеет;
//  - c2: убрать сортировку по priorityProjectId (`ordered = projects`) → тест порядка краснеет;
//  - c3: писать ревизию всем (`freshRevs` для всех stale) → тест «второй прогон» краснеет.

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
  getHistoryTail: vi.fn(async (_p: string, _s: string, _q: { after: number }) => ({})),
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

const warmedIds = () => apiMock.getHistoryTail.mock.calls.map(c => c[1]);

let sync: typeof import('../sync');

beforeEach(async () => {
  vi.resetModules();
  fake.store.clear();
  apiMock.getHistoryTail.mockClear();
  apiMock.chats.clear();
  apiMock.projects = [];
  vi.stubGlobal('fetch', vi.fn());
  vi.stubGlobal('navigator', { onLine: true });
  vi.stubGlobal('window', { addEventListener: vi.fn(), dispatchEvent: () => true });
  vi.stubGlobal('localStorage', storageStub({ cc_token: 't', cc_user_id: 'A' }));
  vi.stubGlobal('sessionStorage', storageStub());
  sync = await import('../sync');
});
afterEach(() => { vi.unstubAllGlobals(); });

describe('прогрев историй чатов: потолок на прогон', () => {
  it('33 проекта по 10 устаревших чатов: за прогон ровно 12 вызовов с after=-1', async () => {
    apiMock.projects = Array.from({ length: 33 }, (_, i) => ({ id: `p${i}` }));
    for (const p of apiMock.projects) apiMock.chats.set(p.id, makeChats(p.id, 10));

    await sync.runOfflineSnapshot();

    expect(apiMock.getHistoryTail).toHaveBeenCalledTimes(12);
    for (const c of apiMock.getHistoryTail.mock.calls) expect(c[2]).toEqual({ after: -1 });
  });

  it('приоритетный проект прогревается первым, внутри — самые свежие', async () => {
    apiMock.projects = [{ id: 'a' }, { id: 'b' }, { id: 'prio' }];
    for (const p of apiMock.projects) apiMock.chats.set(p.id, makeChats(p.id, 10));

    await sync.runOfflineSnapshot('prio');

    const ids = warmedIds();
    expect(ids).toHaveLength(12);
    // Приоритетный проект съел 10 из 12, остаток 2 ушёл следующему по списку
    const prio = ids.filter(id => id.startsWith('prio-'));
    expect(prio.sort()).toEqual(Array.from({ length: 10 }, (_, i) => `prio-c${i}`).sort());
    // Во втором проекте (a) — два самых свежих чата, c9 и c8
    expect(ids.filter(id => id.startsWith('a-')).sort()).toEqual(['a-c8', 'a-c9']);
    expect(ids.some(id => id.startsWith('b-'))).toBe(false);
  });

  it('ревизия непрогретых чатов не записана — второй прогон берёт именно их', async () => {
    apiMock.projects = [{ id: 'p1' }];
    apiMock.chats.set('p1', makeChats('p1', 20));

    await sync.runOfflineSnapshot();
    const first = warmedIds();
    expect(first).toHaveLength(12);
    // Прогреты 12 самых свежих (c8..c19)
    expect(first.sort()).toEqual(Array.from({ length: 12 }, (_, i) => `p1-c${i + 8}`).sort());

    const revs = (fake.store.get('synced-chats:p1')?.data ?? {}) as Record<string, string>;
    expect(Object.keys(revs).sort()).toEqual(first.sort());

    apiMock.getHistoryTail.mockClear();
    await sync.runOfflineSnapshot();
    // Остались 8 непрогретых (c0..c7) — все и берёт второй прогон, прогретые не трогает
    expect(warmedIds().sort()).toEqual(Array.from({ length: 8 }, (_, i) => `p1-c${i}`).sort());
  });
});
