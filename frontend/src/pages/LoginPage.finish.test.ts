// Вход и инвариант «токен в хранилище ⇒ id в том же хранилище» (FINAL итерации 3):
//  (а) /auth/me упал → вход не завершён, в обоих хранилищах нет токена и прежнего id;
//  (б) вход A при призраке id B в localStorage → в хранилище токен A и id A;
//  (в) токен в session без id там же, id B в local → id неизвестен, сверка владельца
//      не маскирует подмену токена (предикат даёт true только из-за «неизвестно»,
//      а чужой id из соседнего хранилища не подхватывается).
import { describe, it, expect, beforeEach, vi } from 'vitest';

const { me } = vi.hoisted(() => ({ me: vi.fn() }));
vi.mock('../lib/signalr', () => ({ onMessage: vi.fn(() => () => {}) }));
vi.mock('../lib/api', () => ({ api: { auth: { me } } }));

import { finishLogin, loginErrorMessage } from './LoginPage';
import { readStoredToken, readStoredUserId, OfflineError } from '../lib/offline';
import { setMeFromServer, clearMe, isTabOwnerConsistent } from '../lib/defaultPersona';
import type { Me } from '../types';

function storageStub(init: Record<string, string> = {}) {
  const m = new Map(Object.entries(init));
  return {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => { m.set(k, v); },
    removeItem: (k: string) => { m.delete(k); },
  };
}

const result = { token: 'токен-A', username: 'a', displayName: null };

describe('finishLogin', () => {
  beforeEach(() => { me.mockReset(); clearMe(); });

  it('(а) /auth/me упал → ошибка входа, в обоих хранилищах нет токена и прежнего id', async () => {
    const ses = storageStub({ cc_token: 'старый', cc_user_id: 'u-b', cc_role: 'user' });
    const loc = storageStub({ cc_token: 'общий', cc_user_id: 'u-b', cc_role: 'user' });
    vi.stubGlobal('sessionStorage', ses);
    vi.stubGlobal('localStorage', loc);
    me.mockRejectedValue(new Error('сеть'));

    await expect(finishLogin(result, true, 'http://x')).rejects.toThrow('Не удалось определить учётную запись');

    for (const s of [ses, loc]) {
      expect(s.getItem('cc_token')).toBeNull();
      expect(s.getItem('cc_user_id')).toBeNull();
      expect(s.getItem('cc_role')).toBeNull();
    }
  });

  it('(б) вход A при призраке id B в localStorage → токен A и id A в хранилище', async () => {
    const ses = storageStub();
    const loc = storageStub({ cc_user_id: 'u-b' });
    vi.stubGlobal('sessionStorage', ses);
    vi.stubGlobal('localStorage', loc);
    me.mockResolvedValue({ userId: 'u-a', role: 'user' });

    const auth = await finishLogin(result, true, 'http://x');

    expect(auth.id).toBe('u-a');
    expect(readStoredToken()).toBe('токен-A');
    expect(readStoredUserId()).toBe('u-a');
    expect(loc.getItem('cc_user_id')).toBe('u-a');
    expect(ses.getItem('cc_token')).toBeNull();
  });

  it('(в) токен в session без id там же, id B в local → id неизвестен, подмена не маскируется', () => {
    vi.stubGlobal('sessionStorage', storageStub({ cc_token: 'токен-A' }));
    vi.stubGlobal('localStorage', storageStub({ cc_user_id: 'u-b' }));
    setMeFromServer({ userId: 'u-b', username: 'b', role: 'user', defaultPersonaId: null } as Me);

    expect(readStoredToken()).toBe('токен-A');
    expect(readStoredUserId()).toBeNull();
    // Раньше id B подтягивался из local и предикат давал true при токене A; теперь id
    // неизвестен — в хранилище владельца нет, а не «совпал с B».
    expect(readStoredUserId()).not.toBe('u-b');
    expect(isTabOwnerConsistent()).toBe(true);
  });

  it('(г) /me на входе зовётся live: ответ из IDB-кэша не подставляет id прежнего владельца', async () => {
    vi.stubGlobal('sessionStorage', storageStub());
    vi.stubGlobal('localStorage', storageStub());
    me.mockResolvedValue({ userId: 'u-a', role: 'user' });

    await finishLogin(result, false, 'http://x');

    expect(me).toHaveBeenCalledWith({ live: true });
  });

  it('(д) обрыв сети на /me → OfflineError пробрасывается, экран покажет «Нет соединения»', async () => {
    vi.stubGlobal('sessionStorage', storageStub());
    vi.stubGlobal('localStorage', storageStub());
    me.mockRejectedValue(new OfflineError());

    const err = await finishLogin(result, true, 'http://x').catch((e: unknown) => e);

    expect(err).toBeInstanceOf(OfflineError);
    expect(loginErrorMessage(err)).toContain('Нет соединения');
  });

  it('(е) текст ошибки экрана: Error — его сообщение, прочее — запасной', () => {
    expect(loginErrorMessage(new Error('плохой пароль'))).toBe('плохой пароль');
    expect(loginErrorMessage('x')).toBe('Не удалось подключиться к серверу');
  });
});
