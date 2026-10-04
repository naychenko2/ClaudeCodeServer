import { beforeEach, describe, expect, it } from 'vitest';

// Окружение node — localStorage нет; мокаем минимальную реализацию на Map
const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => store.get(k) ?? null,
  setItem: (k: string, v: string) => { store.set(k, v); },
  removeItem: (k: string) => { store.delete(k); },
  clear: () => store.clear(),
  key: () => null,
  length: 0,
} as Storage;
import { DEFAULT_INPUTS, inputsKey, readInputs, writeInputs } from './inputs';
import { trimSteps } from './run';

beforeEach(() => {
  localStorage.clear();
});

describe('старые входы в браузере', () => {
  it('входы вне белого списка сервера — в браузере на свою нить, входы из списка — нет', () => {
    const ka = inputsKey('p1', 's1', 'a');
    const kb = inputsKey('p1', 's1', 'b');
    writeInputs(ka, { ...DEFAULT_INPUTS, language: 'ru', voiceModelPath: 'v.pth', trim: { ...DEFAULT_INPUTS.trim, start: 3 } });
    expect(readInputs(ka).language).toBe('');
    expect(readInputs(ka).voiceModelPath).toBe('v.pth');
    expect(readInputs(ka).trim).toEqual({ ...DEFAULT_INPUTS.trim, start: 3 });
    expect(readInputs(kb)).toEqual(DEFAULT_INPUTS);
    expect(inputsKey('p1', 's1', null)).not.toBe(inputsKey('p1', 's2', null));
  });
});

describe('правки обрезки', () => {
  it('по порядку: кусок, громкость, нормализация', () => {
    expect(trimSteps({ start: 1, end: null, gainDb: -3, fadeIn: 0, fadeOut: 2, normalize: true, format: 'mp3' })).toEqual([
      { op: 'trim', startSec: 1, endSec: null, format: 'mp3' },
      { op: 'gainFade', gainDb: -3, fadeInSec: 0, fadeOutSec: 2, format: 'mp3' },
      { op: 'normalize', format: 'mp3' },
    ]);
    expect(trimSteps(DEFAULT_INPUTS.trim)).toEqual([]);
  });
});
