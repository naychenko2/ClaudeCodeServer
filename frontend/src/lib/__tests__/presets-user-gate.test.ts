// Тест гейта hasUserLayer на запись в user-слой (БЛОКЕР-1: изоляция чужого слоя).
//
// Контракт (см. doSave в presets.ts): если запись в 'user'-scope стартует с незагруженным
// user-слоем, редьюсер применяется к пустому шаблону, и PUT затирает specialties/presets
// реального пользователя одним новым значением. Гейт отказывает без PUT и поднимает
// settingsError — UI рисует баннер.
//
// Тест «падает без gate в сторе»: убираем гейт в lib/presets.doSave —
// saveUserLayerMock вызывается с почти пустым слоем и saveUserLayer получает чужого
// адресата. С гейтом — mock не вызывается, getSettingsError() возвращает строку.
//
// Между тестами обязателен vi.resetModules(): приватные _settings/_userLayers живут
// в модуле presets.ts и протекают между it-блоками.

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

const POPULATED_LAYER: SpecialtySettingsLayer = {
  specialties: {
    'test-specialty': {
      access: 'full', tierStrong: 'sonnet', tierMedium: 'haiku', tierWeak: '',
    },
  },
  defaultSpecialty: {
    access: 'full', tierStrong: '', tierMedium: '', tierWeak: '',
  },
  presets: [
    { id: 'existing-preset', name: 'Существующая цепочка',
      description: null, steps: ['strong:default', 'medium:default'] },
  ],
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
    Promise.resolve({ user: POPULATED_LAYER, userId }));

  const mod = await import('../presets');
  return {
    saveLayer: mod.saveLayer,
    loadUserLayer: mod.loadUserLayer,
    getSettingsError: mod.getSettingsError,
    hasUserLayer: mod.hasUserLayer,
  };
}

describe('БЛОКЕР-1: gate hasUserLayer на запись в user-слой', () => {
  it('БЕЗ загрузки слоя — saveUserLayerMock НЕ вызывается и поднимается settingsError', async () => {
    const { saveLayer, getSettingsError } = await freshStore();

    // Слой пользователя НЕ загружен — gate должен отказать без PUT.
    const reducer = (cur: SpecialtySettingsLayer): SpecialtySettingsLayer => ({
      ...cur,
      presets: [...cur.presets, { id: 'new-preset', name: 'Новая',
        description: null, steps: ['strong:default'] }],
    });

    await saveLayer('user', reducer, 'target-user-id');
    // микротаск для промиса из очереди
    await Promise.resolve();
    await Promise.resolve();

    expect(saveUserLayerMock).not.toHaveBeenCalled();
    const err = getSettingsError();
    expect(err).toMatch(/слой пользователя/i);
  });

  it('ПОСЛЕ loadUserLayer — запись проходит, PUT уходит с полным слоем', async () => {
    const { saveLayer, loadUserLayer, hasUserLayer } = await freshStore();

    // До загрузки — gate отказывает (проверка поведения hasUserLayer).
    expect(hasUserLayer('target-user-id')).toBe(false);

    await loadUserLayer('target-user-id');
    expect(hasUserLayer('target-user-id')).toBe(true);

    saveUserLayerMock.mockClear();
    const reducer = (cur: SpecialtySettingsLayer): SpecialtySettingsLayer => ({
      ...cur,
      presets: [...cur.presets, { id: 'new-preset', name: 'Новая',
        description: null, steps: ['strong:default'] }],
    });

    await saveLayer('user', reducer, 'target-user-id');
    await Promise.resolve();
    await Promise.resolve();

    expect(saveUserLayerMock).toHaveBeenCalledTimes(1);
    // В PUT ушёл слой с уже существующими preset (не пустой шаблон) — иначе
    // перезатёрло бы чужой слой.
    const sentLayer = saveUserLayerMock.mock.calls[0][1] as SpecialtySettingsLayer;
    expect(sentLayer.presets.map(p => p.id)).toContain('existing-preset');
    expect(sentLayer.presets.map(p => p.id)).toContain('new-preset');
  });

  it('gate не мешает записи в global и owner — у них own база в settings', async () => {
    const { saveLayer } = await freshStore();

    const reducer = (cur: SpecialtySettingsLayer): SpecialtySettingsLayer => ({
      ...cur,
      presets: [...cur.presets, { id: 'gp', name: 'GP',
        description: null, steps: ['strong:default'] }],
    });

    await saveLayer('global', reducer, null);
    await saveLayer('owner', reducer, null);
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();

    expect(saveGlobalLayerMock).toHaveBeenCalledTimes(1);
    expect(saveOwnerLayerMock).toHaveBeenCalledTimes(1);
    expect(saveUserLayerMock).not.toHaveBeenCalled();
  });
});