/* eslint-disable react-hooks/rules-of-hooks --
   Хук гоняется вне React: модуль react замокан мини-раннером ниже, подписка на
   store и чтение снапшота держатся в rt, «ре-рендер» — повторный вызов хука. */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { useMediaQuery } from '../useMediaQuery';

// --- Фейк matchMedia: matches меняется вручную, dispatch() будит живых слушателей ---
interface FakeMql {
  matches: boolean;
  listeners: Set<() => void>;
  addEventListener: (type: string, cb: () => void) => void;
  removeEventListener: (type: string, cb: () => void) => void;
  dispatch: () => void;
}

function makeFakeMql(matches: boolean): FakeMql {
  const mql: FakeMql = {
    matches,
    listeners: new Set(),
    addEventListener: (_t, cb) => { mql.listeners.add(cb); },
    removeEventListener: (_t, cb) => { mql.listeners.delete(cb); },
    dispatch: () => { for (const l of [...mql.listeners]) l(); },
  };
  return mql;
}

// --- Мини-раннер useSyncExternalStore (окружение node: ни jsdom, ни testing-library) ---
// Слот держит подписку: повторный вызов хука (ре-рендер) не переподписывает, а
// отмонтирование — rt.unmount() — зовёт сохранённую отписку.
const rt = vi.hoisted(() => {
  interface Slot { unsub: (() => void) | null }
  const state = { current: null as { slots: Slot[]; i: number } | null };
  return {
    state,
    useSyncExternalStore(
      subscribe: (l: () => void) => () => void,
      getSnapshot: () => unknown,
      getServerSnapshot?: () => unknown,
    ) {
      const host = state.current;
      if (!host) return (getServerSnapshot ?? getSnapshot)();
      const slot = host.slots[host.i] ?? (host.slots[host.i] = { unsub: null });
      if (slot.unsub === null) slot.unsub = subscribe(() => { /* слушатель будит «ре-рендер» */ });
      host.i++;
      return getSnapshot();
    },
    unmount() {
      const host = state.current;
      state.current = null;
      if (host) for (const s of host.slots) s.unsub?.();
    },
  };
});

vi.mock('react', () => ({
  useCallback: (fn: unknown) => fn,
  useSyncExternalStore: rt.useSyncExternalStore,
}));

// Окружение node: window ставим сами, чтобы хук шёл по клиентской ветке
const g = globalThis as { window?: { matchMedia: (q: string) => FakeMql } };

beforeEach(() => {
  rt.state.current = { slots: [], i: 0 };
});
afterEach(() => {
  rt.unmount();
  delete g.window;
});

describe('useMediaQuery', () => {
  it('SSR (нет window) — false, без падения', () => {
    expect(useMediaQuery('(max-width: 339px)')).toBe(false);
  });

  it('отдаёт текущее значение matchMedia', () => {
    const mql = makeFakeMql(true);
    g.window = { matchMedia: () => mql };
    expect(useMediaQuery('(max-width: 339px)')).toBe(true);
  });

  it('реагирует на change: dispatch + ре-рендер меняет значение', () => {
    const mql = makeFakeMql(true);
    g.window = { matchMedia: () => mql };
    expect(useMediaQuery('(max-width: 339px)')).toBe(true);

    mql.matches = false;
    mql.dispatch(); // в React здесь компонент перерисовывается…
    rt.state.current!.i = 0; // …а в мини-раннере — повторный вызов хука
    expect(useMediaQuery('(max-width: 339px)')).toBe(false);
  });

  it('отписывается при размонтировании: после unmount dispatch слушателей не находит', () => {
    const mql = makeFakeMql(true);
    g.window = { matchMedia: () => mql };
    expect(useMediaQuery('(max-width: 339px)')).toBe(true);
    expect(mql.listeners.size).toBe(1);

    rt.unmount();
    expect(mql.listeners.size).toBe(0);
    mql.dispatch(); // не падает и никого не будит
  });

  it('окружение без matchMedia (node) — false, без падения', () => {
    g.window = { matchMedia: undefined as unknown as (q: string) => FakeMql };
    expect(useMediaQuery('(max-width: 339px)')).toBe(false);
  });
});
