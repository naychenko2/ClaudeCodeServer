// Серия glif в аналитике расхода: источник с подписью и своим цветом (отличным
// от fal), относится к «генерациям медиа» — рендерится счётчиком, без токенов.
import { describe, it, expect } from 'vitest';
import { C } from '../design';
import { SPEND_SOURCES, GEN_SOURCES, isGenSource, plural, sourceColor, sourceLabel, sourceTextColor } from '../spend';

describe('SPEND_SOURCES: серия glif', () => {
  it('glif присутствует с подписью и цветом серии', () => {
    expect(SPEND_SOURCES.glif).toEqual({ label: 'glif', color: C.warning });
  });

  it('цвет glif отличен от fal — серии различимы на графике', () => {
    expect(SPEND_SOURCES.glif.color).not.toBe(SPEND_SOURCES.fal.color);
  });

  it('хелперы: label/color по ключу, «текстовый» цвет — text-пара токена', () => {
    expect(sourceLabel('glif')).toBe('glif');
    expect(sourceColor('glif')).toBe(C.warning);
    expect(sourceTextColor('glif')).toBe(C.warningText);
    expect(sourceTextColor('fal')).toBe(C.planText);
    // Прочие источники — без text-пары: текстовый цвет совпадает с цветом серии
    expect(sourceTextColor('chat-turn')).toBe(sourceColor('chat-turn'));
  });
});

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

describe('isGenSource: источники-«генерации медиа»', () => {
  it('fal и glif — генерации; токенные источники — нет', () => {
    expect(GEN_SOURCES).toEqual(['fal', 'glif']);
    expect(isGenSource('glif')).toBe(true);
    expect(isGenSource('fal')).toBe(true);
    expect(isGenSource('chat-turn')).toBe(false);
    expect(isGenSource('one-shot')).toBe(false);
    expect(isGenSource('free')).toBe(false);
  });
});
