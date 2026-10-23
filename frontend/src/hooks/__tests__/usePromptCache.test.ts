/* eslint-disable react-hooks/rules-of-hooks --
   Хук гоняется вне React: модуль react замокан мини-раннером ниже, слоты useState/
   useMemo/ useEffect держит host, «ре-рендер» — повторный render(). */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { ChatItem } from '../../types';
import type { PromptCacheWire } from '../../lib/promptCache';

// Тесты: пустое состояние до первого result'а с кэшем, состояние по result'у,
// переход warm → cooling → cold по таймеру, заморозка ленты на стрим-дельты хода.

// --- Мини-раннер React-хуков (окружение node: ни jsdom, ни testing-library нет) ---
// useState держит значение в слоте: setNow из эффекта обновляет его, следующий
// render() его видит. useMemo и useEffect не перезапускаются при тех же deps.
const rt = vi.hoisted(() => {
  interface Slot {
    value?: unknown;
    deps?: unknown[];
    memo?: unknown;
    hasMemo?: boolean;
    cleanup?: (() => void) | void;
  }
  interface Host { slots: Slot[]; i: number }
  const state = { current: null as Host | null };

  const sameDeps = (a?: unknown[], b?: unknown[]) =>
    !!a && !!b && a.length === b.length && a.every((d, k) => Object.is(d, b[k]));

  return {
    state,
    useState(init: unknown) {
      const host = state.current;
      if (!host) return [typeof init === 'function' ? (init as () => unknown)() : init, () => { }];
      const idx = host.i++;
      const slot = host.slots[idx] ?? (host.slots[idx] = {});
      if (slot.value === undefined) slot.value = typeof init === 'function' ? (init as () => unknown)() : init;
      return [slot.value, (v: unknown) => { slot.value = typeof v === 'function' ? v(slot.value) : v; }];
    },
    useMemo(fn: () => unknown, deps?: unknown[]) {
      const host = state.current;
      if (!host) return fn();
      const idx = host.i++;
      const slot = host.slots[idx] ?? (host.slots[idx] = {});
      if (slot.hasMemo && sameDeps(slot.deps, deps)) return slot.memo;
      slot.deps = deps;
      slot.memo = fn();
      slot.hasMemo = true;
      return slot.memo;
    },
    useEffect(effect: () => (() => void) | void, deps?: unknown[]) {
      const host = state.current;
      if (!host) return;
      const idx = host.i++;
      const slot = host.slots[idx];
      if (slot && sameDeps(slot.deps, deps)) return;
      slot?.cleanup?.();
      host.slots[idx] = { deps, cleanup: effect() };
    },
  };
});

vi.mock('react', () => ({
  useState: rt.useState,
  useMemo: rt.useMemo,
  useEffect: rt.useEffect,
}));

// Окружение node: window и document стабим сами — хук тикает через window.setTimeout
// и слушает visibilitychange.
(globalThis as unknown as { window: { setTimeout: (fn: () => void, ms: number) => number; clearTimeout: (t: number) => void } }).window = {
  setTimeout: (fn: () => void, ms: number) => setTimeout(fn, ms),
  clearTimeout: (t: number) => clearTimeout(t),
};
(globalThis as unknown as { document: { visibilityState: string; addEventListener: (t: string, h: () => void) => void; removeEventListener: (t: string, h: () => void) => void } }).document = {
  visibilityState: 'visible',
  addEventListener: () => { },
  removeEventListener: () => { },
};

const { usePromptCache, usePromptCacheItems } = await import('../usePromptCache');

const ANCHOR = 1_700_000_000_000;
const MIN = 60_000;

const resultItem = (promptCache?: PromptCacheWire): ChatItem =>
  ({ kind: 'result', subtype: 'success', durationMs: 1000, numTurns: 1, promptCache } as ChatItem);

const textItem = (t: string): ChatItem => ({ kind: 'text', text: t } as ChatItem);

const cache = (over: Partial<PromptCacheWire> = {}): PromptCacheWire =>
  ({ readTokens: 1000, writtenTokens: 500, anchorAtMs: ANCHOR, ttl: '1h', missReason: 'none', ...over });

