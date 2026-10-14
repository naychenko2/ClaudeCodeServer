// Чистая функция построения линии графика «Нагрузка»: разрывы на null, масштаб,
// один пункт, пусто. Мутация «null → 0» должна покрасить тест на разрыв (см. ниже).

import { describe, expect, it } from 'vitest';
import {
  buildThroughputSegments, maxThroughputOf, maxWaitingOf,
  xForTime, yForThroughput,
  type ChartGeom, type ThroughputSample,
} from './ThroughputChart';

const GEOM: ChartGeom = { width: 340, height: 120, left: 26, right: 18, top: 8, bottom: 18 };

describe('buildThroughputSegments: разрывы на null', () => {
  it('null в середине — два отдельных сегмента (ноль стороны не соединяет)', () => {
    const samples: ThroughputSample[] = [
      { t: 1000, throughput: 10 },
      { t: 2000, throughput: null },   // разрыв
      { t: 3000, throughput: 20 },
    ];
    const segs = buildThroughputSegments(samples, 0, 4000, 20, GEOM);
    // null рвёт линию: слева одна точка, справа одна точка — два сегмента по одной точке
    expect(segs).toHaveLength(2);
    expect(segs[0]).toHaveLength(1);
    expect(segs[1]).toHaveLength(1);
  });

  it('без null — один непрерывный сегмент на все точки', () => {
    const samples: ThroughputSample[] = [
      { t: 1000, throughput: 10 },
      { t: 2000, throughput: 20 },
      { t: 3000, throughput: 30 },
    ];
    const segs = buildThroughputSegments(samples, 0, 4000, 30, GEOM);
    expect(segs).toHaveLength(1);
    expect(segs[0]).toHaveLength(3);
  });

  it('один ненулевой пункт — один сегмент из одной точки', () => {
    const samples: ThroughputSample[] = [{ t: 2000, throughput: 15 }];
    const segs = buildThroughputSegments(samples, 0, 4000, 15, GEOM);
    expect(segs).toHaveLength(1);
    expect(segs[0]).toHaveLength(1);
  });

  it('пустой вход — нет сегментов', () => {
    expect(buildThroughputSegments([], 0, 4000, 1, GEOM)).toHaveLength(0);
  });

  it('только null — нет сегментов (нули не рисуем)', () => {
    const samples: ThroughputSample[] = [{ t: 1000, throughput: null }, { t: 2000, throughput: null }];
    expect(buildThroughputSegments(samples, 0, 4000, 1, GEOM)).toHaveLength(0);
  });

  it('несколько разрывов — сегментов ровно на число непрерывных прогонов', () => {
    const samples: ThroughputSample[] = [
      { t: 100, throughput: 5 },
      { t: 200, throughput: 6 },
      { t: 300, throughput: null },
      { t: 400, throughput: null },
      { t: 500, throughput: 7 },
      { t: 600, throughput: 8 },
      { t: 700, throughput: 9 },
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
    // дно = height - bottom, верх = top
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

describe('maxThroughputOf / maxWaitingOf', () => {
  it('максимум выработки, null и 0 не влияют', () => {
    const samples: ThroughputSample[] = [
      { t: 1, throughput: 12 },
      { t: 2, throughput: null },
      { t: 3, throughput: 40 },
      { t: 4, throughput: 0 },
    ];
    expect(maxThroughputOf(samples)).toBe(40);
  });

  it('пусто — 0', () => {
    expect(maxThroughputOf([])).toBe(0);
  });

  it('максимум очереди считается только по точкам с данными', () => {
    const samples: ThroughputSample[] = [
      { t: 1, throughput: 10, waiting: 3 },
      { t: 2, throughput: null, waiting: 99 },   // без throughput — не считаем
      { t: 3, throughput: 20, waiting: 7 },
    ];
    expect(maxWaitingOf(samples)).toBe(7);
  });
});
