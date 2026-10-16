// Чистые функции графика «Выработка стенда, всего»: разрывы на null, «круглая»
// шкала, среднее без простоя, пик. Мутация «null → 0» должна покрасить тест на
// разрыв; «37 → 50» — на шкалу; «среднее с простоем» — на фильтр running>0.

import { describe, expect, it } from 'vitest';
import {
  agentsChartAriaLabel, avgAgentsOf, avgIssuedOf, avgPerStreamOf, avgStepMsOf, avgTokensPerStepOf,
  avgThroughputOf, breakRuns, buildAgentsSteps, buildIntervals,
  buildIssuedSegments, buildPerStreamSegments, buildStepMsSegments, buildTokensPerStepSegments,
  buildThroughputSegments, cacheHitAvg,
  chartAriaLabel, chartNow, evChartAriaLabel, formatAxisTps, intervalCountFor, intervalGrid,
  kvChartAriaLabel, maxAgentsOf, maxSeriesOf, maxStepMsOf, maxTokensPerStepOf, maxThroughputOf,
  niceAxisMax, peakPerStreamOf,
  peakThroughputOf, perStreamBreakRuns, preemptionTotal, shouldShowNoDataLabel,
  stepChartAriaLabel, tpChartAriaLabel, xForTime, yForThroughput,
  type ChartGeom, type Interval, type ThroughputSample,
} from './ThroughputChart';

// Геометрия как в компоненте (height 96, left 0, right 28)
const GEOM: ChartGeom = { width: 340, height: 96, left: 0, right: 28, top: 8, bottom: 18 };

describe('buildThroughputSegments: разрывы на null', () => {
  it('null в середине — два отдельных сегмента (ноль стороны не соединяет)', () => {
    const samples: ThroughputSample[] = [
      { t: 1000, throughput: 10, running: 1 },
      { t: 2000, throughput: null, running: null },   // разрыв
      { t: 3000, throughput: 20, running: 1 },
    ];
    const segs = buildThroughputSegments(samples, 0, 4000, 20, GEOM);
    expect(segs).toHaveLength(2);
    expect(segs[0]).toHaveLength(1);
    expect(segs[1]).toHaveLength(1);
  });

  it('без null — один непрерывный сегмент на все точки', () => {
    const samples: ThroughputSample[] = [
      { t: 1000, throughput: 10, running: 1 },
      { t: 2000, throughput: 20, running: 1 },
      { t: 3000, throughput: 30, running: 1 },
    ];
    const segs = buildThroughputSegments(samples, 0, 4000, 30, GEOM);
    expect(segs).toHaveLength(1);
    expect(segs[0]).toHaveLength(3);
  });

  it('один ненулевой пункт — один сегмент из одной точки', () => {
    const samples: ThroughputSample[] = [{ t: 2000, throughput: 15, running: 1 }];
    const segs = buildThroughputSegments(samples, 0, 4000, 15, GEOM);
    expect(segs).toHaveLength(1);
    expect(segs[0]).toHaveLength(1);
  });

  it('пустой вход — нет сегментов', () => {
    expect(buildThroughputSegments([], 0, 4000, 1, GEOM)).toHaveLength(0);
  });

  it('только null — нет сегментов (нули не рисуем)', () => {
    const samples: ThroughputSample[] = [
      { t: 1000, throughput: null, running: null },
      { t: 2000, throughput: null, running: null },
    ];
    expect(buildThroughputSegments(samples, 0, 4000, 1, GEOM)).toHaveLength(0);
  });

  it('несколько разрывов — сегментов ровно на число непрерывных прогонов', () => {
    const samples: ThroughputSample[] = [
      { t: 100, throughput: 5, running: 1 },
      { t: 200, throughput: 6, running: 1 },
      { t: 300, throughput: null, running: null },
      { t: 400, throughput: null, running: null },
      { t: 500, throughput: 7, running: 1 },
      { t: 600, throughput: 8, running: 1 },
      { t: 700, throughput: 9, running: 1 },
    ];
    const segs = buildThroughputSegments(samples, 0, 1000, 9, GEOM);
    expect(segs).toHaveLength(2);
    expect(segs[0]).toHaveLength(2);   // 5, 6
    expect(segs[1]).toHaveLength(3);   // 7, 8, 9
  });
});

describe('масштаб: y зависит от maxThroughput', () => {
  it('максимум — наверху (min y), ноль — на дне (max y)', () => {
    const top = yForThroughput(20, 20, GEOM);
    const mid = yForThroughput(10, 20, GEOM);
    const bottom = yForThroughput(0, 20, GEOM);
    expect(top).toBeLessThan(mid);
    expect(mid).toBeLessThan(bottom);
    expect(bottom).toBe(GEOM.height - GEOM.bottom);
    expect(top).toBe(GEOM.top);
  });

  it('значение выше max зажимается наверху', () => {
    expect(yForThroughput(40, 20, GEOM)).toBe(GEOM.top);
  });

  it('xForTime: tMin — левый край, tMax — правый край, середина — центр', () => {
    expect(xForTime(0, 0, 4000, GEOM)).toBe(GEOM.left);
    expect(xForTime(4000, 0, 4000, GEOM)).toBe(GEOM.width - GEOM.right);
    const mid = xForTime(2000, 0, 4000, GEOM);
    expect(mid).toBeGreaterThan(GEOM.left);
    expect(mid).toBeLessThan(GEOM.width - GEOM.right);
  });
});