// Монтируем «компонент» с хуком: render(items) — чтение состояния с пересчётом
// по новым deps, unmount() — снятие таймеров.
function mount() {
  const host = { slots: [] as Array<{ deps?: unknown[]; memo?: unknown; hasMemo?: boolean; cleanup?: (() => void) | void; }>, i: 0 };
  rt.state.current = host;
  return {
    render: (items: ChatItem[]) => { host.i = 0; return usePromptCache(items); },
    unmount: () => {
      rt.state.current = null;
      for (const s of host.slots) s.cleanup?.();
    },
  };
}

// Один тик: таймер будит setNow, «ре-рендер» пересчитывает состояние
function tick(m: ReturnType<typeof mount>, items: ChatItem[]) {
  vi.advanceTimersByTime(MIN + 50);
  return m.render(items);
}

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(ANCHOR);
});

afterEach(() => {
  vi.useRealTimers();
});

describe('usePromptCache — пустое состояние', () => {
  it("до первого result'а с данными кэша индикатора нет", () => {
    const m = mount();
    expect(m.render([])).toBeNull();
    expect(m.render([textItem('привет'), resultItem()])).toBeNull();
    m.unmount();
  });

  it('последний result без кэша — индикатора нет, даже если раньше он был', () => {
    const m = mount();
    expect(m.render([resultItem(cache())])).not.toBeNull();
    expect(m.render([resultItem(cache()), resultItem()])).toBeNull();
    m.unmount();
  });
});

describe("usePromptCache — состояние по result'у и переходы по таймеру", () => {
  it('result с кэшем даёт тёплое состояние с полным остатком', () => {
    const m = mount();
    const items = [resultItem(cache())];
    const s = m.render(items);
    expect(s?.status).toBe('warm');
    expect(s?.remainingMs).toBe(60 * MIN);
    m.unmount();
  });

  it("новый result' с новым якорем пересчитывает остаток", () => {
    const m = mount();
    const s1 = m.render([resultItem(cache())]);
    expect(s1?.remainingMs).toBe(60 * MIN);
    const items = [resultItem(cache()), textItem('ход'), resultItem(cache({ anchorAtMs: ANCHOR + 10 * MIN, ttl: '5m' }))];
    const s2 = m.render(items);
    expect(s2?.ttl).toBe('5m');
    expect(s2?.remainingMs).toBe(5 * MIN);
    m.unmount();
  });

  it('таймер двигает часы: тёплый → остывает → холодный, после остывания тиков нет', () => {
    const m = mount();
    const items = [resultItem(cache())];
    let s = m.render(items);
    expect(s?.status).toBe('warm');
    // 55 минут по минутным тикам — остаток 4,95 мин, ниже пяиминутного порога
    for (let i = 0; i < 55; i++) s = tick(m, items);
    expect(s?.status).toBe('cooling');
    // до нуля остатка — ещё ~5 минут тиков
    for (let i = 0; i < 5; i++) s = tick(m, items);
    expect(s?.status).toBe('cold');
    expect(s?.remainingMs).toBe(0);
    // холодный — таймера нет: ещё один тик ничего не двигает
    vi.advanceTimersByTime(10 * MIN);
    expect(m.render(items)).toEqual(s);
    m.unmount();
  });
});

describe('usePromptCacheItems — заморозка на время хода', () => {
  it('пока идёт ход, дельты не попадают в ленту для индикатора', () => {
    const host = { slots: [] as Array<{ value?: unknown }>, i: 0 };
    rt.state.current = host;
    const items = [resultItem(cache())];
    usePromptCacheItems(items, true); // первый рендер — «до хода»
    const withDeltas = [...items, textItem('…'), textItem('…')];
    host.i = 0;
    expect(usePromptCacheItems(withDeltas, true)).toBe(items); // та же ссылка: пересчёта нет
    host.i = 0;
    expect(usePromptCacheItems(withDeltas, false)).toBe(withDeltas); // ход кончился — лента живая
    rt.state.current = null;
  });
});
