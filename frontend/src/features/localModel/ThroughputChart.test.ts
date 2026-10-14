// Чистые функции графика «Выработка стенда, всего»: разрывы на null, «круглая»
// шкала, среднее без простоя, пик. Мутация «null → 0» должна покрасить тест на
// разрыв; «37 → 50» — на шкалу; «среднее с простоем» — на фильтр running>0.

import { describe, expect, it } from 'vitest';
import {
  avgThroughputOf, buildThroughputSegments, formatAxisTps, maxThroughputOf,
  niceAxisMax, peakThroughputOf, xForTime, yForThroughput,
  type ChartGeom, type ThroughputSample,
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