describe('maxThroughputOf', () => {
  it('максимум выработки, null и 0 не влияют', () => {
    const samples: ThroughputSample[] = [
      { t: 1, throughput: 12, running: 1 },
      { t: 2, throughput: null, running: null },
      { t: 3, throughput: 40, running: 1 },
      { t: 4, throughput: 0, running: 0 },
    ];
    expect(maxThroughputOf(samples)).toBe(40);
  });

  it('пусто — 0', () => {
    expect(maxThroughputOf([])).toBe(0);
  });
});

describe('niceAxisMax: «круглая» шкала (1, 2, 5 × 10ⁿ, не меньше 10)', () => {
  it('0 → 10 (не 0: ось не схлопывается)', () => {
    expect(niceAxisMax(0)).toBe(10);
  });
  it('37 → 50 (ряд 1,2,5: 5×10=50 ≥ 37)', () => {
    expect(niceAxisMax(37)).toBe(50);
  });
  it('412 → 500', () => {
    expect(niceAxisMax(412)).toBe(500);
  });
  it('980 → 1000', () => {
    expect(niceAxisMax(980)).toBe(1000);
  });
  it('ровно 1000 → 1000', () => {
    expect(niceAxisMax(1000)).toBe(1000);
  });
  it('1500 → 2000', () => {
    expect(niceAxisMax(1500)).toBe(2000);
  });
  it('1 → 10 (не меньше 10)', () => {
    expect(niceAxisMax(1)).toBe(10);
  });
});

describe('formatAxisTps: подпись оси без единицы, ≥1000 — k', () => {
  it('0 → «0», 25 → «25», 50 → «50»', () => {
    expect(formatAxisTps(0)).toBe('0');
    expect(formatAxisTps(25)).toBe('25');
    expect(formatAxisTps(50)).toBe('50');
  });
  it('500 → «500», 1000 → «1k», 1500 → «1.5k»', () => {
    expect(formatAxisTps(500)).toBe('500');
    expect(formatAxisTps(1000)).toBe('1k');
    expect(formatAxisTps(1500)).toBe('1.5k');
  });
});

describe('avgThroughputOf: среднее без простоя (running > 0)', () => {
  it('среднее по точкам с running>0; простой (running=0) не входит', () => {
    const samples: ThroughputSample[] = [
      { t: 1, throughput: 100, running: 1 },
      { t: 2, throughput: 200, running: 2 },
      { t: 3, throughput: 50, running: 0 },    // простой — не входит
      { t: 4, throughput: null, running: null },
    ];
    // (100 + 200) / 2 = 150
    expect(avgThroughputOf(samples)).toBe(150);
  });

  it('только простой (running=0) — null', () => {
    const samples: ThroughputSample[] = [
      { t: 1, throughput: 100, running: 0 },
      { t: 2, throughput: 200, running: 0 },
    ];
    expect(avgThroughputOf(samples)).toBeNull();
  });

  it('пусто — null', () => {
    expect(avgThroughputOf([])).toBeNull();
  });

  it('running=null (движок не отдал) — точка не входит', () => {
    const samples: ThroughputSample[] = [
      { t: 1, throughput: 100, running: null },
      { t: 2, throughput: 200, running: 1 },
    ];
    // только 200
    expect(avgThroughputOf(samples)).toBe(200);
  });
});

describe('peakThroughputOf: пик по истории в пределах окна', () => {
  const now = 1_000_000;
  it('максимум genThroughput в окне; вне окна не считается', () => {
    const history = [
      { t: now - 10_000, genThroughput: 100 },
      { t: now - 5_000, genThroughput: 400 },
      { t: now - 1_000, genThroughput: 250 },
      // вне окна (старше 15 мин)
      { t: now - 20 * 60_000, genThroughput: 999 },
    ];
    expect(peakThroughputOf(history, 15 * 60_000, now)).toBe(400);
  });

  it('null не влияет; пусто — 0', () => {
    const history = [
      { t: now - 10_000, genThroughput: null },
      { t: now - 5_000, genThroughput: 300 },
    ];
    expect(peakThroughputOf(history, 15 * 60_000, now)).toBe(300);
    expect(peakThroughputOf([], 15 * 60_000, now)).toBe(0);
  });
});

// === Сведение в интервалы: доля из СУММ, максимум, сумма, разрывы ===

// Точка с нужными полями (остальные — null).
function sample(p: Partial<ThroughputSample> & { t: number }): ThroughputSample {
  return {
    t: p.t,
    throughput: p.throughput ?? null,
    running: p.running ?? null,
    kvUsage: p.kvUsage ?? null,
    prefixHits: p.prefixHits ?? null,
    prefixQueries: p.prefixQueries ?? null,
    ramHits: p.ramHits ?? null,
    ramQueries: p.ramQueries ?? null,
    preemptions: p.preemptions ?? null,
    genTokens: p.genTokens ?? null,
    itlSeconds: p.itlSeconds ?? null,
    itlCount: p.itlCount ?? null,
  };
}

// Окно 60 с, два интервала по 30 с.
const T_MIN = 0, T_MAX = 60_000, INTERVAL_MS = 30_000;

