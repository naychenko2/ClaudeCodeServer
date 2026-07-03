import { describe, it, expect } from 'vitest';
import type { ChatItem, UsageInfo } from '../../types';
import { estimateContext, DEFAULT_CTX_WARN, DEFAULT_CTX_DANGER } from '../context';

// --- Фикстуры ---

const usage = (input: number, cacheRead = 0, cacheCreation = 0): UsageInfo =>
  ({ inputTokens: input, outputTokens: 100, cacheReadTokens: cacheRead, cacheCreationTokens: cacheCreation });

const result = (u?: UsageInfo): ChatItem =>
  ({ kind: 'result', subtype: 'success', durationMs: 100, numTurns: 1, usage: u });

const started = (model: string): ChatItem =>
  ({ kind: 'session_started', model, mode: 'default' });

const compact = (): ChatItem => ({ kind: 'compact_boundary', trigger: 'auto' });

describe('estimateContext: оценка токенов', () => {
  it('пустая лента → оценки нет, дефолтное окно 200k, уровень normal', () => {
    const est = estimateContext([]);
    expect(est).toMatchObject({ tokens: undefined, pct: undefined, fresh: false, level: 'normal', window: 200_000 });
  });

  it('токены = input + cacheRead + cacheCreation последнего result', () => {
    const est = estimateContext([result(usage(10_000, 5_000, 1_000))]);
    expect(est.tokens).toBe(16_000);
    expect(est.pct).toBe(8); // 16k / 200k
  });

  it('result с нулевым usage (компакт-ход) пропускается, берётся предыдущий содержательный', () => {
    const est = estimateContext([result(usage(50_000)), result(usage(0, 0, 0))]);
    expect(est.tokens).toBe(50_000);
  });

  it('compact_boundary позже последнего result → fresh, оценки нет', () => {
    const est = estimateContext([result(usage(150_000)), compact()]);
    expect(est.fresh).toBe(true);
    expect(est.tokens).toBeUndefined();
    expect(est.level).toBe('normal');
  });

  it('result после compact_boundary → оценка снова есть, fresh=false', () => {
    const est = estimateContext([compact(), result(usage(20_000))]);
    expect(est.fresh).toBe(false);
    expect(est.tokens).toBe(20_000);
  });

  it('pct ограничен сверху 100', () => {
    const est = estimateContext([result(usage(500_000))]);
    expect(est.pct).toBe(100);
  });
});

describe('estimateContext: окно модели', () => {
  it('модель из последнего session_started определяет окно (opus 4.8 → 1M)', () => {
    const est = estimateContext([started('claude-opus-4-8-20260101'), result(usage(200_000))]);
    expect(est.model).toBe('claude-opus-4-8-20260101');
    expect(est.window).toBe(1_000_000);
    expect(est.pct).toBe(20);
  });

  it('fallbackModel используется когда session_started нет', () => {
    const est = estimateContext([result(usage(100_000))], 'haiku');
    expect(est.window).toBe(200_000);
    expect(est.pct).toBe(50);
  });
});

describe('estimateContext: пороги подсветки (thresholdLevel)', () => {
  // Дефолтное окно 200k: N% = N*2000 токенов
  const atPct = (pct: number) => [result(usage(pct * 2000))];

  it(`ровно на warn (${DEFAULT_CTX_WARN}%) → warn`, () => {
    expect(estimateContext(atPct(DEFAULT_CTX_WARN)).level).toBe('warn');
  });

  it('чуть ниже warn → normal', () => {
    expect(estimateContext(atPct(DEFAULT_CTX_WARN - 1)).level).toBe('normal');
  });

  it(`ровно на danger (${DEFAULT_CTX_DANGER}%) → danger`, () => {
    expect(estimateContext(atPct(DEFAULT_CTX_DANGER)).level).toBe('danger');
  });

  it('между warn и danger → warn', () => {
    expect(estimateContext(atPct(DEFAULT_CTX_DANGER - 1)).level).toBe('warn');
  });

  it('пользовательские пороги переопределяют дефолт', () => {
    const custom = { warnPct: 30, dangerPct: 50 };
    expect(estimateContext(atPct(30), undefined, custom).level).toBe('warn');
    expect(estimateContext(atPct(50), undefined, custom).level).toBe('danger');
    expect(estimateContext(atPct(29), undefined, custom).level).toBe('normal');
  });
});
