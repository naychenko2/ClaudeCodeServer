// Геометрия области прокрутки чата: отступ слева, равный полосе прокрутки справа,
// удерживает колонку сообщений по центру окна.
import { describe, it, expect } from 'vitest';
import { chatBox } from '../chatBox';
import { CHAT_SCROLLBAR_W, CHAT_MAX_W, CHAT_COLUMN_W } from '../design';

const CW = 950;

describe('chatBox', () => {
  it('коробка вмещает колонку, полосу и равный ей отступ слева', () => {
    const box = chatBox(CW, 10);
    expect(box.maxWidth).toBe(CW + 10 * 2);
  });

  it('слева ровно столько же, сколько занимает полоса справа', () => {
    // Обе стороны колонки одинаковы — она стоит посередине коробки, а коробка
    // посередине окна, и лента не расходится с композером
    for (const scrollbarW of [0, 10, 15, 17]) {
      const box = chatBox(CW, scrollbarW);
      expect(box.paddingLeft).toBe(scrollbarW);
      expect(box.maxWidth - box.paddingLeft - CW).toBe(scrollbarW);
    }
  });

  it('полоса поверх контента (нулевая ширина) — отступа нет вовсе', () => {
    const box = chatBox(CW, 0);
    expect(box.maxWidth).toBe(CW);
    expect(box.paddingLeft).toBe(0);
  });

  // CHAT_COLUMN_W — то же самое число, но заранее: раскладке (useCenterOffset) нужно
  // знать полную потребность ленты ДО замеров, а не после. Занизишь — компенсация
  // перекоса зон начнёт сжимать ленту раньше времени
  it('CHAT_COLUMN_W покрывает реальную коробку при любой полосе', () => {
    for (const scrollbarW of [0, 10, 15, 17]) {
      expect(chatBox(CHAT_MAX_W, scrollbarW).maxWidth).toBeLessThanOrEqual(CHAT_COLUMN_W);
    }
  });

  it('запас под полосу перекрывает реальные ширины полос', () => {
    // Самая толстая полоса на практике — 17px (Windows, крупный масштаб)
    expect(CHAT_SCROLLBAR_W).toBeGreaterThanOrEqual(17);
  });
});
