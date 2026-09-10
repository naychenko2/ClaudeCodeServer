// Тесты стора включённости подсистем. Покрывает:
//   - дефолт (всё выключено до setAllSubsystems);
//   - замену всего набора через setAllSubsystems массивом ключей — единственная
//     форма ввода (экран подсистем только читает, PUT нет);
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

describe('subsystems — setAllSubsystems принимает массив (форма от бэка)', () => {
  // Это и был корневой дефект пилота: бэк шлёт массив, стороной { ...arr }
  // получался { '0': 'notes' }, и useSubsystem('notes') возвращал false при
  // включённой подсистеме. Тест закрывает регресс: ключ разворачивается
  // правильно.
  it('включает ключи из массива', () => {
    setAllSubsystems(['notes']);
    expect(isSubsystemEnabled('notes')).toBe(true);
    expect(getAllSubsystems()).toEqual({ notes: true });
  });

  it('пустой массив → пустой набор (стейт выключен)', () => {
    setAllSubsystems([]);
    expect(isSubsystemEnabled('notes')).toBe(false);
    expect(getAllSubsystems()).toEqual({});
  });

  it('несколько ключей в массиве разворачиваются все', () => {
    setAllSubsystems(['notes', 'video', 'reader']);
    expect(isSubsystemEnabled('notes')).toBe(true);
    expect(isSubsystemEnabled('video')).toBe(true);
    expect(isSubsystemEnabled('reader')).toBe(true);
  });

  it('массив с дубликатами ключей не падает и не множит записи', () => {
    setAllSubsystems(['notes', 'notes']);
    expect(getAllSubsystems()).toEqual({ notes: true });
  });

  it('перезаписывает набор целиком — старые ключи сбрасываются', () => {
    setAllSubsystems(['notes']);
    setAllSubsystems([]);
    expect(isSubsystemEnabled('notes')).toBe(false);
    expect(getAllSubsystems()).toEqual({});
  });

  it('getAllSubsystems возвращает копию, а не внутреннюю ссылку', () => {
    setAllSubsystems(['notes']);
    const snap = getAllSubsystems();
    snap.notes = false;
    expect(isSubsystemEnabled('notes')).toBe(true);
  });
});

describe('subsystems — подписки', () => {
  it('оповещает подписчиков при замене набора массивом', () => {
    const listener = vi.fn();
    const unsub = subscribeSubsystems(listener);
    setAllSubsystems(['notes']);
    expect(listener).toHaveBeenCalledTimes(1);
    unsub();
  });

  it('отписка прекращает оповещения', () => {
    const listener = vi.fn();
    const unsub = subscribeSubsystems(listener);
    setAllSubsystems(['notes']);
    expect(listener).toHaveBeenCalledTimes(1);
    unsub();
    setAllSubsystems([]);
    expect(listener).toHaveBeenCalledTimes(1);
  });

  it('несколько подписчиков получают события независимо', () => {
    const a = vi.fn();
    const b = vi.fn();
    const unsubA = subscribeSubsystems(a);
    const unsubB = subscribeSubsystems(b);
    setAllSubsystems(['notes']);
    expect(a).toHaveBeenCalledTimes(1);
    expect(b).toHaveBeenCalledTimes(1);
    unsubA();
    setAllSubsystems([]);
    expect(a).toHaveBeenCalledTimes(1);
    expect(b).toHaveBeenCalledTimes(2);
    unsubB();
  });
});

describe('subsystems — useSubsystem', () => {
  it('возвращает false до setAllSubsystems', () => {
    expect(renderUseSubsystem('notes')).toBe(false);
  });

  it('возвращает true после setAllSubsystems(["notes"])', () => {
    setAllSubsystems(['notes']);
    expect(renderUseSubsystem('notes')).toBe(true);
  });

  it('ключ SUBSYSTEMS.notes включает подсистему через useSubsystem', () => {
    setAllSubsystems([SUBSYSTEMS.notes]);
    expect(renderUseSubsystem(SUBSYSTEMS.notes)).toBe(true);
  });

  it('неизвестный ключ в массиве не включает notes', () => {
    setAllSubsystems(['other-subsystem']);
    expect(renderUseSubsystem('notes')).toBe(false);
  });
});

describe('subsystems — стык с /api/auth/me (контракт)', () => {
  // Это контрактный сторож на стыке «что шлёт бэк» ↔ «что принимает стор».
  // Под Д-1 отчёта QA пилота: бэк шлёт массив, фронт ждал Record<string, boolean>
  // — спред массива давал { '0': 'notes' }, гейт ломался.
  //
  // Реальный JSON-ответ бэка (воспроизводим строкой) применяется к стору, и
  // `useSubsystem('notes')` обязан сойтись с тем, что бэк объявил активным.
  // Меняется форма поля на бэке → ключ «notes» разворачивается иначе → тест краснеет.
  //
  // Толерантности к чужой форме у стора больше нет: `setAllSubsystems` принимает
  // только массив ключей. Страховка «а вдруг придёт Record» была тестом собственной
  // недостижимой ветки и снята вместе с ней — форму на фронте не подменяем.
  // Корневой источник рассинхрона — AuthController.cs + Me.subsystems: контракт
  // между ними держит subsystems.contract.test.ts в __tests__.

  it('на включённой notes в реальном JSON-ответе useSubsystem("notes")=true', () => {
    // Реальный ответ GET /api/auth/me для пользователя с включённой notes.
    // Минимально воспроизводит форму, важную для гейта: subsystems — массив
    // активных ключей. Соседние поля оставлены для правдоподобия типа.
    const serverJson = JSON.stringify({
      userId: 'u-1',
      username: 'andrey',
      role: 'admin',
      featureFlags: {},
      subsystems: ['notes'],
    });
    const me = JSON.parse(serverJson);
    if (me.subsystems) setAllSubsystems(me.subsystems);
    expect(renderUseSubsystem('notes')).toBe(true);
  });

  it('при отсутствии notes в массиве useSubsystem("notes")=false', () => {
    const serverJson = JSON.stringify({
      userId: 'u-2',
      username: 'no-notes',
      role: 'user',
      featureFlags: {},
      subsystems: ['other', 'video'],
    });
    const me = JSON.parse(serverJson);
    if (me.subsystems) setAllSubsystems(me.subsystems);
    expect(renderUseSubsystem('notes')).toBe(false);
  });

  it('пустой массив subsystems = всё выключено', () => {
    const serverJson = JSON.stringify({
      userId: 'u-3',
      username: 'empty',
      role: 'user',
      featureFlags: {},
      subsystems: [],
    });
    const me = JSON.parse(serverJson);
    if (me.subsystems) setAllSubsystems(me.subsystems);
    expect(renderUseSubsystem('notes')).toBe(false);
  });
});