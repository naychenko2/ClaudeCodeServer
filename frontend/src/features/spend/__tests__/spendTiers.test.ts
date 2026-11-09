// Цвет полосы строки-карточки по ценовому классу.
// Класс приходит с бэка (SpendCardRow.tier) — фронт красит по ключу и не решает,
// какая модель какого класса. Сборка столбика дня живёт в spendDayColumn.test.ts.
import { describe, it, expect } from 'vitest';
import { C } from 'aihome_shell/kit';
import { rowBarColor } from '../spendModel';

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
