// Сторож «одна вкладка — один владелец»: расхождение владельца на storage / pageshow
// (bfcache) / visibilitychange → reload; пустой токен → выход; тот же владелец — тишина.
// Мутация: убрать actions.reload из check → тесты «чужой владелец» красные.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../idb', () => ({ idbGet: vi.fn(), idbSet: vi.fn() }));
vi.mock('../signalr', () => ({ onMessage: () => () => {} }));

function storageStub(init: Record<string, string> = {}) {
  const m = new Map(Object.entries(init));
  return {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => { m.set(k, v); },
    removeItem: (k: string) => { m.delete(k); },
  };
}

type Handler = (e: unknown) => void;

let wireTabOwnerGuard: typeof import('../tabOwnerGuard').wireTabOwnerGuard;
let setMeFromServer: typeof import('../defaultPersona').setMeFromServer;
let clearMe: typeof import('../defaultPersona').clearMe;
let winHandlers: Map<string, Handler>;
let docHandlers: Map<string, Handler>;
let visibility: string;
const reload = vi.fn();
const signOut = vi.fn();
let stop: () => void;

beforeEach(async () => {
  vi.resetModules();
  reload.mockReset();
  signOut.mockReset();
  winHandlers = new Map();
  docHandlers = new Map();
  visibility = 'visible';
  vi.stubGlobal('window', {
    addEventListener: (t: string, h: Handler) => { winHandlers.set(t, h); },
    removeEventListener: (t: string) => { winHandlers.delete(t); },
  });
  vi.stubGlobal('document', {
    addEventListener: (t: string, h: Handler) => { docHandlers.set(t, h); },
    removeEventListener: (t: string) => { docHandlers.delete(t); },
    get visibilityState() { return visibility; },
  });
  vi.stubGlobal('navigator', { onLine: true });
  vi.stubGlobal('sessionStorage', storageStub());
  ({ setMeFromServer, clearMe } = await import('../defaultPersona'));
  ({ wireTabOwnerGuard } = await import('../tabOwnerGuard'));
  // Вкладка показывает владельца u-a
  setMeFromServer({ userId: 'u-a', username: 'a', role: 'user' });
  stop = wireTabOwnerGuard({ reload, signOut });
});

afterEach(() => {
  stop();
  clearMe();
  vi.unstubAllGlobals();
});

const ownLocal = (id: string) => vi.stubGlobal('localStorage', storageStub({ cc_token: 'tok', cc_user_id: id }));

describe('wireTabOwnerGuard', () => {
  it('storage cc_user_id: чужой владелец → reload', () => {
    ownLocal('u-b');
    winHandlers.get('storage')!({ key: 'cc_user_id' });
    expect(reload).toHaveBeenCalledTimes(1);
    expect(signOut).not.toHaveBeenCalled();
  });

  it('storage cc_token и key=null (clear) тоже проверяют владельца', () => {
    ownLocal('u-b');
    winHandlers.get('storage')!({ key: 'cc_token' });
    winHandlers.get('storage')!({ key: null });
    expect(reload).toHaveBeenCalledTimes(2);
  });

  it('storage: тот же владелец → тишина', () => {
    ownLocal('u-a');
    winHandlers.get('storage')!({ key: 'cc_user_id' });
    expect(reload).not.toHaveBeenCalled();
    expect(signOut).not.toHaveBeenCalled();
  });

  it('storage по постороннему ключу не проверяет владельца', () => {
    ownLocal('u-b');
    winHandlers.get('storage')!({ key: 'cc_theme' });
    expect(reload).not.toHaveBeenCalled();
  });

  it('pageshow из bfcache (persisted) → reload; обычный pageshow — нет', () => {
    ownLocal('u-b');
    winHandlers.get('pageshow')!({ persisted: false });
    expect(reload).not.toHaveBeenCalled();
    winHandlers.get('pageshow')!({ persisted: true });
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('visibilitychange → visible: чужой владелец → reload; hidden — нет', () => {
    ownLocal('u-b');
    visibility = 'hidden';
    docHandlers.get('visibilitychange')!({});
    expect(reload).not.toHaveBeenCalled();
    visibility = 'visible';
    docHandlers.get('visibilitychange')!({});
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('свой sessionStorage-вход не страдает от чужого localStorage', () => {
    vi.stubGlobal('sessionStorage', storageStub({ cc_token: 'свой', cc_user_id: 'u-a' }));
    ownLocal('u-b');
    winHandlers.get('storage')!({ key: 'cc_user_id' });
    expect(reload).not.toHaveBeenCalled();
  });

  it('пустой токен → выход, а не reload', () => {
    vi.stubGlobal('localStorage', storageStub());
    winHandlers.get('storage')!({ key: 'cc_token' });
    expect(signOut).toHaveBeenCalledTimes(1);
    expect(reload).not.toHaveBeenCalled();
  });

  it('отписка снимает слушателей', () => {
    stop();
    expect(winHandlers.size).toBe(0);
    expect(docHandlers.size).toBe(0);
  });
});
