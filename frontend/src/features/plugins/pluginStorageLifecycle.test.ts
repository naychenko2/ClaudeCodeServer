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
