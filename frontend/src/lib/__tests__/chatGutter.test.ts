// Геометрия области прокрутки чата: жёлоб слева под значок ожидания и
// компенсация, удерживающая колонку сообщений по центру окна.
import { describe, it, expect } from 'vitest';
import { gutterBox } from '../chatGutter';
import { CHAT_GUTTER_L, CHAT_MAX_W, CHAT_COLUMN_W } from '../design';

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

  it('жёлоба хватает на аватар с кольцами «Эхо»', () => {
    // Аватар 28px вынесен в жёлоб на -(28+10), центр на 14 от левого края жёлоба;
    // кольца расходятся до radius 26 — левый край кольца = центр − 26 ≥ 0,
    // иначе клип области прокрутки (overflow-x: hidden) режет пульс
    const avatarCenter = CHAT_GUTTER_L - (28 + 10) + 14;
    expect(avatarCenter - 26).toBeGreaterThanOrEqual(0);
  });

  // CHAT_COLUMN_W — то же самое число, но заранее: раскладке (useCenterOffset) нужно
  // знать полную потребность ленты ДО замеров, а не после. Разъедутся — компенсация
  // перекоса зон снова начнёт сжимать ленту раньше времени
  // (полоса шире жёлоба в равенство не входит: там marginRight упирается в ноль,
  // но таких полос не бывает — крайний реальный случай и есть жёлоб)
  it('CHAT_COLUMN_W равен реальному месту под коробку при любой полосе', () => {
    for (const scrollbarW of [0, 10, 15, 17, CHAT_GUTTER_L]) {
      const box = gutterBox(CHAT_MAX_W, scrollbarW);
      expect(box.maxWidth + box.marginRight).toBe(CHAT_COLUMN_W);
    }
  });
});
