import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// Решения по по-чатовым потребителям (F2c-e): кто уходит в по-чатовой класс кэша
// (свой бюджет), а кто не кэшируется вовсе (live).
//
// Проверяем не «есть ли ключ», а КАКОЙ он: по-чатовой ответ лежит в своём диапазоне
// `offlinechat:<owner>:<chat>:<url>`, а не в общем `offline:<owner>:<url>` (иначе он
// вытеснял бы чужой класс общим правилом и рос вместе с числом открытых чатов).

const mocks = vi.hoisted(() => ({
  idbSet: vi.fn(async () => 1 as unknown),
  idbGet: vi.fn(async (): Promise<unknown> => undefined),
  idbKeys: vi.fn(async (): Promise<string[]> => []),
  idbDelete: vi.fn(async () => undefined),
}));
vi.mock('../idb', () => ({
  idbGet: mocks.idbGet, idbSet: mocks.idbSet, idbKeys: mocks.idbKeys, idbDelete: mocks.idbDelete,
  idbCountRange: vi.fn(async () => 0), idbRangeStamps: vi.fn(async () => []),
  outboxAll: vi.fn(async () => []), outboxGet: vi.fn(async () => undefined),
  outboxPut: vi.fn(async () => {}), outboxDelete: vi.fn(async () => undefined),
}));
vi.mock('../signalr', () => ({
  joinUser: vi.fn(async () => {}), onMessage: vi.fn(), onReconnected: vi.fn(),
}));
vi.mock('../themeMode', () => ({ getEffectiveTheme: () => 'light' }));

function storageStub(init: Record<string, string> = {}) {
  const m = new Map(Object.entries(init));
  return {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => { m.set(k, v); },
    removeItem: (k: string) => { m.delete(k); },
  };
}

const keys = () => mocks.idbSet.mock.calls.map(c => String((c as unknown[])[0]));

beforeEach(() => {
  vi.resetModules();
  Object.values(mocks).forEach(m => m.mockClear());
  mocks.idbGet.mockResolvedValue(undefined);
  mocks.idbKeys.mockResolvedValue([]);
  const storage = storageStub({ cc_token: 't', cc_user_id: 'A' });
  vi.stubGlobal('localStorage', storage);
  vi.stubGlobal('sessionStorage', storage);
  vi.stubGlobal('navigator', { onLine: true });
  vi.stubGlobal('window', new EventTarget());
  vi.stubGlobal('document', Object.assign(new EventTarget(), { visibilityState: 'visible' }));
  vi.stubGlobal('fetch', vi.fn(async () => new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } })));
});

afterEach(() => { vi.unstubAllGlobals(); });

describe('по-чатовые потребители: что в по-чатовой класс, что live', () => {
  it('метаданные чата, история, бейдж расхода и статистика локальной модели — по-чатовой класс', async () => {
    const { api } = await import('../api');
    await api.chats.get('c1');
    await api.chats.getHistoryTail('c1', { after: -1 });
    await api.sessions.getHistoryTail('p1', 's1', { after: -1 });
    await api.spend.badge('s1');
    await api.localModel.lastTurn('s1');

    expect(keys()).toEqual(expect.arrayContaining([
      'offlinechat:A:c1:/chats/c1',
      'offlinechat:A:c1:/chats/c1/history?after=-1',
      'offlinechat:A:s1:/projects/p1/sessions/s1/history?after=-1',
      'offlinechat:A:s1:/spend/sessions/s1/badge',
      'offlinechat:A:s1:/sessions/s1/local-model',
    ]));
    // Ни одна из этих записей не попала в общий класс
    expect(keys().filter(k => k.startsWith('offline:A:'))).toEqual([]);
  });

  it('хвост истории (после курсора) не кэшируется вовсе', async () => {
    const { api } = await import('../api');
    await api.chats.getHistoryTail('c1', { after: 5 });
    await api.sessions.getHistoryTail('p1', 's1', { after: 5, fp: 'abc' });

    expect(keys().filter(k => k.includes('/history'))).toEqual([]);
  });

  it('незапушенные коммиты (до 29 КБ на чат) не кэшируются вовсе', async () => {
    const { api } = await import('../api');
    await api.git.unpushed('p1');

    expect(keys().filter(k => k.includes('git/unpushed'))).toEqual([]);
  });

  it('git-статус без активного чата — общий класс, с активным чатом — по-чатовой', async () => {
    const { api, setGitSessionContext } = await import('../api');
    // Запись в общий класс идёт в фоне (ответ сети её не ждёт) — дожидаемся
    const { flushPassiveWrites } = await import('../offline');
    await api.git.status('p1');
    await flushPassiveWrites();
    expect(keys()).toContain('offline:A:/projects/p1/git/status');

    mocks.idbSet.mockClear();
    setGitSessionContext('p1', 's1');
    await api.git.status('p1');
    expect(keys()).toContain('offlinechat:A:s1:/projects/p1/git/status?sessionId=s1');
  });
});
