// Столбик дня в обзоре аналитики расхода: правило сборки живёт в чистой функции
// dayColumn (spendModel), а рендер SpendOverview берёт из неё готовые высоты.
// Кейсы ниже закрывают то, что человек видит: порядок классов, высоту столбика
// от дневного итога и нейтральный столбик дня без разбивки по классам.
import { describe, it, expect } from 'vitest';
import { C } from 'aihome_shell/kit';
import { dayColumn, tierSegments } from '../spendModel';

const CHART_H = 130;      // высота области графика (как в SpendOverview)
const PAD = 12;           // отступ столбика от краёв области

describe('tierSegments: сегменты столбика дня', () => {
  it('три класса дают три сегмента в порядке снизу вверх expensive → cheap → free', () => {
    const segs = tierSegments({ byTier: { free: 100, cheap: 200, expensive: 300 } });
    expect(segs.map(s => s.key)).toEqual(['expensive', 'cheap', 'free']);
    expect(segs.map(s => s.value)).toEqual([300, 200, 100]);
  });

  it('цвет сегмента — токен класса', () => {
    const segs = tierSegments({ byTier: { free: 100, cheap: 200, expensive: 300 } });
    expect(segs.map(s => s.color)).toEqual([C.warning, C.info, C.success]);
  });

  it('пустой класс в столбик не попадает', () => {
    const segs = tierSegments({ byTier: { free: 10, cheap: 0, expensive: 5 } });
    expect(segs.map(s => s.key)).toEqual(['expensive', 'free']);
  });

  it('день без byTier (старый ответ бэка) не ломает разметку', () => {
    expect(tierSegments({ bySource: { 'chat-turn': 10 } })).toEqual([]);
  });
});

describe('dayColumn: столбик дня', () => {
  const day = { total: 600, byTier: { free: 100, cheap: 200, expensive: 300 } };

  it('классы идут в порядке снизу вверх expensive → cheap → free', () => {
    const col = dayColumn(day, 600, CHART_H);
    expect(col.segments.map(s => s.key)).toEqual(['expensive', 'cheap', 'free']);
    expect(col.segments.map(s => s.color)).toEqual([C.warning, C.info, C.success]);
    expect(col.neutral).toBe(false);
  });

  it('высота столбика — от дневного итога: сумма высот сегментов == высоте столбика', () => {
    const col = dayColumn(day, 600, CHART_H);
    const sum = col.segments.reduce((a, s) => a + s.height, 0);
    // высота столбика дня — от его итога, а не от суммы классов
    expect(col.height).toBe(Math.round(600 / 600 * (CHART_H - PAD)));
    expect(sum).toBe(col.height);
  });

  it('более дешёвый день — ниже столбик, масштаб от максимума по дням', () => {
    const col = dayColumn({ total: 300, byTier: { cheap: 300 } }, 600, CHART_H);
    expect(col.height).toBe(Math.round(300 / 600 * (CHART_H - PAD)));
    expect(col.segments.reduce((a, s) => a + s.height, 0)).toBe(col.height);
  });

  it('высоты сегментов пропорциональны их значениям', () => {
    const col = dayColumn(day, 600, CHART_H);
    const [expensive, cheap, free] = col.segments;
    expect(cheap.height).toBeGreaterThan(free.height);
    expect(expensive.height).toBeGreaterThan(cheap.height);
    expect(free.height / col.height).toBeCloseTo(100 / 600, 1);
  });

  it('день без byTier — нейтральный столбик, высота от дневного итога', () => {
    const col = dayColumn({ total: 600, bySource: { 'chat-turn': 600 } }, 600, CHART_H);
    expect(col.neutral).toBe(true);
    expect(col.segments).toEqual([]);
    expect(col.height).toBe(Math.round(600 / 600 * (CHART_H - PAD)));
  });

  it('копеечный класс не исчезает, но сумма высот не меняется', () => {
    const col = dayColumn({ total: 1000, byTier: { free: 1, cheap: 999 } }, 1000, CHART_H);
    expect(col.segments.every(s => s.height >= 1)).toBe(true);
    expect(col.segments.reduce((a, s) => a + s.height, 0)).toBe(col.height);
  });
});
