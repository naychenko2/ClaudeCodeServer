import { beforeEach, describe, expect, it, vi } from 'vitest';

// Регрессия (личный чат вне проекта, дефект naychenko.me): useCharacters('personal') дёргал
// проектную ручку персонажей — projectBase('personal') бросает синхронно и рвёт весь чат.
// Точка одна — в самом хуке: область (включая 'personal') нормализуется в null, и любой
// вызывающий (LaunchAnchor, VersionCard, EditorModal, панель) безопасен по построению.
//
// Хук — mini-прогонщик (node, без рендерера): useEffect реально выполняется, как в
// useCharacterTrain.test.ts. listCharacters — реальный (projectBase('personal') бросает
// синхронно), request/onMessage — заглушки (liveApi не ходит в сеть). На старом коде (без
// нормализации и без Promise-обёртки) эффект бросает — тест краснеет.

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

const { useCharacters, reloadCharacters } = await import('./useCharacters');

const marina = { slug: 'marina', name: 'Марина', path: 'characters/marina', photos: [], createdAt: '2026-09-29T00:00:00Z' };
const settle = () => new Promise<void>(r => setTimeout(r, 0));

beforeEach(() => {
  h.request.mockReset();
  vi.restoreAllMocks();
});

describe('useCharacters: личный чат вне проекта', () => {
  it("'personal' → пустой список, error: false, listCharacters (projectBase) не бросает", async () => {
    const res = useCharacters('personal');
    expect(res).toEqual({ list: [], error: false });
    await settle(); // эффект отработал — и проектную ручку персонажей не тронул
    expect(h.request).not.toHaveBeenCalled();
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

// Страховка load(): даже если проектную ручку персонажей дёрнули областью напрямую
// (reloadCharacters), синхронный projectBase('personal') падает в .catch, а не из вызывающего
describe('load(): страховка от синхронного отказа', () => {
  it('reloadCharacters(personal) резолвится, а не бросает', async () => {
    await expect(reloadCharacters('personal')).resolves.toBeUndefined();
  });
});
