import { describe, expect, it, vi } from 'vitest';

const me = vi.hoisted(() => ({ userId: 'u1' as string | null }));
vi.mock('../../lib/defaultPersona', () => ({ getMeSnapshot: () => ({ userId: me.userId }) }));

const { eraseOnUninstall, collectOnListLoaded } = await import('./pluginStorageLifecycle');
const { handleStorageCall } = await import('./hostOps/storage.js');

function makeStore(init: Record<string, string>) {
  const m = new Map(Object.entries(init));
  return {
    get length() { return m.size; },
    key: (i: number) => [...m.keys()][i] ?? null,
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => { m.set(k, v); },
    removeItem: (k: string) => { m.delete(k); },
    clear: vi.fn(() => m.clear()),
    keys: () => [...m.keys()].sort(),
  };
}

const seed = () => makeStore({
  'ccs.plugin.u1.acme/one.a': '1',
  'ccs.plugin.u1.acme/one.b': '2',
  'ccs.plugin.u1.acme/two.a': '3',
  'ccs.plugin.u2.acme/one.a': '4',
  cc_token: 'tok',
});

describe('eraseOnUninstall', () => {
  it('стирает ровно свой префикс, чужое и cc_token целы, clear() не вызван', () => {
    me.userId = 'u1';
    const s = seed();
    expect(eraseOnUninstall('acme/one', s)).toBe(2);
    expect(s.keys()).toEqual(['cc_token', 'ccs.plugin.u1.acme/two.a', 'ccs.plugin.u2.acme/one.a']);
    expect(s.clear).not.toHaveBeenCalled();
  });
  it('без владельца ничего не трогает', () => {
    me.userId = null;
    const s = seed();
    expect(eraseOnUninstall('acme/one', s)).toBe(0);
    expect(s.keys()).toHaveLength(5);
  });
});

describe('collectOnListLoaded', () => {
  it('удаляет ключи текущего владельца вне списка; чужой ownerId и cc_token не тронуты', () => {
    me.userId = 'u1';
    const s = seed();
    expect(collectOnListLoaded(['acme/two'], s)).toBe(2);
    expect(s.keys()).toEqual(['cc_token', 'ccs.plugin.u1.acme/two.a', 'ccs.plugin.u2.acme/one.a']);
    expect(s.clear).not.toHaveBeenCalled();
  });
});