describe('buildIntervals: доля попаданий из СУММ приращений', () => {
  it('точка 1/1 и точка 0/99 в одном интервале дают 1 %, а не 50 %', () => {
    const samples = [
      sample({ t: 5_000, prefixHits: 1, prefixQueries: 1 }),
      sample({ t: 10_000, prefixHits: 0, prefixQueries: 99 }),
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    // (1 + 0) / (1 + 99) = 0.01, а не (100 % + 0 %) / 2 = 50 %
    expect(intervals[0].prefix).toBeCloseTo(0.01, 6);
  });

  it('Σ queries = 0 → null (обрыв линии, не разрыв)', () => {
    const samples = [sample({ t: 5_000, prefixHits: 5, prefixQueries: 0 })];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].prefix).toBeNull();
  });

  it('из RAM — так же из сумм; Σ ramQueries = 0 → null', () => {
    const samples = [
      sample({ t: 5_000, ramHits: 2, ramQueries: 4 }),
      sample({ t: 10_000, ramHits: 0, ramQueries: 6 }),
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].ram).toBeCloseTo(0.2, 6);
    const empty = buildIntervals([sample({ t: 5_000, ramHits: 3, ramQueries: 0 })], T_MIN, T_MAX, INTERVAL_MS);
    expect(empty[0].ram).toBeNull();
  });

  it('kvUsage — максимум по точкам интервала (пик давления не теряется)', () => {
    const samples = [
      sample({ t: 5_000, kvUsage: 0.3 }),
      sample({ t: 10_000, kvUsage: 0.8 }),
      sample({ t: 15_000, kvUsage: 0.5 }),
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].kvUsage).toBe(0.8);
  });

  it('выработка — среднее ненулевых; вытеснения — сумма', () => {
    const samples = [
      sample({ t: 5_000, throughput: 100, running: 1, preemptions: 2 }),
      sample({ t: 10_000, throughput: 200, running: 1, preemptions: 3 }),
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].throughput).toBe(150);
    expect(intervals[0].preemptions).toBe(5);
  });

  it('все null в интервале — разрыв: throughput/kvUsage/prefix/preemptions null', () => {
    const samples = [
      sample({ t: 5_000 }),
      sample({ t: 10_000 }),
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].throughput).toBeNull();
    expect(intervals[0].kvUsage).toBeNull();
    expect(intervals[0].prefix).toBeNull();
    expect(intervals[0].preemptions).toBeNull();
  });

  it('пустой интервал (точек нет) — null; интервалы не смешиваются', () => {
    const samples = [sample({ t: 5_000, throughput: 100, running: 1, prefixHits: 10, prefixQueries: 20 })];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].throughput).toBe(100);
    expect(intervals[0].prefix).toBeCloseTo(0.5, 6);
    // второй интервал пуст
    expect(intervals[1].throughput).toBeNull();
    expect(intervals[1].prefix).toBeNull();
  });
});

describe('cacheHitAvg: кэш за окно из сумм по всем точкам', () => {
  it('Σ hits / Σ queries; простой (0/0) не тянет долю вниз', () => {
    const samples = [
      sample({ t: 1, prefixHits: 80, prefixQueries: 100 }),
      sample({ t: 2, prefixHits: 0, prefixQueries: 0 }),   // простой
      sample({ t: 3, prefixHits: 20, prefixQueries: 100 }),
    ];
    // (80 + 0 + 20) / (100 + 0 + 100) = 100 / 200 = 0.5
    expect(cacheHitAvg(samples)).toBeCloseTo(0.5, 6);
  });

  it('запросов не было (все null) → null', () => {
    expect(cacheHitAvg([sample({ t: 1 }), sample({ t: 2 })])).toBeNull();
  });

  it('Σ queries = 0 → null', () => {
    expect(cacheHitAvg([sample({ t: 1, prefixHits: 5, prefixQueries: 0 })])).toBeNull();
  });
});

describe('preemptionTotal: сумма вытеснений по окну', () => {
  it('суммирует ненулевые, null пропускает', () => {
    const samples = [
      sample({ t: 1, preemptions: 2 }),
      sample({ t: 2 }),
      sample({ t: 3, preemptions: 3 }),
    ];
    expect(preemptionTotal(samples)).toBe(5);
  });
  it('пусто — 0', () => {
    expect(preemptionTotal([])).toBe(0);
  });
});

describe('intervalCountFor: число интервалов окна', () => {
  it('5m → 20 (по 15 с), 15m → 60 (по 15 с), 1h → 60 (по 60 с)', () => {
    expect(intervalCountFor('5m')).toBe(20);
    expect(intervalCountFor('15m')).toBe(60);
    expect(intervalCountFor('1h')).toBe(60);
  });
});

describe('breakRuns: непрерывные прогоны разрыва (throughput == null)', () => {
  const mk = (tStart: number, tEnd: number, tp: number | null): Interval =>
    ({ tStart, tEnd, throughput: tp, perStream: null, kvUsage: null, prefix: null, ram: null, preemptions: null,
      hasData: { prefix: false, ram: false } });

  it('один прогон: серия null → пара [start, end]', () => {
    const intervals = [mk(0, 1, 10), mk(1, 2, null), mk(2, 3, null), mk(3, 4, 20)];
    expect(breakRuns(intervals)).toEqual([[1, 2]]);
  });

  it('два раздельных разрыва → две пары', () => {
    const intervals = [mk(0, 1, null), mk(1, 2, 10), mk(2, 3, null), mk(3, 4, null), mk(4, 5, 10)];
    expect(breakRuns(intervals)).toEqual([[0, 0], [2, 3]]);
  });

  it('без разрывов → пусто; ноль (не null) — не разрыв', () => {
    expect(breakRuns([mk(0, 1, 0), mk(1, 2, 0)])).toEqual([]);
  });
});

describe('chartAriaLabel: подпись области графика', () => {
  it('окно + среднее + кэш', () => {
    const label = chartAriaLabel('15m', 412, 0.87);
    expect(label).toContain('15 минут');
    expect(label).toContain('412');
  });
  it('1h → «1 час»; без avg/hit — только окно', () => {
    expect(chartAriaLabel('1h', null, null)).toBe('Выработка и кэш за 1 час');
  });
});

