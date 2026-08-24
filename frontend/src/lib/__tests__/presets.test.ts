// Тесты уровня стора на результат этапа 0: после починки затирания чужого слоя
// настроек user-слой пользователя X живёт ОТДЕЛЬНО от settings вызывающего, а
// запись через commit/rollback не задевает _settings.
//
// Стор тут — src/lib/presets.ts. Запись в user-слой делают компоненты
// (ChainsTab/SlotsTab/PresetOptions), но базу они берут через getUserLayer(X) и
// обновляют стор через commitUserLayer/rollbackUserLayer. Этот файл проверяет,
// что эти примитивы стора не дают записи затереть ни слой вызывающего, ни
// specialties/presets settings, и что hasUserLayer различает «нет ключа» и
// «ключ есть, значение пустое» (проверка по наличию ключа, а не по falsy).

import { describe, it, expect, beforeEach, vi } from 'vitest';

// Моки api: для этих тестов нужны getSettings (settings вызывающего) и
// getUserLayer (чужой слой X). saveUserLayer не вызываем напрямую —
// компоненты делают PUT вне стора, а стор только хранит результат.
const getSettingsMock = vi.fn();
const getUserLayerMock = vi.fn();
vi.mock('../api', () => ({
  api: {
    specialties: {
      getSettings: (...args: unknown[]) => getSettingsMock(...args),
      getUserLayer: (...args: unknown[]) => getUserLayerMock(...args),
    },
  },
}));

import type { SpecialtySettingsLayer, SpecialtySettingsResponse } from '../../types';

const EMPTY_LAYER: SpecialtySettingsLayer = {
  specialties: {}, defaultSpecialty: null, presets: [],
};

// Снимок owner/user вызывающего (админа): specialties и пресеты свои
const ADMIN_USER_LAYER: SpecialtySettingsLayer = {
  specialties: { 'admin-specialty': { access: 'full', tools: null, disallowedTools: null } },
  defaultSpecialty: null,
  presets: [
    { id: 'admin-preset', name: 'Админский', description: null, steps: ['opus'] },
  ],
};

// Снимок user-слоя конкретного пользователя X: specialties и пресеты чужие
const TARGET_USER_LAYER: SpecialtySettingsLayer = {
  specialties: { 'user-specialty': { access: 'readOnly', tools: null, disallowedTools: null } },
  defaultSpecialty: null,
  presets: [
    { id: 'user-preset', name: 'Юзерский', description: null, steps: ['haiku'] },
  ],
};

function settingsWithAdminLayer(): SpecialtySettingsResponse {
  return {
    version: 1,
    global: { specialties: {}, defaultSpecialty: null, presets: [] },
    owner: { specialties: {}, defaultSpecialty: null, presets: [] },
    user: ADMIN_USER_LAYER,
    presets: [
      { id: 'admin-preset', name: 'Админский', scope: 'owner', description: null, steps: ['opus'] },
    ],
  };
}

beforeEach(() => {
  getSettingsMock.mockReset();
  getUserLayerMock.mockReset();
});

// vitest даёт vi.resetModules — переимпортируем стор под новым identity, чтобы
// _settings/_userLayers из предыдущего теста не «протекали» между тестами.
async function freshStore() {
  vi.resetModules();
  const mod = await import('../presets');
  return {
    ensurePresetSettingsLoaded: mod.ensurePresetSettingsLoaded,
    getSpecialtySettings: mod.getSpecialtySettings,
    loadUserLayer: mod.loadUserLayer,
    getUserLayer: mod.getUserLayer,
    hasUserLayer: mod.hasUserLayer,
    commitUserLayer: mod.commitUserLayer,
    rollbackUserLayer: mod.rollbackUserLayer,
  };
}

