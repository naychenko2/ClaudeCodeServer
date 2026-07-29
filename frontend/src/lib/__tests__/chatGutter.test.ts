// Геометрия области прокрутки чата: жёлоб слева под значок ожидания и
// компенсация, удерживающая колонку сообщений по центру окна.
import { describe, it, expect } from 'vitest';
import { gutterBox, CHAT_GUTTER_L } from '../chatGutter';

const CW = 950;

describe('gutterBox', () => {
  it('коробка вмещает жёлоб, колонку и место под полосу', () => {
    const box = gutterBox(CW, 10);
    expect(box.maxWidth).toBe(CW + CHAT_GUTTER_L + 10);
  });

  it('компенсация равна перекосу «жёлоб слева против полосы справа»', () => {
    // Сдвиг коробки влево на половину внешнего отступа возвращает её содержимое
    // на середину окна: (жёлоб − полоса) / 2 гасится marginRight = жёлоб − полоса
    const box = gutterBox(CW, 10);
    expect(box.marginRight).toBe(CHAT_GUTTER_L - 10);
    const leftOfContent = CHAT_GUTTER_L;
    const rightOfContent = 10 + box.marginRight;
    expect(leftOfContent).toBe(rightOfContent);
  });

  it('полоса шире жёлоба компенсации не требует', () => {
    // Толстые полосы (крупный масштаб, системная тема) — отрицательный отступ
    // сдвинул бы колонку в другую сторону, поэтому упираемся в ноль
    expect(gutterBox(CW, CHAT_GUTTER_L + 8).marginRight).toBe(0);
  });

  it('полосы поверх контента (нулевая ширина) — компенсация во весь жёлоб', () => {
    const box = gutterBox(CW, 0);
    expect(box.maxWidth).toBe(CW + CHAT_GUTTER_L);
    expect(box.marginRight).toBe(CHAT_GUTTER_L);
  });

  it('жёлоба хватает на значок с дымком', () => {
    // Значок 19px вынесен на 29px влево, дым уходит ещё на 7px
    expect(CHAT_GUTTER_L).toBeGreaterThanOrEqual(29 + 7);
  });
});
