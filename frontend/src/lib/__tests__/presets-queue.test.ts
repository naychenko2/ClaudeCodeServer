// Тест ключевания очереди PUT (saveLayer в lib/presets.ts).
//
// Контракт (см. writeKey в presets.ts): пара scope+userId защищает user-слой
// двух разных адресатов от перетирания ответов друг друга. Если ключ убрать
// до scope — параллельные правки разных пользователей гонятся в одной очереди,
// и ответ одного из них может затереть оптимистичный апдейт другого.
//
// Тест «падает при ключевании только по scope» — это защита от регрессии
// writeKey: вызовы saveLayer('user', r1, 'X') и saveLayer('user', r2, 'Y') идут
// через РАЗНЫЕ ключи ('user:X' и 'user:Y'), doSave для них стартует сразу,
// без ожидания. Если writeKey схлопнет ключи — оба saveUserLayer НЕ стартуют
// одновременно, и тест на это падает.
//
// Между тестами обязателен vi.resetModules(): приватные _settings/_userLayers/
// _writeInFlight живут в модуле presets.ts и протекают между it-блоками, если
// модуль не переимпортировать.
//
// Замечание про синхронность: saveLayer ставит doSave в очередь через
// `prev.then(() => doSave(...))`, поэтому сам PUT уходит в следующий микротаск.
// Перед проверкой «был ли вызван PUT» нужно дождаться `await Promise.resolve()`.

import { describe, it, expect, vi } from 'vitest';

const saveUserLayerMock = vi.fn();
const saveGlobalLayerMock = vi.fn();
const saveOwnerLayerMock = vi.fn();
const getSettingsMock = vi.fn();
const getUserLayerMock = vi.fn();

vi.mock('../api', () => ({
  api: {
    specialties: {
      saveUserLayer: (...args: unknown[]) => saveUserLayerMock(...args),
      saveGlobalLayer: (...args: unknown[]) => saveGlobalLayerMock(...args),
      saveOwnerLayer: (...args: unknown[]) => saveOwnerLayerMock(...args),
      getSettings: (...args: unknown[]) => getSettingsMock(...args),
      getUserLayer: (...args: unknown[]) => getUserLayerMock(...args),
    },
  },
}));

import type { SpecialtySettingsLayer } from '../../types';

const EMPTY_LAYER: SpecialtySettingsLayer = {
  specialties: {}, defaultSpecialty: null, presets: [],
};

async function freshStore() {
  vi.resetModules();
  saveUserLayerMock.mockReset();
  saveGlobalLayerMock.mockReset();
  saveOwnerLayerMock.mockReset();
  getSettingsMock.mockReset();
  getUserLayerMock.mockReset();

  saveUserLayerMock.mockImplementation((_userId: string, layer: SpecialtySettingsLayer) =>
    Promise.resolve({ user: layer }));
  saveGlobalLayerMock.mockImplementation((layer: SpecialtySettingsLayer) =>
    Promise.resolve({ global: layer }));
  saveOwnerLayerMock.mockImplementation((layer: SpecialtySettingsLayer) =>
    Promise.resolve({ owner: layer }));
  getSettingsMock.mockResolvedValue({
    version: 1,
    global: EMPTY_LAYER,
    owner: EMPTY_LAYER,
    user: EMPTY_LAYER,
    presets: [],
  });
  getUserLayerMock.mockImplementation((userId: string) =>
    Promise.resolve({ user: EMPTY_LAYER, userId }));

  const mod = await import('../presets');
  return {
    saveLayer: mod.saveLayer,
    loadUserLayer: mod.loadUserLayer,
    ensurePresetSettingsLoaded: mod.ensurePresetSettingsLoaded,
  };
}

function pending(): { promise: Promise<unknown>; resolve: (v: unknown) => void } {
  let resolve: (v: unknown) => void = () => {};
  const promise = new Promise<unknown>((r) => { resolve = r; });
  return { promise, resolve };
}

type LayerReducer = (cur: SpecialtySettingsLayer) => SpecialtySettingsLayer;

