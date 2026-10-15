import { describe, it, expect } from 'vitest';
import type { ChatItem, PromptCacheInfo } from '../../types';
import { promptCacheState, rewriteCostUsd } from '../promptCache';

const MIN = 60_000;
const T0 = 1_700_000_000_000;

const result = (promptCache?: PromptCacheInfo | null, contextTokens?: number): ChatItem =>
  ({ kind: 'result', subtype: 'success', durationMs: 100, numTurns: 1, contextTokens, promptCache });

const pc = (p: Partial<PromptCacheInfo>): PromptCacheInfo => ({ prefixTokens: 120_000, ...p });

describe('promptCacheState', () => {
  it('1h: на 50-й минуте тёплый, на 55-й остывает, на 61-й остыл', () => {
    const items = [result(pc({ ttl: '1h', anchorAtMs: T0 }), 120_000)];
    expect(promptCacheState(items, T0 + 50 * MIN).status).toBe('warm');
    const at55 = promptCacheState(items, T0 + 55 * MIN);
    expect(at55.status).toBe('expiring');
    expect(at55.remainingMs).toBe(4 * MIN); // минута запаса на время до первого токена
    const at61 = promptCacheState(items, T0 + 61 * MIN);
    expect(at61.status).toBe('cold');
    expect(at61.remainingMs).toBe(0);
    expect(at61.prefixTokens).toBe(120_000);
  });

  it('5m: на 2-й минуте тёплый, на 4-й остывает, на 6-й остыл', () => {
    const items = [result(pc({ ttl: '5m', anchorAtMs: T0 }), 9_000)];
    expect(promptCacheState(items, T0 + 2 * MIN).status).toBe('warm');
    expect(promptCacheState(items, T0 + 4 * MIN).status).toBe('expiring');
    expect(promptCacheState(items, T0 + 6 * MIN).status).toBe('cold');
  });

  it('старая история без поля → unknown', () => {
    const items = [result(undefined, 50_000), result(undefined, 60_000)];
    expect(promptCacheState(items, T0).status).toBe('unknown');
  });

  it('после хода стороннего провайдера (result без поля, но с contextTokens) → unknown', () => {
    const items = [result(pc({ ttl: '1h', anchorAtMs: T0 }), 50_000), result(null, 60_000)];
    expect(promptCacheState(items, T0 + MIN).status).toBe('unknown');
  });

  it('result компакт-хода без поля не сбрасывает якорь', () => {
    const items = [
      result(pc({ ttl: '1h', anchorAtMs: T0 }), 50_000),
      { kind: 'compact_boundary', trigger: 'manual' } as ChatItem,
      result(undefined, undefined),
    ];
    const s = promptCacheState(items, T0 + 10 * MIN);
    expect(s.status).toBe('warm');
    expect(s.anchorAtMs).toBe(T0);
  });

  it('вызов только на чтение наследует TTL от прошлой записи, якорь — свежий', () => {
    const items = [
      result(pc({ ttl: '1h', anchorAtMs: T0 }), 50_000),
      result(pc({ ttl: null, anchorAtMs: T0 + 30 * MIN }), 51_000),
    ];
    const s = promptCacheState(items, T0 + 70 * MIN);
    expect(s.ttl).toBe('1h');
    expect(s.anchorAtMs).toBe(T0 + 30 * MIN);
    expect(s.status).toBe('warm');
  });

  it('lastMiss — последний промах с человеческим текстом', () => {
    const items = [
      result(pc({ ttl: '1h', anchorAtMs: T0, missReason: 'model_changed', missedTokens: 90_000 }), 90_000),
      result(pc({ ttl: '1h', anchorAtMs: T0 + MIN }), 91_000),
    ];
    const s = promptCacheState(items, T0 + 2 * MIN);
    expect(s.lastMiss).toEqual({ reason: 'model_changed', tokens: 90_000, label: 'смена модели' });
  });
});

describe('rewriteCostUsd', () => {
  // База $5 за миллион входа: 1000 вход + 1000×5 выход + 100k×0,1 чтение + 10k×2 запись (1h) = 36k единиц
  const paid = (ttl: '1h' | '5m' | null): ChatItem => ({
    kind: 'result', subtype: 'success', durationMs: 100, numTurns: 1, contextTokens: 100_000,
    totalCostUsd: 36_000 * 5e-6,
    usage: { inputTokens: 1000, outputTokens: 1000, cacheReadTokens: 100_000, cacheCreationTokens: 10_000 },
    promptCache: pc({ ttl, anchorAtMs: T0 }),
  });

  it('базу берёт из стоимости хода, запись часового кэша — ×2', () => {
    expect(rewriteCostUsd([paid('1h')], '1h', 100_000)).toBeCloseTo(1.0, 6);
  });

  it('5-минутный кэш перезаписывается по ×1,25', () => {
    expect(rewriteCostUsd([paid('1h')], '5m', 100_000)).toBeCloseTo(0.625, 6);
  });

  it('ход без TTL считает запись по TTL кэша', () => {
    expect(rewriteCostUsd([paid(null)], '1h', 100_000)).toBeCloseTo(1.0, 6);
  });

  it('без хода со стоимостью — оценки нет', () => {
    expect(rewriteCostUsd([result(pc({ ttl: '1h', anchorAtMs: T0 }), 100_000)], '1h', 100_000)).toBeUndefined();
    expect(rewriteCostUsd([paid('1h')], '1h', 0)).toBeUndefined();
  });
});