// F-д2: при отключённом хранилище (Firefox dom.storage.enabled=false, приватный
// режим) сам геттер localStorage бросает SecurityError — typeof от этого не спасает.
// Lifecycle обязан отвечать тихим нулем: список плагинов не падает из-за сборки мусора.
describe('хранилище отключено (F-д2)', () => {
  const sec = () => Object.assign(new Error('sandboxed'), { name: 'SecurityError' });

  it('store бросает SecurityError на операции — тихий 0, ни один ключ не тронут', () => {
    me.userId = 'u1';
    const s = seed();
    s.getItem = () => { throw sec(); };
    s.removeItem = () => { throw sec(); };
    expect(collectOnListLoaded(['acme/two'], s)).toBe(0);
    expect(eraseOnUninstall('acme/one', s)).toBe(0);
    expect(s.keys()).toEqual(['cc_token', 'ccs.plugin.u1.acme/one.a', 'ccs.plugin.u1.acme/one.b', 'ccs.plugin.u1.acme/two.a', 'ccs.plugin.u2.acme/one.a']);
  });

  // Мутация (а): вернуть в lifecycle «typeof localStorage === 'undefined' ? null :
  // localStorage» без try/catch — чтение бросит SecurityError → этот тест красный.
  it('globalThis.localStorage бросает SecurityError: без store — тихий 0, без исключения', () => {
    me.userId = 'u1';
    const orig = Object.getOwnPropertyDescriptor(globalThis, 'localStorage');
    Object.defineProperty(globalThis, 'localStorage', {
      configurable: true,
      get() { throw sec(); },
    });
    try {
      expect(collectOnListLoaded(['acme/one'])).toBe(0);
      expect(eraseOnUninstall('acme/one')).toBe(0);
    } finally {
      if (orig) Object.defineProperty(globalThis, 'localStorage', orig);
      else delete (globalThis as { localStorage?: unknown }).localStorage;
    }
  });

  it('настоящая ошибка не прячется: пробрасывается, как раньше', () => {
    me.userId = 'u1';
    const s = seed();
    // GC работает только по ключам и removeItem — именно там и бросаем.
    s.removeItem = () => { throw new Error('boom'); };
    expect(() => collectOnListLoaded(['acme/two'], s)).toThrow('boom');
  });

  // F-д3 (минор 2): тихая ветка keyed по name, не по instanceof Error —
  // исключение из другого realm не instanceof Error. Мутация (б): вернуть
  // `e instanceof Error &&` в quietSecurity — этот тест красный.
  it('SecurityError не экземпляр Error (другой realm) — тоже тихий 0', () => {
    me.userId = 'u1';
    const s = seed();
    // Бросаем с key() — до частичной обработки в removePluginStorage: исключение
    // доходит до quietSecurity, и именно её проверка (по name, не instanceof)
    // здесь и проверяется.
    s.key = () => { throw { name: 'SecurityError', message: 'cross-realm' }; };
    expect(collectOnListLoaded(['acme/two'], s)).toBe(0);
    expect(s.keys()).toEqual(['cc_token', 'ccs.plugin.u1.acme/one.a', 'ccs.plugin.u1.acme/one.b', 'ccs.plugin.u1.acme/two.a', 'ccs.plugin.u2.acme/one.a']);
  });

  // F-д3 (минор 1): TypeError геттера — не SecurityError, не глотается:
  // настоящая ошибка видна, а не прячется в тихом нуле.
  it('геттер localStorage бросает TypeError — ошибка идёт вверх, не в тихий 0', () => {
    me.userId = 'u1';
    const orig = Object.getOwnPropertyDescriptor(globalThis, 'localStorage');
    Object.defineProperty(globalThis, 'localStorage', {
      configurable: true,
      get() { throw new TypeError('extension broke the getter'); },
    });
    try {
      expect(() => collectOnListLoaded(['acme/one'])).toThrow('extension broke the getter');
    } finally {
      if (orig) Object.defineProperty(globalThis, 'localStorage', orig);
      else delete (globalThis as { localStorage?: unknown }).localStorage;
    }
  });

  // F-д3 (минор 5): хранилище отключилось в середине цикла — часть ключей
  // реально удалена, счётчик обязан быть честным, а не нулем.
  it('частичный сбой: первый ключ удалён, затем SecurityError — счётчик = фактически удалённое', () => {
    me.userId = 'u1';
    const s = makeStore({
      'ccs.plugin.u1.acme/one.a': '1',
      'ccs.plugin.u1.acme/one.b': '2',
      cc_token: 'tok',
    });
    const origRemove = s.removeItem;
    let n = 0;
    s.removeItem = (k: string) => {
      if (n === 0) { origRemove(k); n++; return; }
      throw Object.assign(new Error('sandboxed'), { name: 'SecurityError' });
    };
    expect(eraseOnUninstall('acme/one', s)).toBe(1);
    expect(s.keys()).toEqual(['cc_token', 'ccs.plugin.u1.acme/one.b']);
  });
});

describe('отзыв права: данные остаются, повторная выдача возвращает их', () => {
  it('denied без права, те же значения после повторной выдачи', () => {
    me.userId = 'u1';
    const s = makeStore({});
    const call = (granted: string[], op: string, args: Record<string, unknown>) =>
      handleStorageCall({ op, args, granted, ownerId: 'u1', pluginId: 'acme/one', store: s } as never);
    call(['storage'], 'set', { key: 'k', value: { a: 1 } });
    expect(call([], 'get', { key: 'k' })).toMatchObject({ ok: false, error: { code: 'denied' } });
    expect(call(['storage'], 'get', { key: 'k' })).toEqual({ ok: true, data: { exists: true, value: { a: 1 } } });
  });
});
