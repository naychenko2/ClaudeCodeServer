// Тесты секции «Расход» вкладки «Разбор» (FS-FR5): одна плитка «Расход» + строки
// токенов / общих чатов / моделей без цены / «данные с» / полноты цены в порядке записки
// §5; при stats.spend=null — одна строка «Расход по этой фиче не учитывался», без плиток
// и строк. Окружение node: renderToStaticMarkup.
//
// Мутации (краснеющие тесты — по фактическим прогонам в копии дерева, 10.11):
// (1) `if (!spend)` → `if (false)` с подменой null на нулевой spend (при null рисуются
//     нули) — краснеют «spend null: …» и «нулей нет: …»;
// (2) `spend.shared ? … : null` → `… : 'нет'` (строка «Доля общих чатов» рисуется и без
//     shared) — краснеет «shared null — строки нет»;
// (3) `spend.costPartial ? … : null` → `… : 'полная'` (строка «Цена неполная» при
//     costPartial=false) — краснеет «costPartial=false — строки нет».
// После отката каждой мутации — зелёные.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect } from 'vitest';
import { FeatureReviewSpend } from './FeatureReviewSpend';
import { formatDateTime } from './FeatureStatsBits';
import { formatTokens } from './FeatureStatsFormat';
import type { FeatureRun } from '../../types/featureRun';
import type { FeatureSpend, FeatureStats, FeatureTimeline } from '../../types/featureStats';

function mkRun(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'f-1',
    state: 'running',
    steps: [],
    reviews: [],
    qa: [],
    final: null,
    merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0,
    ready: false,
    ...over,
  };
}

function mkTotals(over: Partial<FeatureTimeline['totals']> = {}): FeatureTimeline['totals'] {
  return {
    elapsedMinutes: 0,
    workWallMinutes: 0,
    agentsSumMinutes: 0,
    notWorkingMinutes: 0,
    waitingMinutes: null,
    idleMinutes: null,
    waitingCount: null,
    longestWaitMinutes: null,
    errorCount: null,
    errorMinutes: null,
    ...over,
  };
}

// Фикстура расхода: полные поля, значения разные (формат разрядов виден на каждом пороге
// formatTokens: ≥1М → «2,4М», ≥1000 → «310 тыс.»).
function mkSpend(over: Partial<FeatureSpend> = {}): FeatureSpend {
  return {
    inputTokens: 2_400_000,
    outputTokens: 310_000,
    cacheReadTokens: 18_600_000,
    cacheCreationTokens: 1_100_000,
    costUsd: 14.9,
    costPartial: false,
    hiddenCostModels: [],
    from: '2026-11-10T15:48:00Z',
    shared: null,
    ...over,
  };
}

function mkStats(over: Partial<FeatureStats> = {}): FeatureStats {
  return {
    source: 'journal',
    truncated: false,
    timeline: { from: '2026-11-10T10:00:00Z', to: '2026-11-10T16:00:00Z', stages: [], segments: [], current: null, totals: mkTotals() },
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null,
    spend: null,
    ...over,
  };
}

const html = (stats: FeatureStats): string =>
  renderToStaticMarkup(createElement(FeatureReviewSpend, { run: mkRun(), stats }));

// Пары «ключ — значение» всех строк ReviewKv в порядке html (строки с value=null
// в html отсутствуют — ReviewKv их не рендерит).
function kvPairs(h: string): [string, string][] {
  return [...h.matchAll(/data-testid="review-kv-row"[^>]*><span[^>]*>([^<]*)<\/span><span[^>]*>([^<]*)<\/span>/g)]
    .map((m) => [m[1], m[2]] as [string, string]);
}

describe('плитка «Расход» (c1)', () => {
  it('spend есть — ровно одна плитка «Расход», остальных нет', () => {
    const h = html(mkStats({ spend: mkSpend() }));
    expect(h).toContain('data-testid="tile-spend"');
    expect(h).not.toContain('data-testid="tile-work"');
    expect(h).not.toContain('data-testid="tile-waiting"');
    expect(h).not.toContain('data-testid="tile-loops"');
  });

  it('плитка идёт первой, до строк', () => {
    const h = html(mkStats({ spend: mkSpend() }));
    expect(h.indexOf('data-testid="tile-spend"')).toBeLessThan(h.indexOf('data-testid="review-kv-row"'));
  });
});

describe('строки токенов (c1)', () => {
  it('«Вход», «Выход», «Чтение кэша», «Запись кэша» — formatTokens по полям, в порядке', () => {
    const pairs = kvPairs(html(mkStats({ spend: mkSpend() })));
    expect(pairs).toContainEqual(['Вход', formatTokens(2_400_000)]); // 2,4М
    expect(pairs).toContainEqual(['Выход', formatTokens(310_000)]); // 310 тыс.
    expect(pairs).toContainEqual(['Чтение кэша', formatTokens(18_600_000)]); // 19М
    expect(pairs).toContainEqual(['Запись кэша', formatTokens(1_100_000)]); // 1М
    const labels = pairs.map((p) => p[0]);
    const iIn = labels.indexOf('Вход');
    expect(iIn).toBeGreaterThanOrEqual(0);
    expect(labels.indexOf('Выход')).toBe(iIn + 1);
    expect(labels.indexOf('Чтение кэша')).toBe(iIn + 2);
    expect(labels.indexOf('Запись кэша')).toBe(iIn + 3);
  });
});

