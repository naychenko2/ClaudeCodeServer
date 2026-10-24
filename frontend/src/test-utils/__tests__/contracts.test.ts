import { describe, expect, it } from 'vitest';
import { assertAbortReachesFetch, assertKeyedBy, type KeyedStateProbe } from '../contracts';

// Фикстуры повторяют форму request() из lib/offline.ts: внутренний контроллер для таймаута.
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

describe('assertKeyedBy', () => {
  it('краснеет, если состояние не сбрасывается при смене ключа', () => {
    expect(() => assertKeyedBy(sharedProbe(), 'a', 'x')).toThrow('не привязано к ключу');
  });

  it('зеленеет, если состояние хранится по ключу', () => {
    expect(() => assertKeyedBy(keyedProbe(), 'a', 'x')).not.toThrow();
  });
});
