import { describe, it, expect } from 'vitest';
import type { ChatItem, PromptCacheInfo } from '../../types';
import { cacheNoteText, cacheTipText, cacheWarnMaxSec, expiringThresholdMs, promptCacheState, rewriteCostUsd } from '../promptCache';

describe('cacheTipText', () => {
  it('подпись состояния и остаток значением в колонке', () => {
    expect(cacheTipText('warm', false, 42 * 60_000)).toEqual({ label: 'Кэш тёплый', value: '42 мин' });
    expect(cacheTipText('expiring', false, 90_000)).toEqual({ label: 'Кэш скоро остынет', value: '2 мин' });
    expect(cacheTipText('warm', false)).toEqual({ label: 'Кэш тёплый', value: undefined });
  });

  it('остывший — без остатка', () => {
    expect(cacheTipText('cold', false, 0)).toEqual({ label: 'Кэш', value: 'остыл' });
  });

  it('пока идёт ход — без остатка при любом статусе', () => {
    for (const status of ['warm', 'expiring', 'cold'] as const)
      expect(cacheTipText(status, true, 42 * 60_000)).toEqual({ label: 'Кэш', value: 'идёт ход' });
  });
});

describe('cacheNoteText', () => {
  const rewrite = { tokens: '120.0k', usd: '$1.80', apiTariff: true };

  it('пока идёт ход — ни остатка, ни цены, при любом статусе', () => {
    for (const status of ['warm', 'expiring', 'cold'] as const) {
      const text = cacheNoteText(status, true, rewrite);
      expect(text).toBe('Обновится после хода.');
      expect(text).not.toContain('$');
    }
  });

  it('тёплый и остывающий — одна короткая строка', () => {
    expect(cacheNoteText('warm', false)).toBe('Ход до конца срока читает кэш в 10 раз дешевле. Каждый ход продлевает срок.');
    expect(cacheNoteText('expiring', false, rewrite)).toBe(cacheNoteText('warm', false));
  });

  it('остывший — объём и цена перезаписи', () => {
    expect(cacheNoteText('cold', false, rewrite)).toBe('Следующий ход перепишет ≈120.0k токенов, ≈$1.80 по API-тарифу.');
    expect(cacheNoteText('cold', false, { tokens: '5.0k', usd: '$0.10' })).toBe('Следующий ход перепишет ≈5.0k токенов, ≈$0.10.');
  });

  it('остывший без данных — без чисел', () => {
    expect(cacheNoteText('cold', false)).toBe('Следующий ход перепишет кэш заново.');
  });
});

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

  it('личный порог: 1h с порогом 20 мин остывает уже на 45-й минуте, 5m — по своему порогу', () => {
    const warn = { warn1hSec: 20 * 60, warn5mSec: 150 };
    const h = [result(pc({ ttl: '1h', anchorAtMs: T0 }), 120_000)];
    expect(promptCacheState(h, T0 + 45 * MIN).status).toBe('warm');           // без префа — тёплый
    expect(promptCacheState(h, T0 + 45 * MIN, warn).status).toBe('expiring'); // осталось 14 мин < 20
    const m = [result(pc({ ttl: '5m', anchorAtMs: T0 }), 9_000)];
    // Видимый срок 5m — 270 с: на 150-й секунде осталось 120 с
    expect(promptCacheState(m, T0 + 150_000).status).toBe('warm');           // дефолт 60 с
    expect(promptCacheState(m, T0 + 150_000, warn).status).toBe('expiring'); // 120 с < 150
    // null — как без префа
    expect(promptCacheState(h, T0 + 45 * MIN, null).status).toBe('warm');
  });
});

describe('expiringThresholdMs', () => {
  it('без префа — прежнее поведение: 5 мин у часового, пятая доля у 5-минутного', () => {
    expect(expiringThresholdMs(60 * MIN)).toBe(5 * MIN);
    expect(expiringThresholdMs(5 * MIN)).toBe(MIN);
    expect(expiringThresholdMs(60 * MIN, undefined)).toBe(5 * MIN);
  });

  it('с префом — секунды в миллисекунды', () => {
    expect(expiringThresholdMs(60 * MIN, 600)).toBe(600_000);
    expect(expiringThresholdMs(5 * MIN, 90)).toBe(90_000);
  });

  it('зажим границ: не меньше 30 с и не больше видимого срока (TTL − запас)', () => {
    expect(expiringThresholdMs(60 * MIN, 5)).toBe(30_000);
    expect(expiringThresholdMs(60 * MIN, 99_999)).toBe(3540_000);
    expect(expiringThresholdMs(5 * MIN, 999)).toBe(270_000);
    expect(cacheWarnMaxSec(60 * MIN)).toBe(3540);
    expect(cacheWarnMaxSec(5 * MIN)).toBe(270);
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
