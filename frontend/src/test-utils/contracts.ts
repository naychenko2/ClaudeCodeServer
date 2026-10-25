// Контрактные хелперы соседнего слоя: ловят класс дефектов, который юнит-тест слоя
// в одиночку не видит (отмена не дошла до сети, состояние не привязано к ключу).
import { vi } from 'vitest';

const FETCH_WAIT_MS = 1000;

/**
 * Отмена вызывающего доходит до fetch. `call` получает внешний signal и обязан
 * передать его тестируемому коду; хелпер подменяет глобальный fetch, отменяет
 * внешний контроллер и проверяет, что signal, с которым вызван fetch, отменился.
 * Образец дефекта — request() в lib/offline.ts, перетиравший переданный signal своим.
 */
export async function assertAbortReachesFetch(
  call: (signal: AbortSignal) => Promise<unknown>,
): Promise<void> {
  let fetchSignal: AbortSignal | null | undefined;
  let onFetch: () => void = () => {};
  const fetchCalled = new Promise<void>(resolve => { onFetch = resolve; });
  const fetchMock = vi.fn((_url: unknown, init?: RequestInit) => {
    fetchSignal = init?.signal;
    onFetch();
    return new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
    });
  });
  const originalFetch = globalThis.fetch;
  globalThis.fetch = fetchMock as unknown as typeof fetch;
  try {
    const outer = new AbortController();
    const pending = call(outer.signal).catch(() => undefined);
    // Код может ждать до вызова fetch (токен, IDB): ждём сам вызов, а не фиксированный микротик
    let timer: ReturnType<typeof setTimeout> | undefined;
    await Promise.race([
      fetchCalled,
      new Promise(resolve => { timer = setTimeout(resolve, FETCH_WAIT_MS); }),
    ]);
    clearTimeout(timer);
    if (!fetchSignal) throw new Error('fetch не вызван или вызван без signal');
    outer.abort();
    await Promise.race([pending, new Promise(resolve => setTimeout(resolve, 0))]);
    if (!fetchSignal.aborted) {
      throw new Error('отмена вызывающего не дошла до fetch: signal перетёрт');
    }
  } finally {
    globalThis.fetch = originalFetch;
  }
}

/** Щуп к состоянию, привязанному к ключу (хук, стор, компонент с key-пропом). */
export interface KeyedStateProbe<T> {
  setKey(key: string): void;
  write(value: T): void;
  read(): T | undefined;
}

/**
 * Состояние привязано к ключу: записанное под `key` значение не видно после смены
 * ключа на `otherKey`. Красный, если состояние переживает смену ключа (утечка
 * между чатами/проектами) или не возвращается при возврате к исходному ключу.
 */
export function assertKeyedBy<T>(state: KeyedStateProbe<T>, key: string, marker: T, otherKey = `${key}:other`): void {
  state.setKey(key);
  state.write(marker);
  state.setKey(otherKey);
  if (state.read() === marker) {
    throw new Error(`состояние не привязано к ключу: значение «${key}» пережило смену ключа на «${otherKey}»`);
  }
  state.setKey(key);
  if (state.read() !== marker) {
    throw new Error(`состояние не хранится по ключу: значение «${key}» потеряно после возврата с «${otherKey}»`);
  }
}
