// Тесты стора включённости подсистем. Покрывает:
//   - дефолт (всё выключено до setAllSubsystems);
//   - замену всего набора через setAllSubsystems;
//   - оповещение подписчиков и корректность отписки;
//   - поведение useSubsystem через мини-раннер React (как в useSession.test).
//
// Источник истины — поле `subsystems` в /api/auth/me; App.tsx вызывает
// setAllSubsystems(me.subsystems) на старте. Здесь этот шаг воспроизводим
// вручную через setAllSubsystems, чтобы тест не зависел от сети.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import {
  SUBSYSTEMS,
  setAllSubsystems,
  isSubsystemEnabled,
  getAllSubsystems,
  subscribeSubsystems,
  useSubsystem,
  __resetSubsystems,
} from './subsystems';

// --- Мини-раннер React-хуков: useSyncExternalStore с локальным стейтом,
// чтобы тест хука не зависел от jsdom/testing-library. Контракт useSyncExternalStore:
// при изменении store между рендерами подписчик должен вызвать cb, и React
// перечитает getSnapshot. Имитируем этот цикл через Effect.
const rt = vi.hoisted(() => {
  interface Slot { deps?: unknown[]; cleanup?: (() => void) | void }
  interface Host { slots: Slot[]; i: number }
  const state = { current: null as Host | null };
  return {
    state,
    useEffect(effect: () => (() => void) | void, deps?: unknown[]) {
      const host = state.current;
      if (!host) return;
      const slot = host.slots[host.i++];
      const same = !!slot && !!slot.deps && !!deps && slot.deps.length === deps.length
        && slot.deps.every((d, k) => Object.is(d, deps[k]));
      if (same) return;
      slot?.cleanup?.();
      host.slots[host.i - 1] = { deps, cleanup: effect() };
    },
  };
});

vi.mock('react', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react')>();
  return {
    ...actual,
    useState: (init: unknown) => [typeof init === 'function' ? (init as () => unknown)() : init, () => { }],
    useCallback: (fn: unknown) => fn,
    useMemo: (fn: () => unknown) => fn(),
    useRef: (v: unknown) => ({ current: v }),
    useEffect: rt.useEffect,
    // Локальная реализация useSyncExternalStore: подписываемся на стор,
    // при изменении стора возвращаем свежий снимок через локальный ref.
    useSyncExternalStore: (subscribe: (cb: () => void) => () => void, get: () => unknown) => {
      const ref = { current: get() };
      rt.useEffect(() => {
        const unsub = subscribe(() => { ref.current = get(); });
        return unsub;
      }, [subscribe]);
      return ref.current;
    },
  };
});

function renderUseSubsystem(key: string): unknown {
  let captured: unknown;
  const Probe = () => {
    captured = useSubsystem(key as 'notes');
    return null;
  };
  renderToStaticMarkup(createElement(Probe));
  return captured;
}

beforeEach(() => {
  __resetSubsystems();
});

describe('subsystems — дефолт', () => {
  it('до setAllSubsystems все ключи выключены', () => {
    expect(isSubsystemEnabled('notes')).toBe(false);
    expect(getAllSubsystems()).toEqual({});
  });
});

describe('subsystems — setAllSubsystems', () => {
  it('включает переданные ключи', () => {
    setAllSubsystems({ notes: true });
    expect(isSubsystemEnabled('notes')).toBe(true);
    expect(getAllSubsystems()).toEqual({ notes: true });
  });

  it('явно выключенный ключ остаётся false', () => {
    setAllSubsystems({ notes: false });
    expect(isSubsystemEnabled('notes')).toBe(false);
  });

  it('перезаписывает набор целиком — старые ключи сбрасываются', () => {
    setAllSubsystems({ notes: true });
    setAllSubsystems({});
    expect(isSubsystemEnabled('notes')).toBe(false);
    expect(getAllSubsystems()).toEqual({});
  });

  it('getAllSubsystems возвращает копию, а не внутреннюю ссылку', () => {
    setAllSubsystems({ notes: true });
    const snap = getAllSubsystems();
    snap.notes = false;
    expect(isSubsystemEnabled('notes')).toBe(true);
  });
});

describe('subsystems — подписки', () => {
  it('оповещает подписчиков при замене набора', () => {
    const listener = vi.fn();
    const unsub = subscribeSubsystems(listener);
    setAllSubsystems({ notes: true });
    expect(listener).toHaveBeenCalledTimes(1);
    unsub();
  });

  it('отписка прекращает оповещения', () => {
    const listener = vi.fn();
    const unsub = subscribeSubsystems(listener);
    setAllSubsystems({ notes: true });
    expect(listener).toHaveBeenCalledTimes(1);
    unsub();
    setAllSubsystems({ notes: false });
    expect(listener).toHaveBeenCalledTimes(1);
  });

  it('несколько подписчиков получают события независимо', () => {
    const a = vi.fn();
    const b = vi.fn();
    const unsubA = subscribeSubsystems(a);
    const unsubB = subscribeSubsystems(b);
    setAllSubsystems({ notes: true });
    expect(a).toHaveBeenCalledTimes(1);
    expect(b).toHaveBeenCalledTimes(1);
    unsubA();
    setAllSubsystems({ notes: false });
    expect(a).toHaveBeenCalledTimes(1);
    expect(b).toHaveBeenCalledTimes(2);
    unsubB();
  });
});

describe('subsystems — useSubsystem', () => {
  it('возвращает false до setAllSubsystems', () => {
    expect(renderUseSubsystem('notes')).toBe(false);
  });

  it('возвращает true после setAllSubsystems({ notes: true })', () => {
    setAllSubsystems({ notes: true });
    expect(renderUseSubsystem('notes')).toBe(true);
  });

  it('возвращает SUBSYSTEMS.notes как единственный зарегистрированный ключ', () => {
    expect(SUBSYSTEMS.notes).toBe('notes');
  });
});
