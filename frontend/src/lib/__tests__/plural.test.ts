// Склонение счётчиков: общая функция lib/plural (ранее жила в lib/spend)
import { describe, it, expect } from 'vitest';
import { plural } from '../plural';

describe('plural: русское склонение числительных (тексты сброса настроек моделей)', () => {
  const word = (n: number) => plural(n, 'специальность', 'специальности', 'специальностей');

  it('1, 21 — единственное число', () => {
    expect(word(1)).toBe('специальность');
    expect(word(21)).toBe('специальность');
  });

  it('2, 4, 22 — «немного» (2–4, кроме 12–14)', () => {
    expect(word(2)).toBe('специальности');
    expect(word(4)).toBe('специальности');
    expect(word(22)).toBe('специальности');
  });

  it('0, 5, 11, 12, 111 — родительный множественного (в т.ч. 11–14)', () => {
    expect(word(0)).toBe('специальностей');
    expect(word(5)).toBe('специальностей');
    expect(word(11)).toBe('специальностей');
    expect(word(12)).toBe('специальностей');
    expect(word(111)).toBe('специальностей');
  });

  it('то же правило для «персона»', () => {
    const p = (n: number) => plural(n, 'персона', 'персоны', 'персон');
    expect(p(1)).toBe('персона');
    expect(p(2)).toBe('персоны');
    expect(p(11)).toBe('персон');
    expect(p(0)).toBe('персон');
  });
});
