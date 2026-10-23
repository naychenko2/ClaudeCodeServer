/* eslint-disable react-hooks/rules-of-hooks --
   Хук гоняется вне React мини-раннером: useState/useMemo/useEffect держит host. */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { PromptCacheState } from '../../lib/promptCache';

// Тест F-1b-r2: смена состояния сбрасывает часы — после долгого простоя новый якорь
// считается от «сейчас», а не от часов, застывших на холодном состоянии.
const rt = vi.hoisted(() => {
  interface Slot { value?: unknown; deps?: unknown[]; memo?: unknown; hasMemo?: boolean; cleanup?: (() => void) | void }
  const state = { current: null as { slots: Slot[]; i: number } | null };
  const same = (a?: unknown[], b?: unknown[]) => !!a && !!b && a.length === b.length && a.every((d, k) => Object.is(d, b[k]));
  return {
    state,
    useState(init: unknown) {
      const host = state.current!;
      const slot = host.slots[host.i++] ??= {};
      if (slot.value === undefined) slot.value = init;
      return [slot.value, (v: unknown) => { slot.value = typeof v === 'function' ? (v as (p: unknown) => unknown)(slot.value) : v; }];
    },
    useMemo(fn: () => unknown, deps?: unknown[]) {
      const slot = state.current!.slots[state.current!.i++] ??= {};
      if (slot.hasMemo && same(slot.deps, deps)) return slot.memo;
      slot.deps = deps; slot.memo = fn(); slot.hasMemo = true;
      return slot.memo;
    },
    useEffect(effect: () => (() => void) | void, deps?: unknown[]) {
      const host = state.current!;
      const idx = host.i++;
      const slot = host.slots[idx];
      if (slot && same(slot.deps, deps)) return;
      slot?.cleanup?.();
      host.slots[idx] = { deps, cleanup: effect() };
    },
  };
});

vi.mock('react', () => ({ useState: rt.useState, useMemo: rt.useMemo, useEffect: rt.useEffect }));
(globalThis as unknown as { window: unknown }).window = {
  setTimeout: (fn: () => void, ms: number) => setTimeout(fn, ms),
  clearTimeout: (t: number) => clearTimeout(t),
};

const { useLiveNow } = await import('../useLiveNow');

const T0 = 1_700_000_000_000;
const MIN = 60_000;
const st = (anchorAtMs: number): PromptCacheState => ({
  status: 'warm', ttl: '1h', anchorAtMs, expiresAt: anchorAtMs + 60 * MIN,
  remainingMs: 60 * MIN, readTokens: 1, writtenTokens: 1,
});

function mount() {
  const host = { slots: [] as unknown[], i: 0 };
  rt.state.current = host as never;
  return {
    render: (s: PromptCacheState | null) => { host.i = 0; return useLiveNow(s); },
    unmount: () => { for (const x of host.slots as { cleanup?: () => void }[]) x?.cleanup?.(); rt.state.current = null; },
  };
}

beforeEach(() => { vi.useFakeTimers(); vi.setSystemTime(T0); });
afterEach(() => { vi.useRealTimers(); });

describe('useLiveNow — часы при смене состояния', () => {
  it('холодное → тёплое после простоя: now обновляется сразу, остаток по новому якорю', () => {
    const m = mount();
    const cold = st(T0 - 120 * MIN);
    expect(m.render(cold)).toBe(T0);
    // Простой 35 минут без тиков (у холодного таймера нет), затем приходит result
    vi.setSystemTime(T0 + 35 * MIN);
    const warm = st(T0 + 35 * MIN);
    const now = m.render(warm);
    expect(now).toBe(T0 + 35 * MIN);
    expect(warm.expiresAt - now).toBe(60 * MIN);
    m.unmount();
  });

  it('то же состояние — now не пересчитывается без тика', () => {
    const m = mount();
    const s = st(T0);
    const a = m.render(s);
    vi.setSystemTime(T0 + 10_000);
    expect(m.render(s)).toBe(a);
    m.unmount();
  });
});