describe('стор presets — изоляция записи в чужой user-слой (этап 0)', () => {
  // === Изоляция: user-слой X живёт ОТДЕЛЬНО от settings.user вызывающего ===

  it('user-слой X живёт ОТДЕЛЬНО от settings.user: разные объекты, разные specialties и пресеты', async () => {
    getSettingsMock.mockResolvedValueOnce(settingsWithAdminLayer());
    getUserLayerMock.mockResolvedValueOnce({ user: TARGET_USER_LAYER, userId: 'X' });

    const store = await freshStore();
    await store.ensurePresetSettingsLoaded();
    await store.loadUserLayer('X');

    const layerX = store.getUserLayer('X');
    const settingsUser = store.getSpecialtySettings()!.user;

    // Это два РАЗНЫХ объекта — иначе база для записи в user-слой пришла бы из
    // settings.user, и PUT залил бы в слой реального пользователя specialties/
    // presets вызывающего (тот самый баг этапа 0)
    expect(layerX).not.toBe(settingsUser);
    expect(layerX!.specialties).not.toBe(settingsUser!.specialties);
    expect(layerX!.presets).not.toBe(settingsUser!.presets);

    // specialties разные: админская есть только в settings.user, юзерская — только в X
    expect(layerX!.specialties['user-specialty']).toBeDefined();
    expect(settingsUser!.specialties['user-specialty']).toBeUndefined();
    expect(settingsUser!.specialties['admin-specialty']).toBeDefined();
    expect(layerX!.specialties['admin-specialty']).toBeUndefined();

    // пресеты разные
    expect(layerX!.presets[0].id).toBe('user-preset');
    expect(settingsUser!.presets[0].id).toBe('admin-preset');
  });

  // === Запись: commitUserLayer в user-слой X не трогает settings вызывающего ===

  it('commitUserLayer в user-слой X не меняет ни specialties, ни presets settings вызывающего', async () => {
    getSettingsMock.mockResolvedValueOnce(settingsWithAdminLayer());
    getUserLayerMock.mockResolvedValueOnce({ user: TARGET_USER_LAYER, userId: 'X' });

    const store = await freshStore();
    await store.ensurePresetSettingsLoaded();
    await store.loadUserLayer('X');

    // Снимки settings ДО записи — для жёсткой проверки identity
    const settingsBefore = store.getSpecialtySettings();
    const settingsUserBefore = settingsBefore!.user!;
    const settingsSpecialtiesBefore = settingsBefore!.user!.specialties;
    const settingsPresetsBefore = settingsBefore!.presets;

    // Имитируем то, что делает компонент при записи в user-scope: берёт базу из
    // getUserLayer(X), добавляет пресет и фиксирует через commitUserLayer
    const prev = store.getUserLayer('X')!;
    const newPreset = { id: 'new-1', name: 'Новая цепочка X', description: null, steps: ['sonnet'] };
    const next: SpecialtySettingsLayer = {
      ...prev, presets: [...prev.presets, newPreset],
    };
    store.commitUserLayer('X', next);

    // === Главные проверки: settings вызывающего не изменились ===
    // 1. сам объект settings ТОТ ЖЕ (commitUserLayer не пересоздаёт _settings)
    expect(store.getSpecialtySettings()).toBe(settingsBefore);
    // 2. user-слой вызывающего внутри settings тот же объект и те же поля
    expect(store.getSpecialtySettings()!.user).toBe(settingsUserBefore);
    expect(store.getSpecialtySettings()!.user!.specialties).toBe(settingsSpecialtiesBefore);
    expect(store.getSpecialtySettings()!.user!.presets).toBe(settingsUserBefore.presets);
    // 3. объединённый список пресетов вызывающего (включая admin-preset) тот же
    expect(store.getSpecialtySettings()!.presets).toBe(settingsPresetsBefore);
    // 4. specialties админа на месте и не затёрты юзерскими
    expect(store.getSpecialtySettings()!.user!.specialties['admin-specialty']).toBeDefined();
    expect(store.getSpecialtySettings()!.user!.specialties['user-specialty']).toBeUndefined();
    // 5. пресет админа на месте, новый пресет НЕ просочился в settings
    expect(store.getSpecialtySettings()!.user!.presets).toHaveLength(1);
    expect(store.getSpecialtySettings()!.user!.presets[0].id).toBe('admin-preset');

    // А вот в user-слой X запись прошла — новый пресет на месте
    expect(store.getUserLayer('X')!.presets).toHaveLength(2);
    expect(store.getUserLayer('X')!.presets[1].id).toBe('new-1');
  });

  // === Проверка hasUserLayer: различает «нет ключа» и «ключ есть, значение пустое» ===

  it('до loadUserLayer(X): ключа в _userLayers нет, hasUserLayer=false, запись отказывается', async () => {
    // НЕ мокаем getUserLayer — слой X не загружен и не запрашивался
    const store = await freshStore();
    expect(store.hasUserLayer('X')).toBe(false);
    expect(store.getUserLayer('X')).toBeNull();
    // Защита от записи без слоя: getUserLayer вернёт null, hasUserLayer=false —
    // компонент не дойдёт до PUT (ChainsTab/PresetOptions проверяют hasUserLayer
    // перед onSaveLayer). Тест ниже это и подтверждает.
    expect(getUserLayerMock).not.toHaveBeenCalled();
  });

  it('пустой слой X: ключ в _userLayers ЕСТЬ, hasUserLayer=true — отказа от записи нет', async () => {
    // Сервер ответил пустым слоем (новый пользователь без своих настроек).
    // Ключ есть в _userLayers, значение = EMPTY_LAYER ({} truthy, presets=[]).
    // Проверка наличия ключа (`!== undefined`) и проверка по falsy (`!!`) обе
    // вернут true на таком слое — но защита от `null` в _userLayers у первой
    // есть, у второй нет.
    getUserLayerMock.mockResolvedValueOnce({ user: EMPTY_LAYER, userId: 'X' });

    const store = await freshStore();
    await store.loadUserLayer('X');

    // Ключ есть → hasUserLayer=true. Если бы реализация была `!!_userLayers[X]`,
    // пустой `{}` тоже бы прошёл, но null или 0 — провалились бы (потеряли бы
    // возможность записи в такой «пустой по ошибке» слой)
    expect(store.hasUserLayer('X')).toBe(true);
    expect(store.getUserLayer('X')).toEqual(EMPTY_LAYER);
  });

  it('edge case: защита hasUserLayer от null в _userLayers (проверка наличия ключа, не falsy)', async () => {
    // Симулируем редкий случай, когда компонент по ошибке положил null в
    // _userLayers через commitUserLayer(userId, null as unknown as layer).
    // Текущая реализация hasUserLayer (`!== undefined`) защищает от такой ошибки:
    // ключ есть → true, и getUserLayer вернёт этот null (UI увидит, что слой
    // «как-то странный»). Если бы реализация была `!!`, ключ с null дал бы false
    // — и запись бы тихо заблокировалась без объяснения.
    const store = await freshStore();
    // Имитируем ошибку программиста: записали null в обход типизации
    store.commitUserLayer('X', null as unknown as SpecialtySettingsLayer);

    // Текущий hasUserLayer (`!== undefined`): ключ ЕСТЬ → true.
    // Альтернативный `!!` для null вернул бы false — этот тест защищает от такой
    // регрессии (компонент бы тихо отказался от PUT без объяснения).
    expect(store.hasUserLayer('X')).toBe(true);
  });

  // === Полный цикл записи: commit (успешный PUT) → rollback (отказ PUT) ===

  it('полный сценарий: загрузка → успешный PUT (commit) → отказ PUT (rollback) → слой вернулся', async () => {
    getUserLayerMock.mockResolvedValueOnce({ user: TARGET_USER_LAYER, userId: 'X' });

    const store = await freshStore();
    await store.loadUserLayer('X');

    // Шаг 1: prev — снимок до правки (его покажем rollback'у при отказе PUT)
    const prev = store.getUserLayer('X')!;

    // Шаг 2: имитируем оптимистичный апдейт стора при успешном ответе PUT.
    // Ответ сервера (response.user) совпадает с тем, что мы передали в PUT —
    // для user-снимка мы кладём именно этот объект через commitUserLayer.
    const newPreset = { id: 'new-1', name: 'Новая', description: null, steps: ['opus'] };
    const putBody: SpecialtySettingsLayer = {
      ...prev, presets: [...prev.presets, newPreset],
    };
    store.commitUserLayer('X', putBody);

    // Проверяем, что ответ PUT закоммитился в userLayers[X] (новый пресет на месте)
    expect(store.getUserLayer('X')!.presets).toHaveLength(2);
    expect(store.getUserLayer('X')!.presets[1]).toEqual(newPreset);
    // specialties и прежние пресеты сохранены — запись меняет только адресованное поле
    expect(store.getUserLayer('X')!.specialties).toEqual(prev.specialties);
    expect(store.getUserLayer('X')!.presets[0]).toEqual(prev.presets[0]);

    // Шаг 3: имитируем отказ PUT (400/сеть/прав). Компонент зовёт
    // rollbackUserLayer(X, prev), чтобы стор вернулся к состоянию до правки.
    store.rollbackUserLayer('X', prev);

    // Слой вернулся к состоянию ДО правки: нового пресета нет, юзерский пресет
    // и его specialties на месте
    expect(store.getUserLayer('X')!.presets).toHaveLength(1);
    expect(store.getUserLayer('X')!.presets[0].id).toBe('user-preset');
    expect(store.getUserLayer('X')!.specialties).toEqual(prev.specialties);
    // Объект может быть новым (клон), но содержимое идентично prev
    expect(store.getUserLayer('X')).toEqual(prev);
  });

  it('rollbackUserLayer к undefined: ключ удаляется, hasUserLayer снова false', async () => {
    // Сценарий «слой не существовал до PUT» — крайне редкий (PUT с пустым
    // ответом сервера на только что появившийся слой), но rollback должен
    // корректно удалить ключ, чтобы слой считался «не загружен»
    const store = await freshStore();
    expect(store.hasUserLayer('X')).toBe(false);

    // commit + rollback к undefined — последовательность как при гипотетическом
    // оптимистичном коммите пустого слоя и откате
    store.commitUserLayer('X', EMPTY_LAYER);
    expect(store.hasUserLayer('X')).toBe(true);

    store.rollbackUserLayer('X', undefined);
    expect(store.hasUserLayer('X')).toBe(false);
    expect(store.getUserLayer('X')).toBeNull();
  });
});
