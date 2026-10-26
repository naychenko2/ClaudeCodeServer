// Раскладка столбика дня по ценовым классам и цвет полосы строки-карточки.
// Класс приходит с бэка (SpendDayPoint.byTier, SpendCardRow.tier) — фронт
// красит по ключу и не решает, какая модель какого класса.
import { describe, it, expect } from 'vitest';
import { C } from 'aihome_shell/kit';
import { rowBarColor, tierSegments } from '../spendModel';

const day = { total: 600, byTier: { free: 100, cheap: 200, expensive: 300 } };

describe('tierSegments: сегменты столбика дня', () => {
  it('три класса дают три сегмента в порядке free → cheap → expensive', () => {
    const segs = tierSegments(day);
    expect(segs.map(s => s.key)).toEqual(['free', 'cheap', 'expensive']);
    expect(segs.map(s => s.value)).toEqual([100, 200, 300]);
  });

  it('сумма сегментов равна дневному итогу токенов', () => {
    expect(tierSegments(day).reduce((a, s) => a + s.value, 0)).toBe(day.total);
  });

  it('цвет сегмента — токен класса, доля считается по сумме классов дня', () => {
    const segs = tierSegments(day);
    expect(segs.map(s => s.color)).toEqual([C.success, C.info, C.warning]);
    expect(segs[0].share).toBeCloseTo(100 / 600);
    expect(segs[2].share).toBeCloseTo(0.5);
  });

  it('пустой класс в столбик не попадает', () => {
    const segs = tierSegments({ byTier: { free: 10, cheap: 0, expensive: 5 } });
    expect(segs.map(s => s.key)).toEqual(['free', 'expensive']);
  });

  it('день без byTier (старый ответ бэка) не ломает разметку', () => {
    expect(tierSegments({ bySource: { 'chat-turn': 10 } })).toEqual([]);
  });
});

describe('rowBarColor: цвет полосы строки карточки', () => {
  it('у модели и провайдера — по ценовому классу строки', () => {
    expect(rowBarColor('model', { tier: 'cheap' })).toBe(C.info);
    expect(rowBarColor('model', { tier: 'free' })).toBe(C.success);
    expect(rowBarColor('provider', { tier: 'expensive' })).toBe(C.warning);
  });

  it('дешёвая модель видна сразу — цвет класса не совпадает с акцентом', () => {
    expect(rowBarColor('model', { tier: 'cheap' })).not.toBe(C.accent);
  });

  it('без класса (null) — нейтральный цвет, рендер не падает', () => {
    expect(rowBarColor('model', { tier: null })).toBe(C.textMuted);
    expect(rowBarColor('model', {})).toBe(C.textMuted);
  });

  it('у прочих разрезов — акцент: класс там не значим', () => {
    expect(rowBarColor('project', { tier: 'cheap' })).toBe(C.accent);
    expect(rowBarColor('chat', { tier: null })).toBe(C.accent);
  });
});
