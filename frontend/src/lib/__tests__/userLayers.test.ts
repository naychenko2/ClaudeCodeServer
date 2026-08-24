import { describe, it, expect, beforeEach, vi } from 'vitest';

// Стор user-слоёв тянет api.specialties.getUserLayer — мокаем конкретный метод,
// остальной api остаётся пустым (как в смежных тестах)
const getUserLayerMock = vi.fn();
vi.mock('../api', () => ({
  api: {
    specialties: {
      getUserLayer: (...args: unknown[]) => getUserLayerMock(...args),
    },
  },
}));

import type { SpecialtySettingsLayer } from '../../types';

const EMPTY_LAYER: SpecialtySettingsLayer = { specialties: {}, defaultSpecialty: null, presets: [] };

beforeEach(() => {
  getUserLayerMock.mockReset();
});

// Сбрасываем модуль между тестами, иначе _userLayers из предыдущего теста «протекает».
// vitest даёт vi.resetModules — переимпортируем стор под новым identity.
async function freshStore() {
  vi.resetModules();
  const mod = await import('../presets');
  return {
    loadUserLayer: mod.loadUserLayer,
    getUserLayer: mod.getUserLayer,
    hasUserLayer: mod.hasUserLayer,
    getUserLayerError: mod.getUserLayerError,
    commitUserLayer: mod.commitUserLayer,
    rollbackUserLayer: mod.rollbackUserLayer,
  };
}

describe('стор user-слоёв — изоляция записи в чужой слой', () => {
  it('loadUserLayer на null — no-op (защита от эффектов без контекста)', async () => {
    const store = await freshStore();
    await expect(store.loadUserLayer(null)).resolves.toBeUndefined();
    await expect(store.loadUserLayer(undefined)).resolves.toBeUndefined();
    await expect(store.loadUserLayer('')).resolves.toBeUndefined();
    expect(getUserLayerMock).not.toHaveBeenCalled();
  });

  it('до loadUserLayer: hasUserLayer=false, getUserLayer=null', async () => {
    const store = await freshStore();
    expect(store.hasUserLayer('u1')).toBe(false);
    expect(store.getUserLayer('u1')).toBeNull();
  });

  it('после loadUserLayer: слой лежит в сторе, hasUserLayer=true (даже для пустого ответа сервера)', async () => {
    getUserLayerMock.mockResolvedValueOnce({ user: EMPTY_LAYER, userId: 'u1' });
    const store = await freshStore();
    await store.loadUserLayer('u1');
    expect(store.hasUserLayer('u1')).toBe(true);
    expect(store.getUserLayer('u1')).toEqual(EMPTY_LAYER);
  });

  it('commitUserLayer обновляет снимок; rollbackUserLayer возвращает прежний', async () => {
    const populated: SpecialtySettingsLayer = {
      specialties: { coding: { access: 'full', tools: null, disallowedTools: null } },
      defaultSpecialty: null,
      presets: [{ id: 'p1', name: 'Рабочая', description: null, steps: ['opus', 'sonnet'] }],
    };
    getUserLayerMock.mockResolvedValueOnce({ user: populated, userId: 'u1' });
    const store = await freshStore();
    await store.loadUserLayer('u1');

    const prev = store.getUserLayer('u1')!;
    const next: SpecialtySettingsLayer = {
      ...prev,
      presets: [...prev.presets, { id: 'p2', name: 'Новая', description: null, steps: ['haiku'] }],
    };
    store.commitUserLayer('u1', next);
    expect(store.getUserLayer('u1')?.presets).toHaveLength(2);

    // specialties и прежние пресеты сохранены — критерий «после loadUserLayer(X) запись
    // меняет только адресованное поле»
    expect(store.getUserLayer('u1')?.specialties).toEqual(populated.specialties);
    expect(store.getUserLayer('u1')?.presets[0]).toEqual(populated.presets[0]);

    store.rollbackUserLayer('u1', prev);
    expect(store.getUserLayer('u1')?.presets).toHaveLength(1);
    expect(store.getUserLayer('u1')?.presets[0].id).toBe('p1');
  });

  it('rollbackUserLayer к undefined — ключ удаляется, hasUserLayer снова false', async () => {
    getUserLayerMock.mockResolvedValueOnce({ user: EMPTY_LAYER, userId: 'u1' });
    const store = await freshStore();
    await store.loadUserLayer('u1');
    expect(store.hasUserLayer('u1')).toBe(true);
    store.rollbackUserLayer('u1', undefined);
    expect(store.hasUserLayer('u1')).toBe(false);
    expect(store.getUserLayer('u1')).toBeNull();
  });

  it('ошибка загрузки: hasUserLayer остаётся false, getUserLayerError — текст', async () => {
    getUserLayerMock.mockRejectedValueOnce(new Error('boom'));
    const store = await freshStore();
    await store.loadUserLayer('u1');
    expect(store.hasUserLayer('u1')).toBe(false);
    expect(store.getUserLayer('u1')).toBeNull();
    expect(store.getUserLayerError('u1')).toBe('boom');
  });

  it('commitUserLayer/rollbackUserLayer на null userId — no-op (защита от PUT /.../user/null)', async () => {
    const store = await freshStore();
    // не должно бросить
    expect(() => store.commitUserLayer(null as unknown as string, EMPTY_LAYER)).not.toThrow();
    expect(() => store.rollbackUserLayer(null as unknown as string, EMPTY_LAYER)).not.toThrow();
  });
});