describe('доля общих чатов', () => {
  it('shared (journal) — «≈ N токенов (оценка, по журналу)», токены input+output shared', () => {
    const pairs = kvPairs(html(mkStats({
      spend: mkSpend({ shared: { inputTokens: 100_000, outputTokens: 20_000, cacheReadTokens: 0, cacheCreationTokens: 0, costUsd: 0.9, turns: 6, method: 'journal' } }),
    })));
    expect(pairs).toContainEqual(['Доля общих чатов', `≈ ${formatTokens(120_000)} токенов (оценка, по журналу)`]);
    // после строк токенов
    const labels = pairs.map((p) => p[0]);
    expect(labels.indexOf('Запись кэша')).toBeLessThan(labels.indexOf('Доля общих чатов'));
  });

  it('метод approx — «по задачам ≈» (не из stats.source)', () => {
    const pairs = kvPairs(html(mkStats({
      source: 'journal',
      spend: mkSpend({ shared: { inputTokens: 1000, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0, costUsd: 0, turns: 1, method: 'approx' } }),
    })));
    expect(pairs).toContainEqual(['Доля общих чатов', `≈ ${formatTokens(1000)} токенов (оценка, по задачам ≈)`]);
  });

  it('shared null — строки нет', () => {
    expect(kvPairs(html(mkStats({ spend: mkSpend() }))).some((p) => p[0] === 'Доля общих чатов')).toBe(false);
  });
});

describe('модели без цены', () => {
  it('hiddenCostModels — через запятую, подписи через modelGroupLabel (семейство Claude)', () => {
    const pairs = kvPairs(html(mkStats({
      spend: mkSpend({ costPartial: true, hiddenCostModels: ['claude-opus-4-8', 'local-qwen-7b'] }),
    })));
    // modelGroupLabel: версионный id Claude → семейство «Opus» (models.ts:304-311), сторонний — как есть.
    expect(pairs).toContainEqual(['Модели без цены', 'Opus, local-qwen-7b']);
  });

  it('пустой список — строки нет', () => {
    expect(kvPairs(html(mkStats({ spend: mkSpend() }))).some((p) => p[0] === 'Модели без цены')).toBe(false);
  });
});

describe('«данные с» и полнота цены', () => {
  it('«Данные с» — formatDateTime(spend.from), после «моделей без цены»', () => {
    const pairs = kvPairs(html(mkStats({
      spend: mkSpend({ costPartial: true, hiddenCostModels: ['local-qwen-7b'] }),
    })));
    expect(pairs).toContainEqual(['Данные с', formatDateTime('2026-11-10T15:48:00Z')]);
    const labels = pairs.map((p) => p[0]);
    expect(labels.indexOf('Модели без цены')).toBeLessThan(labels.indexOf('Данные с'));
  });

  it('from нечитаемый — строки нет', () => {
    expect(kvPairs(html(mkStats({ spend: mkSpend({ from: 'not-a-date' }) }))).some((p) => p[0] === 'Данные с')).toBe(false);
  });

  it('costPartial=false — строки «Цена неполная» нет (c1)', () => {
    expect(kvPairs(html(mkStats({ spend: mkSpend() }))).some((p) => p[0] === 'Цена неполная')).toBe(false);
  });

  it('costPartial=true — «Цена неполная» последняя строка', () => {
    const pairs = kvPairs(html(mkStats({ spend: mkSpend({ costPartial: true, hiddenCostModels: ['local-qwen-7b'] }) })));
    expect(pairs).toContainEqual(['Цена неполная', 'часть моделей без цены']);
    expect(pairs[pairs.length - 1][0]).toBe('Цена неполная');
  });
});

describe('общий порядок строк (записка §5)', () => {
  it('токены → общие чаты → модели без цены → «данные с» → «цена неполная»', () => {
    const pairs = kvPairs(html(mkStats({
      spend: mkSpend({
        costPartial: true,
        hiddenCostModels: ['local-qwen-7b'],
        shared: { inputTokens: 1000, outputTokens: 1000, cacheReadTokens: 0, cacheCreationTokens: 0, costUsd: 0.1, turns: 2, method: 'journal' },
      }),
    })));
    expect(pairs.map((p) => p[0])).toEqual([
      'Вход', 'Выход', 'Чтение кэша', 'Запись кэша',
      'Доля общих чатов', 'Модели без цены', 'Данные с', 'Цена неполная',
    ]);
  });
});

describe('нет расхода (c2)', () => {
  it('spend null — одна строка «Расход по этой фиче не учитывался», 0 плиток и 0 строк', () => {
    const h = html(mkStats());
    expect(h).toContain('Расход по этой фиче не учитывался');
    expect(h).not.toContain('data-testid="tile-spend"');
    expect(h).not.toContain('data-testid="stats-tiles"');
    expect(kvPairs(h).length).toBe(0);
  });

  it('нулей нет: ни «$0», ни «0 токенов», ни нулевых строк', () => {
    const h = html(mkStats());
    expect(h).not.toContain('$0');
    expect(h).not.toContain('0 токенов');
    for (const label of ['Вход', 'Выход', 'Чтение кэша', 'Запись кэша', 'Данные с']) {
      expect(h).not.toContain(label);
    }
  });
});