describe('tpChartAriaLabel: SVG «Скорость генерации, ток/с» обновляется на hover', () => {
  it('базовый: окно + среднее + кэш', () => {
    const label = tpChartAriaLabel('15m', 412, 0.87, null, null);
    expect(label).toContain('Скорость генерации, ток/с за 15 минут');
    expect(label).toContain('412 ток/с');
    expect(label).not.toContain('интервал');
  });
  it('hover: интервал со скоростью и «шагом модели»', () => {
    const label = tpChartAriaLabel('15m', 412, 0.87, '14:32', 250, 34, 3.9, 1.5);
    expect(label).toContain('интервал 14:32');
    expect(label).toContain('скорость генерации 250 ток/с');
    expect(label).toContain('шаг 34 мс');
    expect(label).toContain('3.9 ток/шаг');
    expect(label).toContain('1.5 агентов');
  });
  it('hover: генерации не было, но шаг модели есть → «генерации не было» + серия шага', () => {
    const label = tpChartAriaLabel('15m', 412, 0.87, '14:32', null, null, 2.1, 0.8);
    expect(label).toContain('генерации не было');
    expect(label).toContain('2.1 ток/шаг');
    expect(label).toContain('0.8 агентов');
    expect(label).not.toContain('движок не отвечал');
  });
  it('hover на разрыве (все серии null) → «движок не отвечал»', () => {
    const label = tpChartAriaLabel('1h', 412, 0.87, '14:32', null, null, null, null);
    expect(label).toContain('интервал 14:32');
    expect(label).toContain('движок не отвечал');
    const after = label.split('интервал')[1] ?? '';
    expect(after).not.toMatch(/\d+\s+ток\/с/);
  });
});

describe('kvChartAriaLabel: SVG «Кэш, %» обновляется на hover', () => {
  it('базовый: окно + попадания; без RAM — без части про RAM', () => {
    const label = kvChartAriaLabel('15m', 0.87, false, null, null, null);
    expect(label).toContain('Кэш за 15 минут');
    expect(label).toContain('попадания 87 %');
    expect(label).not.toContain('интервал');
    expect(label).not.toContain('RAM');
  });
  it('hover c RAM: интервал + попадания + из RAM', () => {
    const label = kvChartAriaLabel('15m', 0.87, true, '14:32', 0.91, 0.5);
    expect(label).toContain('интервал 14:32');
    expect(label).toContain('попадания 91 %');
    expect(label).toContain('из RAM 50 %');
  });
  it('hover без RAM: пункт RAM в label не пишется', () => {
    const label = kvChartAriaLabel('15m', 0.87, false, '14:32', 0.91, null);
    expect(label).toContain('попадания 91 %');
    expect(label).not.toContain('RAM');
  });
});

describe('evChartAriaLabel: SVG «Вытеснения» обновляется на hover', () => {
  it('базовый: «за окно N»', () => {
    const label = evChartAriaLabel('15m', 7, null, null);
    expect(label).toBe('Вытеснения за 15 минут: за окно 7');
  });
  it('базовый без вытеснений → «не было»', () => {
    const label = evChartAriaLabel('1h', 0, null, null);
    expect(label).toBe('Вытеснения за 1 час: не было');
  });
  it('hover: добавляется интервал со значением', () => {
    const label = evChartAriaLabel('15m', 7, '14:32', 2);
    expect(label).toContain('интервал 14:32');
    expect(label).toContain('2');
  });
  it('hover на разрыве → «—»', () => {
    const label = evChartAriaLabel('15m', 7, '14:32', null);
    expect(label).toContain('—');
  });
});

describe('shouldShowNoDataLabel: текст «данных нет» только при широкой зоне', () => {
  it('≤ 40 → false (подпись не влезает)', () => {
    expect(shouldShowNoDataLabel(0)).toBe(false);
    expect(shouldShowNoDataLabel(40)).toBe(false);
  });
  it('> 40 → true', () => {
    expect(shouldShowNoDataLabel(41)).toBe(true);
    expect(shouldShowNoDataLabel(120)).toBe(true);
  });
});

// === П.3: сетка интервалов на абсолютном времени (не на tMin = now − window) ===
// Старая логика строила сетку от tMin: при живом now (сдвигается на 2 с каждый
// опрос) границы прыгали каждый опрос. Мутация «rightEdge = tMax» (без ceil до
// кратного шагу) краснит тест кратности.

describe('intervalGrid: границы кратны шагу, сетка стабильна при живом now', () => {
  it('tMax кратный шагу — сетка совпадает со старым поведением (от tMin)', () => {
    const grid = intervalGrid(0, 60_000, 30_000);
    expect(grid).toEqual([[0, 30_000], [30_000, 60_000]]);
  });

  it('все границы кратны intervalMs (абсолютное время), даже если tMin не кратен', () => {
    const grid = intervalGrid(5_000, 65_000, 30_000);
    for (const [a, b] of grid) {
      expect(a % 30_000).toBe(0);
      expect(b % 30_000).toBe(0);
      expect(b - a).toBe(30_000);
    }
  });

  it('now сдвинулся на 500 мс (в пределах интервала) — сетка НЕ пересчиталась', () => {
    const before = intervalGrid(5_000, 65_000, 30_000);
    const after = intervalGrid(5_500, 65_500, 30_000);
    expect(after).toEqual(before);
  });

  it('now перешагнул границу интервала — появился новый интервал справа, старые на месте', () => {
    const before = intervalGrid(0, 89_000, 30_000);
    const after = intervalGrid(0, 90_001, 30_000);
    // правый край: 90_000 → 120_000; все интервалы сдвинулись ровно на один шаг вправо
    expect(after[0]).toEqual([before[0][0] + 30_000, before[0][1] + 30_000]);
    expect(after[after.length - 1][1]).toBe(120_000);
  });
});

