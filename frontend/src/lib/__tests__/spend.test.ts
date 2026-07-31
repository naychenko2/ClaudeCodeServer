// Серия glif в аналитике расхода: источник с подписью и своим цветом (отличным
// от fal), относится к «генерациям медиа» — рендерится счётчиком, без токенов.
import { describe, it, expect } from 'vitest';
import { C } from '../design';
import { SPEND_SOURCES, GEN_SOURCES, isGenSource, sourceColor, sourceLabel, sourceTextColor } from '../spend';

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
