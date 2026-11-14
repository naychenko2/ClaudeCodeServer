import { beforeEach, describe, expect, it, vi } from 'vitest';

// Личный чат вне проекта (ADR-040 Ф2-4): useCharacters('personal') ходит на личные ручки
// персонажей чата (/image-editor/chats/{sessionId}/characters), как проект — на ручки проекта.
// Без чата в области (enterScope не звали) синхронный отказ projectBase уходит в .catch, а не
// из эффекта и не рвёт чат.
//
// Хук — mini-прогонщик (node, без рендерера): useEffect реально выполняется, как в
// useCharacterTrain.test.ts. request/onMessage — заглушки (liveApi не ходит в сеть).

const h = vi.hoisted(() => ({
  request: vi.fn((_url: string, _opts?: unknown) => Promise.resolve([])),
  onMessage: vi.fn(),
}));

vi.mock('react', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react')>();
  return {
    ...actual,
    // Значение снапшота хуком не используется — ноль; useEffect выполняет колбэк в микротаске
    useSyncExternalStore: () => 0,
    useEffect: (fn: () => void | (() => void)) => { queueMicrotask(() => { fn(); }); },
  };
});
// request/onMessage — заглушки: liveApi не ходит в сеть; listCharacters остаётся реальным
vi.mock('aihome_shell/kit', async (importOriginal) => {
  const actual = await importOriginal<typeof import('aihome_shell/kit')>();
  return { ...actual, request: h.request, onMessage: h.onMessage };
});

const { useCharacters, reloadCharacters, resetCharactersCache } = await import('./useCharacters');
const { runLogoutResets } = await import('../../../lib/logoutReset');
const { enterScope, __resetScope } = await import('../scope');

const marina = { slug: 'marina', name: 'Марина', path: 'characters/marina', photos: [], createdAt: '2026-09-29T00:00:00Z' };
const settle = () => new Promise<void>(r => setTimeout(r, 0));

beforeEach(() => {
  __resetScope();
  h.request.mockReset();
  vi.restoreAllMocks();
});

describe('useCharacters: личный чат вне проекта', () => {
  it("'personal' → список с личной ручки чата", async () => {
    enterScope(null, 's1');
    h.request.mockReturnValue(Promise.resolve([marina]));
    expect(useCharacters('personal').list).toBeNull(); // ещё не загружено
    await settle();
    expect(h.request).toHaveBeenCalledTimes(1);
    expect(h.request.mock.calls[0][0]).toBe('/image-editor/chats/s1/characters');
    expect(useCharacters('personal').list).toEqual([marina]);
  });

  it('null → пустой список, request не зовётся (как раньше)', async () => {
    const res = useCharacters(null);
    expect(res).toEqual({ list: [], error: false });
    await settle();
    expect(h.request).not.toHaveBeenCalled();
  });

  it('проект → listCharacters зовётся, список из ответа', async () => {
    h.request.mockReturnValue(Promise.resolve([marina]));
    const res = useCharacters('p1');
    expect(res.list).toBeNull(); // ещё не загружено
    await settle();
    expect(h.request).toHaveBeenCalledTimes(1);
    expect(useCharacters('p1').list).toEqual([marina]);
  });
});

// Страховка load(): синхронный отказ (личная область без чата) падает в .catch, а не из вызывающего
describe('load(): страховка от синхронного отказа', () => {
  it('reloadCharacters(personal) без чата резолвится, а не бросает', async () => {
    await expect(reloadCharacters('personal')).resolves.toBeUndefined();
  });
});

describe('useCharacters: выход из аккаунта', () => {
  it("кэш 'personal' и проектов сбрасывается при runLogoutResets", async () => {
    enterScope(null, 's1');
    h.request.mockReturnValue(Promise.resolve([marina]));
    useCharacters('personal');
    await settle();
    expect(useCharacters('personal').list).toEqual([marina]);

    runLogoutResets();

    expect(useCharacters('personal').list).toBeNull();
  });

  it('resetCharactersCache чистит кэш и напрямую', async () => {
    enterScope(null, 's1');
    h.request.mockReturnValue(Promise.resolve([marina]));
    useCharacters('personal');
    await settle();

    resetCharactersCache();

    expect(useCharacters('personal').list).toBeNull();
  });
});