// === П.3/5: «сейчас» — из серверных таймштампов, не Date.now() клиента ===
// Мутация «chartNow = () => fallback» красит первый тест; «max → min» — второй.

describe('chartNow: «сейчас» = время последней точки истории', () => {
  it('максимальный t из истории (серверные таймштампы), не дата клиента', () => {
    const history = [
      { t: 1_000, genThroughput: 1 },
      { t: 3_000, genThroughput: 2 },
      { t: 2_000, genThroughput: 3 },
    ];
    expect(chartNow(history, 99_999)).toBe(3_000);
  });

  it('пустая история — fallback (Date.now() на стороне вызывающего)', () => {
    expect(chartNow([], 42_424)).toBe(42_424);
    expect(chartNow(undefined, 7)).toBe(7);
  });
});

// === П.4: «рваный» кэш — «не спрашивали» не рвёт линию, молчание движка — рвёт ===
// Старая логика: Σ queries = 0 → null (обрыв) — на лёгкой нагрузке линия рвалась
// постоянно. Мутация «else if (hasPH) prefix = prevPrefix → prefix = null» красит
// тест несения; «hasPH → !hasPH» (молчание) красит тест разрыва.

describe('buildIntervals: кэш — несение при «не спрашивали», разрыв при молчании', () => {
  // Окно 60 с, 4 интервала по 15 с; точки с шагом 5 с (контракт PointStep).
  const T_MIN = 0, T_MAX = 60_000, INTERVAL_MS = 15_000;

  // Точка: t + серии кэша. asked — движок отдал счётчики (hits/queries, в т.ч. 0/0);
  // silent — движок молчит (null/null).
  const cachePoint = (t: number, hits: number | null, queries: number | null): ThroughputSample =>
    sample({ t, throughput: 10, running: 1, prefixHits: hits, prefixQueries: queries });

  it('интервал без обращений (0/0) несёт предыдущее значение — линия не рвётся', () => {
    const samples = [
      // интервал 0: 80/100 → 0.8
      cachePoint(5_000, 80, 100),
      // интервал 1: 0/0 — «не спрашивали» (не разрыв!)
      cachePoint(20_000, 0, 0),
      cachePoint(25_000, 0, 0),
      // интервал 2: движок молчит (null) — разрыв
      cachePoint(35_000, null, null),
      cachePoint(40_000, null, null),
      // интервал 3: 50/100 → 0.5
      cachePoint(50_000, 50, 100),
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].prefix).toBeCloseTo(0.8, 6);
    expect(intervals[1].prefix).toBeCloseTo(0.8, 6);   // несётся (старая логика: null)
    expect(intervals[2].prefix).toBeNull();             // молчание — разрыв
    expect(intervals[3].prefix).toBeCloseTo(0.5, 6);
  });

  it('hasData: «не спрашивали» — серия отдана (true), молчание — false', () => {
    const samples = [
      cachePoint(5_000, 80, 100),
      cachePoint(20_000, 0, 0),
      cachePoint(35_000, null, null),
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].hasData.prefix).toBe(true);
    expect(intervals[1].hasData.prefix).toBe(true);   // 0/0 — счётчики отданы
    expect(intervals[2].hasData.prefix).toBe(false);  // null — движок молчал
  });

  it('первый интервал «не спрашивали» — нечего нести: null, но без фона разрыва', () => {
    const samples = [cachePoint(5_000, 0, 0), cachePoint(10_000, 0, 0)];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].prefix).toBeNull();
    expect(intervals[0].hasData.prefix).toBe(true);
  });

  it('из RAM — то же правило: 0/0 несёт, null/null рвёт', () => {
    const ramPoint = (t: number, hits: number | null, queries: number | null): ThroughputSample =>
      sample({ t, throughput: 10, running: 1, ramHits: hits, ramQueries: queries });
    const samples = [
      ramPoint(5_000, 20, 100),     // интервал 0: 0.2
      ramPoint(20_000, 0, 0),       // интервал 1: «не спрашивали» → 0.2
      ramPoint(35_000, null, null), // интервал 2: молчание → null
      ramPoint(50_000, 10, 100),    // интервал 3: 0.1
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].ram).toBeCloseTo(0.2, 6);
    expect(intervals[1].ram).toBeCloseTo(0.2, 6);
    expect(intervals[2].ram).toBeNull();
    expect(intervals[3].ram).toBeCloseTo(0.1, 6);
  });

  it('разрыв в середине не «залипает»: после молчания новое значение, не несение', () => {
    const samples = [
      cachePoint(5_000, 90, 100),      // 0.9
      cachePoint(20_000, null, null),  // молчание → null
      cachePoint(35_000, 10, 100),     // 0.1 (НЕ 0.9: после разрыва несения нет)
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].prefix).toBeCloseTo(0.9, 6);
    expect(intervals[1].prefix).toBeNull();
    expect(intervals[2].prefix).toBeCloseTo(0.1, 6);
  });
});

// === Вторая линия «на агента, в среднем»: скорость = ΣgenTokens/ΣitlSeconds ===
// Мутации: «суммы → среднее долей» красит тест на расчёт; «null → 0» — на разрыв;
// «max одной серии → max обеих» — на ось.

// Минимальный Interval для тестов построения/разрывов линий.
function mkIt(tStart: number, tEnd: number, perStream: number | null, throughput: number | null = null,
  stepMs: number | null = null, tokensPerStep: number | null = null, agents: number | null = null): Interval {
  return { tStart, tEnd, throughput, perStream, kvUsage: null, prefix: null, ram: null,
    preemptions: null, hasData: { prefix: false, ram: false }, stepMs, tokensPerStep, agents };
}

