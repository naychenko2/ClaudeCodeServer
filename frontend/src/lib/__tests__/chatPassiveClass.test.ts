import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// По-чатовой класс пассивного кэша (F2c-e).
// Ответы, привязанные к чату (метаданные, история, бейдж расхода, статистика локальной
// модели, git-статус в дереве чата), лежат в отдельном диапазоне `offlinechat:<owner>:<chat>:<url>`:
// у них свой бюджет (потолок записей и веса) и своё вытеснение — целым чатом. Иначе
// по-чатовой вектор растёт вместе с числом открытых чатов и вытесняет общий класс.
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

function storageStub(init: Record<string, string> = {}) {
  const m = new Map(Object.entries(init));
  return {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => { m.set(k, v); },
    removeItem: (k: string) => { m.delete(k); },
  };
}

const json = (data: unknown) => ({ ok: true, status: 200, statusText: 'OK', json: async () => data, text: async () => JSON.stringify(data) });
const chatKeys = (owner: string) => [...fake.store.keys()].filter(k => k.startsWith(`offlinechat:${owner}:`));
const generalKeys = (owner: string) => [...fake.store.keys()].filter(k => k.startsWith(`offline:${owner}:`));

let offline: typeof import('../offline');
let fetchMock: ReturnType<typeof vi.fn>;
let ls: ReturnType<typeof storageStub>;

const flush = () => vi.advanceTimersByTimeAsync(0);

async function getChat(url: string, chatId: string) {
  fetchMock.mockResolvedValueOnce(json({ url }));
  const r = await offline.request(url, { chatScope: chatId });
  await flush();
  return r;
}

async function getChatOffline(url: string, chatId: string) {
  fetchMock.mockRejectedValue(new TypeError('failed'));
  const p = offline.request(url, { chatScope: chatId });
  const settled = p.then(v => ({ v }), (e: unknown) => ({ e }));
  await vi.advanceTimersByTimeAsync(15_000);
  const res = await settled;
  await flush();
  fetchMock.mockReset();
  return res;
}

beforeEach(async () => {
  vi.resetModules();
  vi.useFakeTimers();
  fake.store.clear();
  fake.calls.count = 0; fake.calls.cursor = 0;
  fetchMock = vi.fn();
  vi.stubGlobal('fetch', fetchMock);
  vi.stubGlobal('navigator', { onLine: true });
  vi.stubGlobal('window', { addEventListener: vi.fn(), dispatchEvent: () => true });
  ls = storageStub({ cc_token: 't', cc_user_id: 'A' });
  vi.stubGlobal('localStorage', ls);
  vi.stubGlobal('sessionStorage', storageStub());
  offline = await import('../offline');
});
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('по-чатовой класс: диапазон и чтение', () => {
  it('по-чатовой ответ пишется в свой диапазон, а не в общий', async () => {
    await getChat('/chats/c1', 'c1');
    expect([...fake.store.keys()]).toEqual(['offlinechat:A:c1:/chats/c1']);
    expect(generalKeys('A')).toHaveLength(0);
  });

  it('офлайн-чтение по-чатового ответа берёт ту же запись', async () => {
    await getChat('/chats/c1', 'c1');
    expect(await getChatOffline('/chats/c1', 'c1')).toEqual({ v: { url: '/chats/c1' } });
  });

  it('разные чаты не путают записи друг друга', async () => {
    await getChat('/chats/c1', 'c1');
    await getChat('/chats/c2', 'c2');
    expect(fake.store.get('offlinechat:A:c1:/chats/c1')!.data).toEqual({ url: '/chats/c1' });
    expect(fake.store.get('offlinechat:A:c2:/chats/c2')!.data).toEqual({ url: '/chats/c2' });
  });

  it('запись хранит вес ответа — по нему считается потолок по весу', async () => {
    await getChat('/chats/c1', 'c1');
    const entry = fake.store.get('offlinechat:A:c1:/chats/c1')!;
    expect(entry.bytes).toBeGreaterThan(0);
  });
});

describe('по-чатовой класс: потолки', () => {
  it('20 чатов подряд не растут линейно: класс ужимается до потолка, старый чат уходит целиком', async () => {
    const CHATS = 20;
    const PER_CHAT = 4;
    for (let c = 0; c < CHATS; c++) {
      for (let i = 0; i < PER_CHAT; i++) {
        fetchMock.mockResolvedValueOnce(json({ c, i }));
        await offline.request(`/chats/c${c}/part${i}`, { chatScope: `c${c}` });
      }
    }
    await flush();

    await offline.runPassiveMaintenance();

    // 20 чатов × 4 записи = 80 записей линейно выросли бы; потолок держит класс в узде
    expect(chatKeys('A').length).toBeLessThanOrEqual(offline.CHAT_CLASS_TRIM_RECORDS);
    expect(chatKeys('A').length).toBeLessThan(CHATS * PER_CHAT);
    // Вытесненный чат уходит ЦЕЛИКОМ: его записи не должны остаться по одной
    expect(fake.store.has('offlinechat:A:c0:/chats/c0/part0')).toBe(false);
    expect(fake.store.has('offlinechat:A:c0:/chats/c0/part3')).toBe(false);
    // Свежий чат на месте
    expect(fake.store.has(`offlinechat:A:c${CHATS - 1}:/chats/c${CHATS - 1}/part0`)).toBe(true);
  });

  it('потолок по весу: тяжёлый чат вытесняет более старые, но сам остаётся', async () => {
    // Старый лёгкий чат и свежий тяжёлый (вес задаётся записью в сторе)
    fake.store.set('offlinechat:A:old:/chats/old', { data: { x: 1 }, savedAt: 1, usedAt: 1, bytes: 1024 });
    fake.store.set('offlinechat:A:heavy:/chats/heavy', {
      data: { x: 2 }, savedAt: 2, usedAt: 2, bytes: offline.CHAT_CLASS_MAX_BYTES + 1,
    });

    await offline.runPassiveMaintenance();

    expect(fake.store.has('offlinechat:A:old:/chats/old')).toBe(false);
    expect(fake.store.has('offlinechat:A:heavy:/chats/heavy')).toBe(true);
  });

  it('общий класс по-чатовыми записями не вытесняется', async () => {
    for (let i = 0; i < 150; i++) fake.store.set(`offline:A:/p/${i}`, { data: { i }, savedAt: 1000 + i });
    for (let c = 0; c < 30; c++) {
      for (let i = 0; i < 4; i++) {
        fake.store.set(`offlinechat:A:c${c}:/chats/c${c}/part${i}`, { data: { c, i }, savedAt: 2000 + c });
      }
    }

    await offline.runPassiveMaintenance();

    expect(generalKeys('A')).toHaveLength(150);
    expect(chatKeys('A').length).toBeLessThanOrEqual(offline.CHAT_CLASS_TRIM_RECORDS);
  });
});

describe('по-чатовой класс: владелец', () => {
  it('записи прежнего аккаунта сносятся проходом обслуживания', async () => {
    fake.store.set('offlinechat:B:c1:/chats/c1', { data: { url: '/chats/c1' }, savedAt: 1 });
    fake.store.set('offlinechat:A:c2:/chats/c2', { data: { url: '/chats/c2' }, savedAt: 2 });

    await offline.runPassiveMaintenance();

    expect(fake.store.has('offlinechat:B:c1:/chats/c1')).toBe(false);
    expect(fake.store.has('offlinechat:A:c2:/chats/c2')).toBe(true);
  });
});
