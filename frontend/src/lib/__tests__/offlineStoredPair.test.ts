// Чтение/запись пары «токен + владелец»: закрепляем поведение после дельты итерации 3.
import { describe, it, expect, beforeEach, vi } from 'vitest';
import { readStoredToken, readStoredUserId, writeStoredToken } from '../offline';

function storageStub(init: Record<string, string> = {}) {
  const m = new Map(Object.entries(init));
  return {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => { m.set(k, v); },
    removeItem: (k: string) => { m.delete(k); },
  };
}

describe('readStoredToken', () => {
  beforeEach(() => { vi.unstubAllGlobals(); });

  it('мусор «null» в sessionStorage пропускается — берётся валидный токен из localStorage', () => {
    vi.stubGlobal('sessionStorage', storageStub({ cc_token: 'null', cc_user_id: 'u-мусор' }));
    vi.stubGlobal('localStorage', storageStub({ cc_token: 'общий', cc_user_id: 'u-общий' }));

    expect(readStoredToken()).toBe('общий');
    // пара читается из того же хранилища, откуда токен
    expect(readStoredUserId()).toBe('u-общий');
  });

  it('мусор в обоих хранилищах → токена нет', () => {
    vi.stubGlobal('sessionStorage', storageStub({ cc_token: 'undefined' }));
    vi.stubGlobal('localStorage', storageStub({ cc_token: 'null' }));

    expect(readStoredToken()).toBeNull();
  });
});

describe('writeStoredToken', () => {
  // Поведение оставлено как есть: id писатель не трогает — при смене пароля пара
  // сохраняется, потому что токен пишется в то хранилище, где уже лежит id.
  it('смена токена в sessionStorage не трогает id там же и не пишет в localStorage', () => {
    const ses = storageStub({ cc_token: 'старый', cc_user_id: 'u-a' });
    const loc = storageStub();
    vi.stubGlobal('sessionStorage', ses);
    vi.stubGlobal('localStorage', loc);

    writeStoredToken('новый');

    expect(readStoredToken()).toBe('новый');
    expect(readStoredUserId()).toBe('u-a');
    expect(loc.getItem('cc_token')).toBeNull();
  });
});