describe('buildIntervals: скорость на агента из СУММ приращений', () => {
  const T_MIN = 0, T_MAX = 60_000, INTERVAL_MS = 30_000;

  it('ΣgenTokens/ΣitlSeconds по точкам интервала (не среднее долей точек)', () => {
    const samples = [
      sample({ t: 5_000, genTokens: 100, itlSeconds: 10 }),
      sample({ t: 10_000, genTokens: 200, itlSeconds: 20 }),
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    // (100 + 200) / (10 + 20) = 300 / 30 = 10
    expect(intervals[0].perStream).toBeCloseTo(10, 6);
  });

  it('разрыв: точка без пары genTokens/itlSeconds не входит → perStream null', () => {
    const samples = [
      sample({ t: 5_000, genTokens: 100, itlSeconds: 10 }),   // интервал 0: 10
      sample({ t: 35_000, genTokens: 50, itlSeconds: null }),  // интервал 1: нет itl → null
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].perStream).toBeCloseTo(10, 6);
    expect(intervals[1].perStream).toBeNull();
  });

  it('ΣitlSeconds = 0 → perStream null (не делим на ноль)', () => {
    const samples = [sample({ t: 5_000, genTokens: 100, itlSeconds: 0 })];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].perStream).toBeNull();
  });
});

describe('buildPerStreamSegments: разрыв линии на null', () => {
  it('null в середине — два отдельных сегмента', () => {
    const intervals = [
      mkIt(0, 30_000, 10),
      mkIt(30_000, 60_000, null),
      mkIt(60_000, 90_000, 20),
    ];
    const segs = buildPerStreamSegments(intervals, 0, 90_000, 20, GEOM);
    expect(segs).toHaveLength(2);
    expect(segs[0]).toHaveLength(1);
    expect(segs[1]).toHaveLength(1);
  });

  it('без null — один непрерывный сегмент на все интервалы', () => {
    const intervals = [mkIt(0, 30_000, 10), mkIt(30_000, 60_000, 12), mkIt(60_000, 90_000, 14)];
    const segs = buildPerStreamSegments(intervals, 0, 90_000, 20, GEOM);
    expect(segs).toHaveLength(1);
    expect(segs[0]).toHaveLength(3);
  });
});

describe('perStreamBreakRuns: прогоны разрыва второй линии', () => {
  it('серия null → пара [start, end]; ноль (не null) — не разрыв', () => {
    const intervals = [
      mkIt(0, 1, 10), mkIt(1, 2, null), mkIt(2, 3, null), mkIt(3, 4, 0),
    ];
    expect(perStreamBreakRuns(intervals)).toEqual([[1, 2]]);
  });
  it('без разрывов → пусто', () => {
    expect(perStreamBreakRuns([mkIt(0, 1, 10), mkIt(1, 2, 12)])).toEqual([]);
  });
});

describe('maxSeriesOf: верх общей оси по максимуму ОБЕИХ серий (обе на интервалах)', () => {
  it('скорость генерации выше «выдано стендом» — берётся она', () => {
    const intervals = [mkIt(0, 1, 300, 100)];   // perStream=300, throughput=100
    expect(maxSeriesOf(intervals)).toBe(300);
  });
  it('«выдано стендом» выше — берётся оно', () => {
    const intervals = [mkIt(0, 1, 300, 500)];   // perStream=300, throughput=500
    expect(maxSeriesOf(intervals)).toBe(500);
  });
  it('без данных — 0', () => {
    expect(maxSeriesOf([])).toBe(0);
  });
});

describe('avgPerStreamOf: средняя на агента за окно', () => {
  it('ΣgenTokens/ΣitlSeconds по всем точкам окна', () => {
    const samples = [
      sample({ t: 1, genTokens: 100, itlSeconds: 10 }),
      sample({ t: 2, genTokens: 200, itlSeconds: 20 }),
    ];
    expect(avgPerStreamOf(samples)).toBeCloseTo(10, 6); // 300/30
  });
  it('нет данных → null; Σitl = 0 → null', () => {
    expect(avgPerStreamOf([sample({ t: 1 })])).toBeNull();
    expect(avgPerStreamOf([sample({ t: 1, genTokens: 100, itlSeconds: 0 })])).toBeNull();
  });
});

// === Второй план «выдано стендом, с паузами»: throughput интервала (genThroughput) ===
// Главная линия — perStream (ΣgenTokens/ΣitlSeconds, измерение vLLM); throughput живёт
// рядом и считается независимо. Мутация «perStream ← throughput» красит оба теста.

describe('buildIntervals: скорость генерации и «выдано стендом» живут независимо', () => {
  const T_MIN = 0, T_MAX = 15_000, INTERVAL_MS = 15_000;

  it('агент 2 с пишет и 13 с простаивает: скорость генерации 100, выдано стендом размыто паузами', () => {
    const samples = [
      sample({ t: 0, throughput: 40, genTokens: 200, itlSeconds: 2 }),
      sample({ t: 5000, throughput: 0, genTokens: 0, itlSeconds: 0 }),
      sample({ t: 10000, throughput: 0, genTokens: 0, itlSeconds: 0 }),
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    // скорость генерации = 200/2 = 100 — паузы в знаменатель не входят
    expect(intervals[0].perStream).toBeCloseTo(100, 6);
    // выдано стендом = среднее (40+0+0)/3 — паузы размывают (так и задумано)
    expect(intervals[0].throughput).toBeCloseTo(40 / 3, 6);
  });

  it('два агента параллельно: выдано стендом выше скорости генерации на поток', () => {
    const samples = [
      sample({ t: 0, throughput: 200, genTokens: 1000, itlSeconds: 10 }),
      sample({ t: 5000, throughput: 200, genTokens: 1000, itlSeconds: 10 }),
      sample({ t: 10000, throughput: 200, genTokens: 1000, itlSeconds: 10 }),
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].perStream).toBeCloseTo(100, 6);   // 3000/30
    expect(intervals[0].throughput).toBeCloseTo(200, 6);
    expect(intervals[0].throughput as number).toBeGreaterThan(intervals[0].perStream as number);
  });

  it('движок молчал (throughput и пары null) → обе серии null (разрыв)', () => {
    const intervals = buildIntervals([sample({ t: 0 })], T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].perStream).toBeNull();
    expect(intervals[0].throughput).toBeNull();
  });
});

