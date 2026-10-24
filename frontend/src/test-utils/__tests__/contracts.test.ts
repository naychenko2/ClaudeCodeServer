import { afterEach, describe, expect, it, vi } from 'vitest';
import { assertAbortReachesFetch, assertKeyedBy, type KeyedStateProbe } from '../contracts';

// Фикстуры повторяют форму request() из lib/offline.ts (перетирание signal — строка 402): внутренний контроллер для таймаута.
// Красная — signal вызывающего перетирается внутренним (дефект), зелёная — контроллеры связаны.
async function requestOverwritingSignal(url: string, options?: RequestInit): Promise<Response> {
  const controller = new AbortController();
  return fetch(url, { ...options, signal: controller.signal });
}

async function requestLinkingSignal(url: string, options?: RequestInit): Promise<Response> {
  const controller = new AbortController();
  options?.signal?.addEventListener('abort', () => controller.abort());
  return fetch(url, { ...options, signal: controller.signal });
}

describe('assertAbortReachesFetch', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('краснеет, если signal вызывающего перетёрт', async () => {
    await expect(
      assertAbortReachesFetch(signal => requestOverwritingSignal('/x', { signal })),
    ).rejects.toThrow('не дошла до fetch');
  });

  it('зеленеет, если отмена доходит до fetch', async () => {
    await expect(
      assertAbortReachesFetch(signal => requestLinkingSignal('/x', { signal })),
    ).resolves.toBeUndefined();
  });

  it('зеленеет, если код ждёт перед вызовом fetch', async () => {
    await expect(
      assertAbortReachesFetch(async signal => {
        await new Promise(resolve => setTimeout(resolve, 20));
        return requestLinkingSignal('/x', { signal });
      }),
    ).resolves.toBeUndefined();
  });

  it('не снимает чужие vi.stubGlobal', async () => {
    vi.stubGlobal('ccsForeignStub', 42);
    try {
      await assertAbortReachesFetch(signal => requestLinkingSignal('/x', { signal }));
      expect((globalThis as Record<string, unknown>).ccsForeignStub).toBe(42);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('восстанавливает оригинальный fetch', async () => {
    const before = globalThis.fetch;
    await assertAbortReachesFetch(signal => requestLinkingSignal('/x', { signal }));
    expect(globalThis.fetch).toBe(before);
  });

  it('краснеет, если fetch вызван без signal', async () => {
    await expect(assertAbortReachesFetch(() => fetch('/x'))).rejects.toThrow('без signal');
  });
});

// Красный щуп держит одно значение независимо от ключа, зелёный хранит по ключу.
function sharedProbe(): KeyedStateProbe<string> {
  let value: string | undefined;
  return { setKey: () => {}, write: v => { value = v; }, read: () => value };
}

function keyedProbe(): KeyedStateProbe<string> {
  const byKey = new Map<string, string>();
  let current = '';
  return { setKey: k => { current = k; }, write: v => { byKey.set(current, v); }, read: () => byKey.get(current) };
}

// Щуп, который ничего не хранит: после возврата к ключу значения нет
function forgetfulProbe(): KeyedStateProbe<string> {
  return { setKey: () => {}, write: () => {}, read: () => undefined };
}

describe('assertKeyedBy', () => {
  it('краснеет, если щуп ничего не хранит', () => {
    expect(() => assertKeyedBy(forgetfulProbe(), 'a', 'x')).toThrow('не хранится по ключу');
  });

  it('краснеет, если состояние не сбрасывается при смене ключа', () => {
    expect(() => assertKeyedBy(sharedProbe(), 'a', 'x')).toThrow('не привязано к ключу');
  });

  it('зеленеет, если состояние хранится по ключу', () => {
    expect(() => assertKeyedBy(keyedProbe(), 'a', 'x')).not.toThrow();
  });
});