describe('saveLayer — ключевание очереди PUT по scope+userId', () => {
  it('два параллельных saveLayer на РАЗНЫЕ userId → оба PUT стартуют немедленно', async () => {
    const store = await freshStore();
    await store.loadUserLayer('X');
    await store.loadUserLayer('Y');

    saveUserLayerMock.mockReset();
    const a = pending();
    const b = pending();
    saveUserLayerMock.mockImplementationOnce(() => a.promise).mockImplementationOnce(() => b.promise);

    const r1: LayerReducer = (cur) => cur;
    const r2: LayerReducer = (cur) => cur;

    const p1 = store.saveLayer('user', r1, 'X');
    const p2 = store.saveLayer('user', r2, 'Y');
    // saveLayer ставит doSave в очередь через .then — один микротаск
    // достаточно, чтобы оба PUT ушли на мок.
    await Promise.resolve();

    expect(saveUserLayerMock).toHaveBeenCalledTimes(2);
    const args = saveUserLayerMock.mock.calls.map(c => c[0]);
    expect(args).toEqual(expect.arrayContaining(['X', 'Y']));

    a.resolve({ user: EMPTY_LAYER });
    b.resolve({ user: EMPTY_LAYER });
    await Promise.all([p1, p2]);
  });

  it('saveLayer на разные userId в одной транзакции → НЕ сериализуются друг за другом', async () => {
    const store = await freshStore();
    await store.loadUserLayer('X');
    await store.loadUserLayer('Y');

    saveUserLayerMock.mockReset();
    const hangs: Array<ReturnType<typeof pending>> = [pending(), pending()];
    let i = 0;
    saveUserLayerMock.mockImplementation(() => hangs[i++].promise);

    const r1: LayerReducer = (cur) => cur;
    const r2: LayerReducer = (cur) => cur;
    const p1 = store.saveLayer('user', r1, 'X');
    const p2 = store.saveLayer('user', r2, 'Y');
    await Promise.resolve();

    expect(saveUserLayerMock.mock.calls.length).toBe(2);
    expect(saveUserLayerMock.mock.calls[0][0]).toBe('X');
    expect(saveUserLayerMock.mock.calls[1][0]).toBe('Y');

    for (const h of hangs) h.resolve({ user: EMPTY_LAYER });
    await Promise.all([p1, p2]);
  });

  it('два параллельных saveLayer на ОДИН userId → второй ждёт первый', async () => {
    const store = await freshStore();
    await store.loadUserLayer('X');

    saveUserLayerMock.mockReset();
    const first = pending();
    const second = pending();
    saveUserLayerMock
      .mockImplementationOnce(() => first.promise)
      .mockImplementationOnce(() => second.promise);

    const r1: LayerReducer = (cur) => cur;
    const r2: LayerReducer = (cur) => cur;

    const p1 = store.saveLayer('user', r1, 'X');
    await Promise.resolve();
    expect(saveUserLayerMock).toHaveBeenCalledTimes(1);
    expect(saveUserLayerMock.mock.calls[0][0]).toBe('X');

    // Второй saveLayer на тот же userId — должен встать в очередь.
    const p2 = store.saveLayer('user', r2, 'X');
    await Promise.resolve();
    expect(saveUserLayerMock).toHaveBeenCalledTimes(1); // ждёт первый

    first.resolve({ user: EMPTY_LAYER });
    await p1;
    // После резолва первого — второй стартует в следующем микротаске.
    await Promise.resolve();
    expect(saveUserLayerMock).toHaveBeenCalledTimes(2);
    expect(saveUserLayerMock.mock.calls[1][0]).toBe('X');

    second.resolve({ user: EMPTY_LAYER });
    await p2;
  });

  it('global и owner имеют РАЗНЫЕ ключи — параллельные правки обоих слоёв идут независимо', async () => {
    const store = await freshStore();
    await store.ensurePresetSettingsLoaded();

    saveGlobalLayerMock.mockReset();
    saveOwnerLayerMock.mockReset();
    const gHang = pending();
    const oHang = pending();
    saveGlobalLayerMock.mockImplementationOnce(() => gHang.promise);
    saveOwnerLayerMock.mockImplementationOnce(() => oHang.promise);

    const r1: LayerReducer = (cur) => cur;
    const r2: LayerReducer = (cur) => cur;
    const pg = store.saveLayer('global', r1);
    const po = store.saveLayer('owner', r2);
    await Promise.resolve();

    expect(saveGlobalLayerMock).toHaveBeenCalledTimes(1);
    expect(saveOwnerLayerMock).toHaveBeenCalledTimes(1);

    gHang.resolve({ global: EMPTY_LAYER });
    oHang.resolve({ owner: EMPTY_LAYER });
    await Promise.all([pg, po]);
  });

  it('user-слой с разными userId использует разные ключи даже в одной модалке', async () => {
    const store = await freshStore();
    await store.loadUserLayer('alice');
    await store.loadUserLayer('bob');

    saveUserLayerMock.mockReset();
    const a = pending();
    const b = pending();
    saveUserLayerMock.mockImplementationOnce(() => a.promise).mockImplementationOnce(() => b.promise);

    const r: LayerReducer = (cur) => cur;
    const p1 = store.saveLayer('user', r, 'alice');
    const p2 = store.saveLayer('user', r, 'bob');
    await Promise.resolve();

    expect(saveUserLayerMock).toHaveBeenCalledTimes(2);
    expect(saveUserLayerMock.mock.calls[0][0]).toBe('alice');
    expect(saveUserLayerMock.mock.calls[1][0]).toBe('bob');

    a.resolve({ user: EMPTY_LAYER });
    b.resolve({ user: EMPTY_LAYER });
    await Promise.all([p1, p2]);
  });

  it('user-scope без userId использует ключ user: (отдельный от любых user:X)', async () => {
    const store = await freshStore();
    await store.loadUserLayer('X');

    saveUserLayerMock.mockReset();
    const noIdHang = pending();
    const xHang = pending();
    saveUserLayerMock
      .mockImplementationOnce(() => noIdHang.promise)
      .mockImplementationOnce(() => xHang.promise);

    const r: LayerReducer = (cur) => cur;
    const pNoId = store.saveLayer('user', r);
    const pX = store.saveLayer('user', r, 'X');
    await Promise.resolve();

    expect(saveUserLayerMock).toHaveBeenCalledTimes(2);
    // saveLayer('user', r) без userId — api получает undefined (исходный userId
    // без подмены на ''). Важно лишь, что ключ 'user:' изолирован от 'user:X'.
    expect(saveUserLayerMock.mock.calls[0][0]).toBeUndefined();
    expect(saveUserLayerMock.mock.calls[1][0]).toBe('X');

    noIdHang.resolve({ user: EMPTY_LAYER });
    xHang.resolve({ user: EMPTY_LAYER });
    await Promise.all([pNoId, pX]);
  });
});