describe('avgIssuedOf: «выдано стендом» за окно — среднее с простоем', () => {
  it('нули простоя входят в среднее; null пропускается', () => {
    const samples = [
      sample({ t: 1, throughput: 100 }),
      sample({ t: 2, throughput: 0 }),
      sample({ t: 3, throughput: null }),
    ];
    expect(avgIssuedOf(samples)).toBeCloseTo(50, 6);
  });
  it('нет данных → null', () => {
    expect(avgIssuedOf([sample({ t: 1 })])).toBeNull();
  });
});

describe('peakPerStreamOf: пик скорости генерации по интервалам', () => {
  it('максимум perStream; null пропускает; пусто → 0', () => {
    const intervals = [mkIt(0, 1, 100, 300), mkIt(1, 2, null, null), mkIt(2, 3, 150, 10)];
    expect(peakPerStreamOf(intervals)).toBe(150);
    expect(peakPerStreamOf([])).toBe(0);
  });
});

describe('buildIssuedSegments: линия «выдано стендом» рвётся на null', () => {
  it('null в throughput рвёт линию: два сегмента; perStream на неё не влияет', () => {
    const intervals = [mkIt(0, 30_000, 5, 100), mkIt(30_000, 60_000, 5, null), mkIt(60_000, 90_000, 5, 200)];
    const segs = buildIssuedSegments(intervals, 0, 90_000, 200, GEOM);
    expect(segs).toHaveLength(2);
    expect(segs[0]).toHaveLength(1);
    expect(segs[1]).toHaveLength(1);
  });
});

// === «Шаг модели»: время шага, токенов за шаг, агентов в генерации (ADR-029) ===
// Скорость генерации, разложенная на множители: скорость на поток = токенов за шаг ÷
// время шага, × сколько потоков генерировало. Все три — из тех же приращений;
// null = разрыв (молчание/рестарт), ΣitlCount = 0 — деление на ноль.

describe('buildIntervals: «шаг модели» из СУММ приращений', () => {
  const T_MIN = 0, T_MAX = 60_000, INTERVAL_MS = 30_000;

  it('stepMs = ΣitlSeconds/ΣitlCount×1000, tokensPerStep = ΣgenTokens/ΣitlCount', () => {
    const samples = [
      sample({ t: 5_000, genTokens: 100, itlSeconds: 10, itlCount: 20 }),
      sample({ t: 10_000, genTokens: 200, itlSeconds: 20, itlCount: 40 }),
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    // (10 + 20) / (20 + 40) × 1000 = 500 мс/шаг; (100 + 200) / (20 + 40) = 5 ток/шаг
    expect(intervals[0].stepMs).toBeCloseTo(500, 6);
    expect(intervals[0].tokensPerStep).toBeCloseTo(5, 6);
  });

  it('agents = ΣitlSeconds/длина интервала (с): 30 с inter-token за 30 с = 1 агент', () => {
    const samples = [
      sample({ t: 5_000, itlSeconds: 10 }),
      sample({ t: 10_000, itlSeconds: 20 }),
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].agents).toBeCloseTo(1, 6);
  });

  it('два параллельных агента: 60 с inter-token за 30 с = 2 агента', () => {
    const samples = [
      sample({ t: 5_000, itlSeconds: 30 }),
      sample({ t: 10_000, itlSeconds: 30 }),
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].agents).toBeCloseTo(2, 6);
  });

  it('разрыв: точка без itlCount → stepMs/tokensPerStep null; молчание → agents null', () => {
    const samples = [
      sample({ t: 5_000, genTokens: 100, itlSeconds: 10 }),  // itlCount нет
      sample({ t: 35_000 }),                                    // движок молчит
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].stepMs).toBeNull();
    expect(intervals[0].tokensPerStep).toBeNull();
    expect(intervals[0].agents).toBeCloseTo(10 / 30, 6);  // itlSeconds был → агенты считаются
    expect(intervals[1].stepMs).toBeNull();
    expect(intervals[1].tokensPerStep).toBeNull();
    expect(intervals[1].agents).toBeNull();
  });

  it('ΣitlCount = 0 (itlSeconds есть, шагов не было) → stepMs null, не деление на ноль', () => {
    const samples = [sample({ t: 5_000, itlSeconds: 5, itlCount: 0 })];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].stepMs).toBeNull();
  });
});

describe('maxStepMsOf/maxTokensPerStepOf/maxAgentsOf: верх шкал', () => {
  it('максимум серии; null пропускает; пусто → 0', () => {
    const intervals = [
      mkIt(0, 1, 10, null, 100, 3, 1.5),
      mkIt(1, 2, null, null, null, null, null),
      mkIt(2, 3, 20, null, 300, 7, 2.5),
    ];
    expect(maxStepMsOf(intervals)).toBe(300);
    expect(maxTokensPerStepOf(intervals)).toBe(7);
    expect(maxAgentsOf(intervals)).toBe(2.5);
    expect(maxStepMsOf([])).toBe(0);
    expect(maxTokensPerStepOf([])).toBe(0);
    expect(maxAgentsOf([])).toBe(0);
  });
});

