import { describe, expect, it } from 'vitest';
import { withThrowingLocalStorage } from './withThrowingLocalStorage';

const boom = (): never => { throw new Error('boom'); };

describe('withThrowingLocalStorage', () => {
  it('внутри чтение бросает, после — геттер восстановлен (в том числе при исключении и в async)', async () => {
    const before = Object.getOwnPropertyDescriptor(globalThis, 'localStorage');
    withThrowingLocalStorage(boom, () => { expect(() => globalThis.localStorage).toThrow('boom'); });
    expect(() => withThrowingLocalStorage(boom, () => { throw new Error('inner'); })).toThrow('inner');
    await withThrowingLocalStorage(boom, async () => { expect(() => globalThis.localStorage).toThrow('boom'); });
    await expect(withThrowingLocalStorage(boom, async () => { throw new Error('async'); })).rejects.toThrow('async');
    expect(Object.getOwnPropertyDescriptor(globalThis, 'localStorage')).toEqual(before);
  });

  // Страховка: без хелпера подмена протекает — чтение бросает и после «теста».
  it('сырая подмена без восстановления протекает — поэтому хелпер обязателен', () => {
    const orig = Object.getOwnPropertyDescriptor(globalThis, 'localStorage');
    Object.defineProperty(globalThis, 'localStorage', { configurable: true, get: boom });
    try {
      expect(() => globalThis.localStorage).toThrow('boom');
    } finally {
      if (orig) Object.defineProperty(globalThis, 'localStorage', orig);
      else delete (globalThis as { localStorage?: unknown }).localStorage;
    }
  });
});
