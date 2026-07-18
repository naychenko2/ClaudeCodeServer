import { describe, it, expect } from 'vitest';
import type { ChatItem, UsageInfo } from '../../types';
import { estimateContext, DEFAULT_CTX_WARN, DEFAULT_CTX_DANGER } from '../context';

// --- Фикстуры ---

const usage = (input: number, cacheRead = 0, cacheCreation = 0): UsageInfo =>
  ({ inputTokens: input, outputTokens: 100, cacheReadTokens: cacheRead, cacheCreationTokens: cacheCreation });

// contextTokens — размер контекста последнего запроса хода (то, по чему считается оценка).
// usage задаётся отдельно: он кумулятивен за ход и на оценку влиять не должен.
const result = (contextTokens?: number, u?: UsageInfo): ChatItem =>
  ({ kind: 'result', subtype: 'success', durationMs: 100, numTurns: 1, usage: u, contextTokens });

const started = (model: string): ChatItem =>
  ({ kind: 'session_started', model, mode: 'default' });

const compact = (): ChatItem => ({ kind: 'compact_boundary', trigger: 'auto' });

describe('estimateContext: оценка токенов', () => {
  it('пустая лента → оценки нет, дефолтное окно 200k, уровень normal', () => {
    const est = estimateContext([]);
    expect(est).toMatchObject({ tokens: undefined, pct: undefined, fresh: false, level: 'normal', window: 200_000 });
  });

  it('токены = contextTokens последнего result', () => {
    const est = estimateContext([result(16_000)]);
    expect(est.tokens).toBe(16_000);
    expect(est.pct).toBe(8); // 16k / 200k
  });

  it('result без contextTokens (компакт-ход) пропускается, берётся предыдущий содержательный', () => {
    const est = estimateContext([result(50_000), result(undefined)]);
    expect(est.tokens).toBe(50_000);
  });

  it('compact_boundary позже последнего result → fresh, оценки нет', () => {
    const est = estimateContext([result(150_000), compact()]);
    expect(est.fresh).toBe(true);
    expect(est.tokens).toBeUndefined();
    expect(est.level).toBe('normal');
  });

  it('result после compact_boundary → оценка снова есть, fresh=false', () => {
    const est = estimateContext([compact(), result(20_000)]);
    expect(est.fresh).toBe(false);
    expect(est.tokens).toBe(20_000);
  });

  it('pct ограничен сверху 100', () => {
    const est = estimateContext([result(500_000)]);
    expect(est.pct).toBe(100);
  });
});

// Регрессия: раньше оценка складывалась из usage самого result, а тот суммирует ВСЕ
// запросы хода (шаги tool-лупа + сабагенты). Ход с 29 тул-вызовами показывал «1.06M»
// при реальных ~60k контекста. Оценка должна брать только contextTokens.
describe('estimateContext: usage не влияет на оценку (регрессия)', () => {
  it('раздутый кумулятивный usage игнорируется, берётся contextTokens', () => {
    const est = estimateContext([result(60_000, usage(15_515, 989_994, 57_266))]);
    expect(est.tokens).toBe(60_000);
    expect(est.pct).toBe(30); // 60k / 200k, а не 100% от 1.06M
  });

  it('старая история (usage есть, contextTokens нет) → оценки нет, а не кривое число', () => {
    const est = estimateContext([result(undefined, usage(15_515, 989_994, 57_266))]);
    expect(est.tokens).toBeUndefined();
    expect(est.pct).toBeUndefined();
    expect(est.level).toBe('normal');
  });
});

describe('estimateContext: окно модели', () => {
  it('модель из последнего session_started определяет окно (opus 4.8 → 1M)', () => {
    const est = estimateContext([started('claude-opus-4-8-20260101'), result(200_000)]);
    expect(est.model).toBe('claude-opus-4-8-20260101');
    expect(est.window).toBe(1_000_000);
    expect(est.pct).toBe(20);
  });

  it('fallbackModel используется когда session_started нет', () => {
    const est = estimateContext([result(100_000)], 'haiku');
    expect(est.window).toBe(200_000);
    expect(est.pct).toBe(50);
  });
});

describe('estimateContext: пороги подсветки (thresholdLevel)', () => {
  // Дефолтное окно 200k: N% = N*2000 токенов
  const atPct = (pct: number) => [result(pct * 2000)];

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