describe('avgStepMsOf/avgTokensPerStepOf/avgAgentsOf: средние за окно', () => {
  it('stepMs = ΣitlSeconds/ΣitlCount×1000 по всем точкам окна', () => {
    const samples = [
      sample({ t: 1, itlSeconds: 10, itlCount: 20 }),
      sample({ t: 2, itlSeconds: 20, itlCount: 40 }),
    ];
    expect(avgStepMsOf(samples)).toBeCloseTo(500, 6);
  });

  it('tokensPerStep = ΣgenTokens/ΣitlCount; нет данных → null; ΣitlCount = 0 → null', () => {
    const samples = [
      sample({ t: 1, genTokens: 100, itlCount: 20 }),
      sample({ t: 2, genTokens: 200, itlCount: 40 }),
    ];
    expect(avgTokensPerStepOf(samples)).toBeCloseTo(5, 6);
    expect(avgTokensPerStepOf([sample({ t: 1 })])).toBeNull();
    expect(avgTokensPerStepOf([sample({ t: 1, genTokens: 100, itlCount: 0 })])).toBeNull();
  });

  it('agents = ΣitlSeconds/длина окна (с); молчание (все null) → null', () => {
    const samples = [
      sample({ t: 1, itlSeconds: 30 }),
      sample({ t: 2, itlSeconds: 30 }),
    ];
    expect(avgAgentsOf(samples, 60_000)).toBeCloseTo(1, 6);
    expect(avgAgentsOf([sample({ t: 1 })], 60_000)).toBeNull();
  });
});

describe('buildStepMsSegments/buildTokensPerStepSegments: линии «шага модели» рвутся на null', () => {
  it('null в середине — два отдельных сегмента; без null — один', () => {
    const intervals = [
      mkIt(0, 30_000, 10, null, 100, 3),
      mkIt(30_000, 60_000, 10, null, null, null),
      mkIt(60_000, 90_000, 10, null, 200, 5),
    ];
    const ms = buildStepMsSegments(intervals, 0, 90_000, 200, GEOM);
    expect(ms).toHaveLength(2);
    expect(ms[0]).toHaveLength(1);
    expect(ms[1]).toHaveLength(1);
    const tps = buildTokensPerStepSegments(intervals, 0, 90_000, 5, GEOM);
    expect(tps).toHaveLength(2);
    expect(tps[0]).toHaveLength(1);
    expect(tps[1]).toHaveLength(1);
    const solid = buildStepMsSegments(
      [mkIt(0, 30_000, 10, null, 100, 3), mkIt(30_000, 60_000, 10, null, 120, 4)], 0, 60_000, 120, GEOM);
    expect(solid).toHaveLength(1);
    expect(solid[0]).toHaveLength(2);
  });
});

describe('buildAgentsSteps: ступенчатая линия «агентов в генерации»', () => {
  it('каждый интервал — горизонталь (x0..x1 на уровне y); null рвёт прогон', () => {
    const intervals = [
      mkIt(0, 30_000, 10, null, null, null, 1),
      mkIt(30_000, 60_000, 10, null, null, null, 2),
      mkIt(60_000, 90_000, 10, null, null, null, null),
      mkIt(90_000, 120_000, 10, null, null, null, 1.5),
    ];
    const segs = buildAgentsSteps(intervals, 0, 120_000, 2, GEOM);
    expect(segs).toHaveLength(2, 'null в середине рвёт: два прогона');
    // Каждый интервал — 2 точки (x0,y),(x1,y): первый прогон (2 интервала) — 4 точки,
    // второй (1 интервал) — 2; на границе интервалов x совпадает → вертикальный переход.
    expect(segs[0].split(' ')).toHaveLength(4);
    expect(segs[1].split(' ')).toHaveLength(2);
  });

  it('без null — один непрерывный прогон; пусто — нет сегментов', () => {
    const intervals = [mkIt(0, 30_000, 10, null, null, null, 1), mkIt(30_000, 60_000, 10, null, null, null, 1)];
    expect(buildAgentsSteps(intervals, 0, 60_000, 2, GEOM)).toHaveLength(1);
    expect(buildAgentsSteps([], 0, 60_000, 2, GEOM)).toHaveLength(0);
  });
});

describe('stepChartAriaLabel: SVG «Шаг модели» обновляется на hover', () => {
  it('базовый: окно; без hover — без интервала', () => {
    const label = stepChartAriaLabel('15m', null, null, null);
    expect(label).toBe('Шаг модели за 15 минут');
  });
  it('hover: интервал со временем шага и токенов за шаг; null — «—»', () => {
    const label = stepChartAriaLabel('15m', '14:32', 34, 3.9);
    expect(label).toContain('интервал 14:32');
    expect(label).toContain('шаг 34 мс');
    expect(label).toContain('токенов за шаг 3.9');
    const empty = stepChartAriaLabel('15m', '14:32', null, null);
    expect(empty).toContain('шаг —');
    expect(empty).toContain('токенов за шаг —');
  });
});

describe('agentsChartAriaLabel: SVG «Агентов в генерации» обновляется на hover', () => {
  it('базовый: окно; hover — значение интервала; null — «—»', () => {
    expect(agentsChartAriaLabel('1h', null, null)).toBe('Агентов в генерации за 1 час');
    const label = agentsChartAriaLabel('1h', '14:32', 1.5);
    expect(label).toContain('интервал 14:32');
    expect(label).toContain('1.5');
    expect(agentsChartAriaLabel('1h', '14:32', null)).toContain('—');
  });
});
