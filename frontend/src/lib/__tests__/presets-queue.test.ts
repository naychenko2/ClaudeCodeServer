// Тест очереди записи слоя (saveLayer в lib/presets.ts).
//
// Контракт: параллельные вызовы saveLayer на одном ключе выстраиваются в очередь —
// второй PUT уходит только после ответа на первый, а его редьюсер получает УЖЕ
// сохранённый слой. Без очереди оба редьюсера накладывались бы на один и тот же
// базовый снимок, и правка, ушедшая первой, молча терялась бы (фикс 65d8df66).
//
// После ADR-012 слой один — общий (global). Прежняя редакция этого набора проверяла
// ключевание очереди по паре scope+userId и защиту чужого user-слоя: ни слоёв
// «пользователь»/«владелец», ни эндпоинтов saveUserLayer/saveOwnerLayer больше нет,
// поэтому от набора осталась та часть, которая описывает живой канал записи.
//
// Между тестами обязателен vi.resetModules(): приватные _settings/_writeInFlight
// живут в модуле presets.ts и протекают между it-блоками.
//
// Замечание про синхронность: saveLayer ставит doSave в очередь через
// `prev.then(() => doSave(...))`, поэтому сам PUT уходит в следующий микротаск.
// Перед проверкой «был ли вызван PUT» нужно дождаться `await Promise.resolve()`.

import { describe, it, expect, vi } from 'vitest';

const saveGlobalLayerMock = vi.fn();
const getSettingsMock = vi.fn();

vi.mock('../api', () => ({
  api: {
    specialties: {
      saveGlobalLayer: (...args: unknown[]) => saveGlobalLayerMock(...args),
      getSettings: (...args: unknown[]) => getSettingsMock(...args),
    },
  },
}));

import type { SpecialtySettingsLayer } from '../../types';

const EMPTY_LAYER: SpecialtySettingsLayer = {
  specialties: {}, defaultSpecialty: null, presets: [],
};

function preset(id: string) {
  return { id, name: id, description: null, steps: ['strong:default'] };
}

async function freshStore() {
  vi.resetModules();
  saveGlobalLayerMock.mockReset();
  getSettingsMock.mockReset();

  saveGlobalLayerMock.mockImplementation((layer: SpecialtySettingsLayer) =>
    Promise.resolve({ global: layer }));
  getSettingsMock.mockResolvedValue({
    version: 1,
    global: EMPTY_LAYER,
    presets: [],
  });

  const mod = await import('../presets');
  return {
    saveLayer: mod.saveLayer,
    ensurePresetSettingsLoaded: mod.ensurePresetSettingsLoaded,
  };
}

function pending(): { promise: Promise<unknown>; resolve: (v: unknown) => void } {
  let resolve: (v: unknown) => void = () => {};
  const promise = new Promise<unknown>((r) => { resolve = r; });
  return { promise, resolve };
}

type LayerReducer = (cur: SpecialtySettingsLayer) => SpecialtySettingsLayer;

describe('saveLayer — очередь записи общего слоя', () => {
  it('вторая запись ждёт ответа на первую, а не уходит параллельно', async () => {
    const store = await freshStore();
    await store.ensurePresetSettingsLoaded();

    saveGlobalLayerMock.mockReset();
    const first = pending();
    const second = pending();
    saveGlobalLayerMock
      .mockImplementationOnce(() => first.promise)
      .mockImplementationOnce(() => second.promise);

    const r: LayerReducer = (cur) => cur;

    const p1 = store.saveLayer('global', r);
    await Promise.resolve();
    expect(saveGlobalLayerMock).toHaveBeenCalledTimes(1);

    const p2 = store.saveLayer('global', r);
    await Promise.resolve();
    expect(saveGlobalLayerMock).toHaveBeenCalledTimes(1);   // ждёт первую

    first.resolve({ global: EMPTY_LAYER });
    await p1;
    await Promise.resolve();
    expect(saveGlobalLayerMock).toHaveBeenCalledTimes(2);

    second.resolve({ global: EMPTY_LAYER });
    await p2;
  });

  it('редьюсеры накладываются по порядку — второй видит правку первого', async () => {
    const store = await freshStore();
    await store.ensurePresetSettingsLoaded();

    saveGlobalLayerMock.mockReset();
    saveGlobalLayerMock.mockImplementation((layer: SpecialtySettingsLayer) =>
      Promise.resolve({ global: layer }));

    // Две правки подряд, каждая добавляет свою цепочку. Без очереди обе легли бы
    // на пустой базовый снимок, и в слое осталась бы только последняя
    const addA: LayerReducer = (cur) => ({ ...cur, presets: [...cur.presets, preset('a')] });
    const addB: LayerReducer = (cur) => ({ ...cur, presets: [...cur.presets, preset('b')] });

    const p1 = store.saveLayer('global', addA);
    const p2 = store.saveLayer('global', addB);
    await Promise.all([p1, p2]);

    expect(saveGlobalLayerMock).toHaveBeenCalledTimes(2);
    const sentFirst = saveGlobalLayerMock.mock.calls[0][0] as SpecialtySettingsLayer;
    const sentSecond = saveGlobalLayerMock.mock.calls[1][0] as SpecialtySettingsLayer;
    expect(sentFirst.presets.map(p => p.id)).toEqual(['a']);
    expect(sentSecond.presets.map(p => p.id)).toEqual(['a', 'b']);
  });

  it('запись до загрузки настроек идёт от пустого шаблона, а не падает', async () => {
    // Модалку можно открыть раньше, чем доедет GET settings: редьюсер обязан получить
    // пустой слой и собрать правку с нуля (тот же инвариант, что в PresetOptions)
    const store = await freshStore();

    saveGlobalLayerMock.mockReset();
    saveGlobalLayerMock.mockImplementation((layer: SpecialtySettingsLayer) =>
      Promise.resolve({ global: layer }));

    await store.saveLayer('global', (cur) => ({ ...cur, presets: [...cur.presets, preset('x')] }));

    expect(saveGlobalLayerMock).toHaveBeenCalledTimes(1);
    const sent = saveGlobalLayerMock.mock.calls[0][0] as SpecialtySettingsLayer;
    expect(sent.presets.map(p => p.id)).toEqual(['x']);
  });
});
