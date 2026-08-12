import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// Гибридная прочитанность (chatReadState.ts): формула max(локальная, серверная,
// baseline) и фоновый синк отметки на бэк (leading + trailing троттл, гард офлайна).

const { markReadMock, offlineState } = vi.hoisted(() => ({
  markReadMock: vi.fn(() => Promise.resolve()),
  offlineState: { online: true },
}));
vi.mock('../api', () => ({ api: { chats: { markRead: markReadMock } } }));
vi.mock('../offline', () => ({ isOnline: () => offlineState.online }));

// Стаб localStorage для node-окружения vitest (jsdom в проекте не подключён)
function fakeStorage(): Storage {
  const m = new Map<string, string>();
  return {
    get length() { return m.size; },
    key: (i: number) => [...m.keys()][i] ?? null,
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => { m.set(k, String(v)); },
    removeItem: (k: string) => { m.delete(k); },
    clear: () => { m.clear(); },
  } as Storage;
}

// Базовая точка времени: baseline модуля встанет на неё при первом обращении
const T0 = new Date('2026-08-11T10:00:00Z').getTime();
const iso = (offsetMs: number) => new Date(T0 + offsetMs).toISOString();

type Mod = typeof import('../chatReadState');
let mod: Mod;

beforeEach(async () => {
  // Свежий модуль на каждый тест: у chatReadState модульное состояние
  // (кеш отметок, baseline, карта троттла) — иначе тесты цеплялись бы друг за друга
  vi.resetModules();
  vi.useFakeTimers();
  vi.setSystemTime(T0);
  vi.stubGlobal('localStorage', fakeStorage());
  // Закрепляем baseline на T0 заранее: он фиксируется лениво по Date.now(), и тесты,
  // сдвигающие время до первого обращения, иначе получили бы «уехавший» baseline
  localStorage.setItem('cc_chats_read_since', String(T0));
  offlineState.online = true;
  markReadMock.mockClear();
  mod = await import('../chatReadState');
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('hasUnread: max(локальная, серверная, baseline)', () => {
  it('без отметок: чат новее baseline — непрочитан, старше — нет', () => {
    expect(mod.hasUnread(iso(10_000), 'a')).toBe(true);
    expect(mod.hasUnread(iso(-10_000), 'b')).toBe(false);
  });

  it('серверная отметка новее updatedAt гасит непрочитанность (чат читали на другом устройстве)', () => {
    expect(mod.hasUnread(iso(10_000), 'a', iso(20_000))).toBe(false);
    // серверная отметка старше updatedAt — не гасит
    expect(mod.hasUnread(iso(30_000), 'a', iso(20_000))).toBe(true);
  });

  it('локальная отметка новее серверной — побеждает локальная', () => {
    vi.setSystemTime(T0 + 30_000);
    mod.markChatRead('a');
    expect(mod.hasUnread(iso(25_000), 'a', iso(5_000))).toBe(false);
  });

  it('битый/отсутствующий lastReadAt трактуется как 0, а не NaN', () => {
    expect(mod.hasUnread(iso(10_000), 'a', 'мусор')).toBe(true);
    expect(mod.hasUnread(iso(10_000), 'a', null)).toBe(true);
    expect(mod.hasUnread(iso(10_000), 'a', undefined)).toBe(true);
  });

  it('битый updatedAt — чат не считается непрочитанным', () => {
    expect(mod.hasUnread('мусор', 'a', iso(5_000))).toBe(false);
  });
});

describe('countUnreadChats', () => {
  it('учитывает серверные отметки элементов', () => {
    const chats = [
      { id: 'a', updatedAt: iso(10_000) },                                // unread
      { id: 'b', updatedAt: iso(10_000), lastReadAt: iso(20_000) },       // прочитан сервером
      { id: 'c', updatedAt: iso(-10_000) },                               // старше baseline
    ];
    expect(mod.countUnreadChats(chats)).toBe(1);
  });
});

describe('markChatRead: фоновый синк на бэк', () => {
  it('первый вызов шлёт PUT немедленно (leading)', () => {
    mod.markChatRead('a');
    expect(markReadMock).toHaveBeenCalledTimes(1);
    expect(markReadMock).toHaveBeenCalledWith('a');
  });

  it('повторы в окне схлопываются в один trailing-дослов', () => {
    mod.markChatRead('a');
    mod.markChatRead('a');
    mod.markChatRead('a');
    expect(markReadMock).toHaveBeenCalledTimes(1);

    // Дослов уходит по истечении окна — финальное состояние не теряется
    vi.advanceTimersByTime(5_000);
    expect(markReadMock).toHaveBeenCalledTimes(2);
  });

  it('после паузы больше окна следующий вызов снова leading', () => {
    mod.markChatRead('a');
    vi.advanceTimersByTime(6_000);
    mod.markChatRead('a');
    expect(markReadMock).toHaveBeenCalledTimes(2);
  });

  it('троттл — per chat: разные чаты не мешают друг другу', () => {
    mod.markChatRead('a');
    mod.markChatRead('b');
    expect(markReadMock).toHaveBeenCalledTimes(2);
  });

  it('в офлайне PUT не шлётся, но локальная отметка ставится', () => {
    offlineState.online = false;
    vi.setSystemTime(T0 + 30_000);
    mod.markChatRead('a');
    expect(markReadMock).not.toHaveBeenCalled();
    expect(mod.hasUnread(iso(25_000), 'a')).toBe(false);
  });

  it('ошибка запроса глотается молча', () => {
    markReadMock.mockReturnValueOnce(Promise.reject(new Error('сеть упала')));
    expect(() => mod.markChatRead('a')).not.toThrow();
  });
});
