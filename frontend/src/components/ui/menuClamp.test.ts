import { describe, expect, it } from 'vitest';
import { clampPopupLeft, popupMaxWidth } from './Menu';

// Окно 1200, карточка списка чатов — [16; 316], меню 230
const VW = 1200;
const panel = { left: 16, right: 316 };

describe('Menu — клэмп попапа по рамкам панели и окна', () => {
  it('без рамок держится только в окне (прежнее поведение)', () => {
    expect(clampPopupLeft(-50, 230, VW)).toBe(8);
    expect(clampPopupLeft(1100, 230, VW)).toBe(VW - 230 - 8);
    expect(clampPopupLeft(300, 230, VW)).toBe(300);
  });

  it('меню кнопки справа не свисает за левую кромку панели', () => {
    // Кнопка у левой части шапки: правый край якоря 120 → желаемый left = 120 - 230 < 16
    expect(clampPopupLeft(120 - 230, 230, VW, panel)).toBe(16);
  });

  it('и за правую кромку панели не уезжает', () => {
    expect(clampPopupLeft(200, 230, VW, panel)).toBe(316 - 230);
  });

  it('влезающее меню остаётся у якоря', () => {
    expect(clampPopupLeft(50, 230, VW, panel)).toBe(50);
  });

  it('попап шире панели встаёт у её левого края', () => {
    expect(clampPopupLeft(200, 320, VW, panel)).toBe(16);
  });

  it('окно важнее рамок: за кромку экрана не уезжает', () => {
    expect(clampPopupLeft(0, 230, VW, { left: 0, right: 100 })).toBe(8);
    expect(clampPopupLeft(1100, 230, VW, { left: 1100, right: 1200 })).toBe(VW - 230 - 8);
  });
});

describe('Menu — потолок ширины попапа', () => {
  it('без рамок — до правой кромки окна', () => {
    expect(popupMaxWidth(100, 380, VW)).toBe(380);
    expect(popupMaxWidth(1000, 380, VW)).toBe(VW - 8 - 1000);
  });

  it('растущее по содержимому меню не вылезает за правую кромку панели', () => {
    expect(popupMaxWidth(50, 380, VW, panel)).toBe(316 - 50);
  });

  it('окно уже рамок — держится окна', () => {
    expect(popupMaxWidth(1000, 380, VW, { left: 900, right: 1300 })).toBe(VW - 8 - 1000);
  });
});
