// Чистые функции графика «Выработка стенда, всего»: разрывы на null, «круглая»
// шкала, среднее без простоя, пик. Мутация «null → 0» должна покрасить тест на
// разрыв; «37 → 50» — на шкалу; «среднее с простоем» — на фильтр running>0.

import { describe, expect, it } from 'vitest';
import {
  avgIssuedOf, avgPerStreamOf, avgTimeSharesOf, avgThroughputOf, breakRuns,
  buildIntervals, buildIssuedSegments, buildPerStreamSegments, buildTimeSharesBar,
  buildThroughputSegments, cacheHitAvg,
  chartAriaLabel, chartNow, evChartAriaLabel, formatAxisTps, intervalCountFor, intervalGrid,
  kvChartAriaLabel, maxSeriesOf, maxThroughputOf,
  niceAxisMax, peakPerStreamOf, percentile10,
  perStreamBreakRuns, preemptionTotal, shouldShowNoDataLabel,
  timeSharesAriaLabel, tpChartAriaLabel, xForTime, yForThroughput,
  type ChartGeom, type Interval, type TimeShares, type TimeState, type ThroughputSample,
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

describe('niceAxisMax: «круглая» шкала (до 100 — 1, 2, 5; от 100 мельче; не меньше 10)', () => {
  it('0 → 10 (не 0: ось не схлопывается)', () => {
    expect(niceAxisMax(0)).toBe(10);
  });
  it('37 → 50 (ряд 1,2,5: 5×10=50 ≥ 37)', () => {
    expect(niceAxisMax(37)).toBe(50);
  });
  it('210 → 250, а не 500 (пик чуть выше 200 не раздувает ось вдвое)', () => {
    expect(niceAxisMax(210)).toBe(250);
  });
  it('260 → 300', () => {
    expect(niceAxisMax(260)).toBe(300);
  });
  it('101 → 150', () => {
    expect(niceAxisMax(101)).toBe(150);
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
  it('1500 → 1500, 1600 → 2000', () => {
    expect(niceAxisMax(1500)).toBe(1500);
    expect(niceAxisMax(1600)).toBe(2000);
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
    prefillSeconds: p.prefillSeconds ?? null,
    queueSeconds: p.queueSeconds ?? null,
    promptTokens: p.promptTokens ?? null,
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

  it('выработка = Σ genTokens / длина интервала; вытеснения — сумма', () => {
    const samples = [
      sample({ t: 5_000, genTokens: 300, itlSeconds: 1, preemptions: 2 }),
      sample({ t: 10_000, genTokens: 300, itlSeconds: 1, preemptions: 3 }),
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    // Σ genTokens = 600; интервал 30 с → 600/30 = 20
    expect(intervals[0].throughput).toBeCloseTo(20, 6);
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
    const samples = [sample({ t: 5_000, genTokens: 300, itlSeconds: 1, prefixHits: 10, prefixQueries: 20 })];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    // interval 0 = 30 с; Σ genTokens = 300 → 300/30 = 10
    expect(intervals[0].throughput).toBeCloseTo(10, 6);
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
  it('1m → 12 (по 5 с), 5m → 20 (по 15 с), 15m → 60 (по 15 с), 1h → 60 (по 60 с)', () => {
    expect(intervalCountFor('1m')).toBe(12);
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

describe('tpChartAriaLabel: SVG «На запрос, ток/с» обновляется на hover', () => {
  it('базовый: окно + кэш (среднее под графиком не дублируем)', () => {
    const label = tpChartAriaLabel('15m', 412, 0.87, null, null);
    expect(label).toContain('На запрос, ток/с за 15 минут');
    expect(label).toContain('кэш 87 %');
    expect(label).not.toContain('интервал');
    expect(label).not.toContain('в среднем');
  });
  it('hover: интервал со скоростью на запрос', () => {
    const label = tpChartAriaLabel('15m', 412, 0.87, '14:32', 250);
    expect(label).toContain('интервал 14:32');
    expect(label).toContain('на запрос 250 ток/с');
  });
  it('hover на разрыве (perStream null) → «движок не отвечал»', () => {
    const label = tpChartAriaLabel('1h', 412, 0.87, '14:32', null);
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

// === Вторая линия «на запрос, в среднем»: скорость = ΣgenTokens/ΣitlSeconds ===
// Мутации: «суммы → среднее долей» красит тест на расчёт; «null → 0» — на разрыв;
// «max одной серии → max обеих» — на ось.

// Минимальный Interval для тестов построения/разрывов линий.
// timeShares по умолчанию — break (движок молчал); shares/idle — явным.
function mkIt(tStart: number, tEnd: number, perStream: number | null, throughput: number | null = null,
  timeShares: TimeState = { kind: 'break' }): Interval {
  return { tStart, tEnd, throughput, perStream, kvUsage: null, prefix: null, ram: null,
    preemptions: null, hasData: { prefix: false, ram: false }, timeShares };
}

describe('buildIntervals: скорость на запрос из СУММ приращений', () => {
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

describe('avgPerStreamOf: средняя на запрос за окно', () => {
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
    // выдано стендом = Σ genTokens / 15 с = 200/15 ≈ 13.3 — паузы размывают (так и задумано)
    expect(intervals[0].throughput).toBeCloseTo(200 / 15, 6);
  });

  it('один агент в простое: throughput интервала ≤ perStream (часть времени в декодировании не тратилась)', () => {
    const samples = [
      sample({ t: 0, genTokens: 100, itlSeconds: 2 }),    // 100 токенов за 2 с декодирования
      sample({ t: 5000, genTokens: 0, itlSeconds: 0 }),    // простой
      sample({ t: 10000, genTokens: 0, itlSeconds: 0 }),  // простой
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    // perStream = 100/2 = 50 (скорость в декодировании); throughput = 100/15 ≈ 6.7 (за весь интервал)
    expect(intervals[0].perStream).toBeCloseTo(50, 6);
    expect(intervals[0].throughput).toBeCloseTo(100 / 15, 6);
    // «всего по стенду» за 15 с не превышает скорость в декодировании: агент простаивал
    expect(intervals[0].throughput as number).toBeLessThanOrEqual(intervals[0].perStream as number);
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

describe('avgIssuedOf: «выдано стендом» за окно — Σ genTokens / длина окна', () => {
  it('Σ genTokens / (tMax − tMin); простой (genTokens=0) размывает, null не входит в сумму', () => {
    const samples = [
      sample({ t: 0, genTokens: 300 }),
      sample({ t: 10_000, genTokens: 0 }),     // простой
      sample({ t: 20_000, genTokens: null }),   // разрыв — не в сумму, но в длину окна
    ];
    // span = 20_000 − 0 = 20 с; Σ genTokens = 300 → 300/20 = 15
    expect(avgIssuedOf(samples)).toBeCloseTo(15, 6);
  });
  it('нет точек с genTokens → null', () => {
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

describe('buildIssuedSegments: линия «всего по стенду» (genThroughput)', () => {
  it('null в throughput рвёт линию: два сегмента; perStream на неё не влияет', () => {
    const intervals = [mkIt(0, 30_000, 5, 100), mkIt(30_000, 60_000, 5, null), mkIt(60_000, 90_000, 5, 200)];
    const segs = buildIssuedSegments(intervals, 0, 90_000, 200, GEOM);
    expect(segs).toHaveLength(2);
    expect(segs[0]).toHaveLength(1);
    expect(segs[1]).toHaveLength(1);
  });

  it('непрерывный прогон: один сегмент, y выше (меньше) для большего значения', () => {
    const intervals = [
      mkIt(0, 30_000, 10, 50),
      mkIt(30_000, 60_000, 10, 150),
      mkIt(60_000, 90_000, 10, 100),
    ];
    const segs = buildIssuedSegments(intervals, 0, 90_000, 200, GEOM);
    expect(segs).toHaveLength(1);
    expect(segs[0]).toHaveLength(3);
    // 150 (выше) → меньший y (выше на экране), чем 50 и 100
    expect(segs[0][1].y).toBeLessThan(segs[0][0].y);
    expect(segs[0][1].y).toBeLessThan(segs[0][2].y);
  });

  it('null в perStream не рвёт вторую серию: непрерывный прогон', () => {
    const intervals = [
      mkIt(0, 30_000, null, 100),   // perStream null, throughput 100
      mkIt(30_000, 60_000, 50, 100), // perStream 50, throughput 100
    ];
    const segs = buildIssuedSegments(intervals, 0, 60_000, 200, GEOM);
    expect(segs).toHaveLength(1);
    expect(segs[0]).toHaveLength(2);
  });
});

// === «Куда уходит время агентов» (ADR-029, v2.3): составная полоса долей ===
// gen = ΣitlCount × чистый шаг; wait = max(0, ΣitlSec − gen); prefill/queue — приращения
// гистограмм. «Чистый шаг» — перцентиль 10 времени шага по «чистым» интервалам (без
// prefill); доли = каждая / сумма четырёх. Мутации: «wait → gen» красит тест на доли;
// «cleanStep → среднее» — на перцентиль.

describe('percentile10: 10-й перцентиль', () => {
  it('пусто → null; один элемент → он', () => {
    expect(percentile10([])).toBeNull();
    expect(percentile10([5])).toBe(5);
  });
  it('10 элементов → индекс floor(0.1×10)=1', () => {
    expect(percentile10([1, 2, 3, 4, 5, 6, 7, 8, 9, 10])).toBe(2);
  });
});

describe('buildIntervals: «куда уходит время» — доли из сумм и чистого шага', () => {
  const T_MIN = 0, T_MAX = 60_000, INTERVAL_MS = 30_000;

  it('чистый шаг = перцентиль 10; wait = ΣitlSec − ΣitlCnt×чистый шаг', () => {
    const samples = [
      // интервал 0: step = 10/20 = 0.5 (чистый), promptTokens=0
      sample({ t: 5_000, itlSeconds: 10, itlCount: 20, promptTokens: 0 }),
      // интервал 1: step = 40/20 = 2.0 (длинный — ждёт соседа), promptTokens=0
      sample({ t: 35_000, itlSeconds: 40, itlCount: 20, promptTokens: 0 }),
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    // чистый шаг = percentile10([0.5, 2.0]) = 0.5
    // интервал 0: gen = 20×0.5 = 10, wait = 10−10 = 0 → 100 % генерации
    expect(intervals[0].timeShares).toEqual({ kind: 'shares', shares: { gen: 1, prefill: 0, wait: 0, queue: 0 } });
    // интервал 1: gen = 20×0.5 = 10, wait = 40−10 = 30 → 25 % генерации, 75 % ждёт
    expect(intervals[1].timeShares).toEqual({ kind: 'shares', shares: { gen: 0.25, prefill: 0, wait: 0.75, queue: 0 } });
  });

  it('prefill/очередь — приращения гистограмм; доли в сумме 1', () => {
    const samples = [
      // step = 20/20 = 1.0 = чистый шаг (единственный интервал); gen = 20×1.0 = 20, wait = 0
      sample({ t: 5_000, itlSeconds: 20, itlCount: 20, promptTokens: 0, prefillSeconds: 5, queueSeconds: 3 }),
    ];
    const intervals = buildIntervals(samples, T_MIN, T_MAX, INTERVAL_MS);
    const st = intervals[0].timeShares;
    expect(st.kind).toBe('shares');
    if (st.kind !== 'shares') return;
    const s = st.shares;
    // total = 20 (gen) + 0 (wait) + 5 (prefill) + 3 (queue) = 28
    expect(s.gen).toBeCloseTo(20 / 28, 6);
    expect(s.wait).toBe(0);
    expect(s.prefill).toBeCloseTo(5 / 28, 6);
    expect(s.queue).toBeCloseTo(3 / 28, 6);
    expect(s.gen + s.wait + s.prefill + s.queue).toBeCloseTo(1, 6);
  });

  it('молчание (без itl) → break (заливки нет, фон-разрыв)', () => {
    const intervals = buildIntervals([sample({ t: 5_000 })], T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].timeShares).toEqual({ kind: 'break' });
  });

  it('простой: движок отвечал (itl=0), но шагов/запросов не было → idle', () => {
    // itlSeconds/itlCount = 0 (реальные нули, не null) → движок отвечал, но шагов нет.
    const intervals = buildIntervals([sample({ t: 5_000, itlSeconds: 0, itlCount: 0 })], T_MIN, T_MAX, INTERVAL_MS);
    expect(intervals[0].timeShares).toEqual({ kind: 'idle' });
  });

  it('простой ≠ разрыв: itl=0 (idle) vs itl=null (break)', () => {
    const idle = buildIntervals([sample({ t: 5_000, itlSeconds: 0, itlCount: 0 })], T_MIN, T_MAX, INTERVAL_MS);
    const brk = buildIntervals([sample({ t: 5_000 })], T_MIN, T_MAX, INTERVAL_MS);
    expect(idle[0].timeShares).toEqual({ kind: 'idle' });
    expect(brk[0].timeShares).toEqual({ kind: 'break' });
  });
});

describe('buildTimeSharesBar: сегменты полосы на x-диапазоне интервала', () => {
  it('shares: 4 сегмента пропорционально долям, суммарная ширина = x1−x0', () => {
    const shares: TimeShares = { gen: 0.5, prefill: 0.25, wait: 0.15, queue: 0.1 };
    const segs = buildTimeSharesBar({ kind: 'shares', shares }, 0, 100);
    expect(segs).toHaveLength(4);
    expect(segs[0].w).toBeCloseTo(50, 6);
    expect(segs[1].w).toBeCloseTo(25, 6);
    expect(segs[2].w).toBeCloseTo(15, 6);
    expect(segs[3].w).toBeCloseTo(10, 6);
    expect(segs.reduce((a, s) => a + s.w, 0)).toBeCloseTo(100, 6);
  });
  it('break → нет сегментов; shares с нулевой долей — сегмент не рисуется', () => {
    expect(buildTimeSharesBar({ kind: 'break' }, 0, 100)).toHaveLength(0);
    const segs = buildTimeSharesBar({ kind: 'shares', shares: { gen: 1, prefill: 0, wait: 0, queue: 0 } }, 0, 100);
    expect(segs).toHaveLength(1);
  });
  it('idle → один нейтральный сегмент на всю ширину', () => {
    const segs = buildTimeSharesBar({ kind: 'idle' }, 0, 100);
    expect(segs).toHaveLength(1);
    expect(segs[0].x).toBe(0);
    expect(segs[0].w).toBeCloseTo(100, 6);
  });
});

describe('avgTimeSharesOf: средние доли за окно (легенда без считывания)', () => {
  it('среднее по интервалам с долями; break/idle не входят; без долей → null', () => {
    const base: TimeShares = { gen: 0.5, prefill: 0.25, wait: 0.15, queue: 0.1 };
    const intervals = [
      mkIt(0, 1, 10, null, { kind: 'shares', shares: base }),
      mkIt(1, 2, 10, null, { kind: 'shares', shares: { gen: 0.7, prefill: 0.1, wait: 0.1, queue: 0.1 } }),
      mkIt(2, 3, null, null, { kind: 'break' }),   // без долей — не входит
      mkIt(3, 4, null, null, { kind: 'idle' }),     // простой — не входит
    ];
    const avg = avgTimeSharesOf(intervals);
    expect(avg!.gen).toBeCloseTo(0.6, 6);
    expect(avgTimeSharesOf([mkIt(0, 1, null, null, { kind: 'break' })])).toBeNull();
  });
});

describe('timeSharesAriaLabel: SVG «Куда уходит время» обновляется на hover', () => {
  it('базовый: окно; без hover — без интервала', () => {
    expect(timeSharesAriaLabel('15m', null, null)).toBe('Куда уходит время агентов за 15 минут');
  });
  it('hover: доли интервала в %; break — «движок не отвечал»; idle — «простой»', () => {
    const label = timeSharesAriaLabel('15m', '14:32', { kind: 'shares', shares: { gen: 0.45, prefill: 0.2, wait: 0.3, queue: 0.05 } });
    expect(label).toContain('интервал 14:32');
    expect(label).toContain('генерирует 45 %');
    expect(label).toContain('читает промпт 20 %');
    expect(label).toContain('ждёт соседа 30 %');
    expect(label).toContain('в очереди 5 %');
    expect(timeSharesAriaLabel('15m', '14:32', { kind: 'break' })).toContain('движок не отвечал');
    expect(timeSharesAriaLabel('15m', '14:32', { kind: 'idle' })).toContain('простой');
  });
});
