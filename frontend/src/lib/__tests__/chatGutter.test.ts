// Геометрия области прокрутки чата: боковое поле слева и компенсация,
// удерживающая колонку сообщений по центру окна.
import { describe, it, expect } from 'vitest';
import { gutterBox, gutterPadRight } from '../chatGutter';
import { CHAT_GUTTER_L, CHAT_MAX_W, CHAT_COLUMN_W } from '../design';

const CW = 950;

describe('gutterBox', () => {
  it('коробка вмещает поле, колонку и место под полосу', () => {
    const box = gutterBox(CW, 10);
    expect(box.maxWidth).toBe(CW + CHAT_GUTTER_L + 10);
  });

  it('компенсация равна перекосу «поле слева против полосы справа»', () => {
    // Сдвиг коробки влево на половину внешнего отступа возвращает её содержимое
    // на середину окна: (поле − полоса) / 2 гасится marginRight = поле − полоса
    const box = gutterBox(CW, 10);
    expect(box.marginRight).toBe(CHAT_GUTTER_L - 10);
    const leftOfContent = CHAT_GUTTER_L;
    const rightOfContent = 10 + box.marginRight;
    expect(leftOfContent).toBe(rightOfContent);
  });

  it('полоса шире поля компенсации не требует', () => {
    // Толстые полосы (крупный масштаб, системная тема) — отрицательный отступ
    // сдвинул бы колонку в другую сторону, поэтому упираемся в ноль
    expect(gutterBox(CW, CHAT_GUTTER_L + 8).marginRight).toBe(0);
  });

  it('полосы поверх контента (нулевая ширина) — компенсация во всё поле', () => {
    const box = gutterBox(CW, 0);
    expect(box.maxWidth).toBe(CW + CHAT_GUTTER_L);
    expect(box.marginRight).toBe(CHAT_GUTTER_L);
  });

  it('бокового поля хватает на кольца «Эхо» вокруг индикатора', () => {
    // Индикатор стоит в потоке: аватар 28px начинается ровно на границе поля.
    // Кольца расходятся до scale 1.85, то есть выступают за аватар на
    // (28 × 1.85 − 28) / 2 ≈ 12px в каждую сторону. Левый край кольца не должен
    // уходить в минус — иначе клип области прокрутки (overflow-x: hidden) режет пульс
    const ringOverhang = (28 * 1.85 - 28) / 2;
    expect(CHAT_GUTTER_L - ringOverhang).toBeGreaterThanOrEqual(0);
  });

  // CHAT_COLUMN_W — то же самое число, но заранее: раскладке (useCenterOffset) нужно
  // знать полную потребность ленты ДО замеров, а не после. Разъедутся — компенсация
  // перекоса зон снова начнёт сжимать ленту раньше времени
  it('CHAT_COLUMN_W равен реальному месту под коробку, пока полоса не шире поля', () => {
    for (const scrollbarW of [0, 10, 15, CHAT_GUTTER_L]) {
      const box = gutterBox(CHAT_MAX_W, scrollbarW);
      expect(box.maxWidth + box.marginRight).toBe(CHAT_COLUMN_W);
    }
  });

  it('полоса шире поля — бюджет занижен ровно на разницу, и это единицы пикселей', () => {
    // marginRight упирается в ноль, поэтому реальное место чуть больше бюджета:
    // раскладка сожмёт ленту на эту разницу раньше времени. Поле 16 против полосы
    // Windows (17) даёт 1px — цена узких полей, ради которых поле и уменьшали
    const box = gutterBox(CHAT_MAX_W, 17);
    expect(box.maxWidth + box.marginRight - CHAT_COLUMN_W).toBe(17 - CHAT_GUTTER_L);
    expect(box.maxWidth + box.marginRight - CHAT_COLUMN_W).toBeLessThanOrEqual(2);
  });
});

// Колонка стены: центрировать ленту негде, поля должны совпасть сами по себе
describe('gutterPadRight', () => {
  it('полоса плюс паддинг дают ровно левое поле, пока полоса не шире его', () => {
    for (const scrollbarW of [0, 10, 15, CHAT_GUTTER_L]) {
      expect(scrollbarW + gutterPadRight(scrollbarW)).toBe(CHAT_GUTTER_L);
    }
  });

  it('полоса шире поля — паддинг ноль, а не отрицательный', () => {
    // Поле у́же типичной полосы Windows, поэтому это не экзотика: при полосе 17
    // правое поле выходит на пиксель шире левого. Мириться с этим дешевле, чем
    // отрицательным паддингом уводить текст под полосу
    expect(gutterPadRight(17)).toBe(0);
    expect(gutterPadRight(CHAT_GUTTER_L + 6)).toBe(0);
  });
});
