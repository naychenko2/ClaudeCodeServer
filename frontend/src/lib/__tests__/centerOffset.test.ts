import { describe, it, expect } from 'vitest';
import { computeCenterShift } from '../centerOffset';
import { CHAT_COLUMN_W, SPLASH_W } from '../design';

// Проверяем главное свойство компенсации: после применения сдвига середина области,
// в которой центрируется контент, совпадает с серединой окна.
//
// Модель: окно [0, win]; слева зоны занимают L, справа R; колонка центра — всё, что
// осталось. Сдвиг >0 съедает справа, <0 — слева, поэтому область центрирования
// становится [centerLeft + max(0,-shift), centerRight - max(0,shift)].
function layout(win: number, left: number, right: number, contentWidth: number) {
  const centerLeft = left;
  const centerRight = win - right;
  const shift = computeCenterShift({ rootLeft: 0, rootRight: win, centerLeft, centerRight, contentWidth });
  const areaLeft = centerLeft + Math.max(0, -shift);
  const areaRight = centerRight - Math.max(0, shift);
  return { shift, areaMid: (areaLeft + areaRight) / 2, areaWidth: areaRight - areaLeft, winMid: win / 2 };
}

describe('computeCenterShift', () => {
  it('зоны симметричны — компенсировать нечего', () => {
    expect(computeCenterShift({ rootLeft: 0, rootRight: 1600, centerLeft: 44, centerRight: 1556, contentWidth: 950 }))
      .toBe(0);
  });

  it('панель слева — центр возвращается на середину окна', () => {
    // Слева список чатов (320) + рельса (44), справа только рельса (44).
    // Окно широкое: запаса колонки хватает на полную компенсацию
    const { shift, areaMid, winMid } = layout(2000, 364, 44, 950);
    expect(shift).toBeGreaterThan(0);        // поджимаем справа
    expect(areaMid).toBe(winMid);            // и середина совпала с серединой окна
  });

  it('панель справа — компенсация зеркальная', () => {
    const { shift, areaMid, winMid } = layout(2000, 44, 364, 950);
    expect(shift).toBeLessThan(0);           // поджимаем слева
    expect(areaMid).toBe(winMid);
  });

  it('запаса не хватило — компенсация частичная, но в верную сторону', () => {
    // То же расположение зон в окне 1600: нужно 320, а отдать колонка может лишь 242.
    // Центр не доезжает до середины окна, зато и лента не сжимается
    const { shift, areaMid, areaWidth, winMid } = layout(1600, 364, 44, 950);
    expect(shift).toBe(242);
    expect(areaWidth).toBe(950);
    expect(areaMid).toBeGreaterThan(winMid);           // остаточный перекос вправо
    expect(areaMid - winMid).toBeLessThan(364 - 44);   // но меньше исходного
  });

  it('перекос ровно равен разнице занятого слева и справа', () => {
    expect(computeCenterShift({ rootLeft: 0, rootRight: 2560, centerLeft: 400, centerRight: 2516, contentWidth: 950 }))
      .toBe(400 - 44);
  });

  it('сдвиг обрезается запасом — контент не сжимается', () => {
    // Колонка 1000 при контенте 950: съесть можно только 50, хотя перекос 500
    const { shift, areaWidth } = layout(1550, 500, 50, 950);
    expect(shift).toBe(50);
    expect(areaWidth).toBe(950);             // контент остался своей ширины
  });

  it('колонка уже контента — компенсации нет вовсе', () => {
    const { shift } = layout(1200, 400, 44, 950);
    expect(shift).toBe(0);
  });

  it('нулевой запас на границе: колонка ровно по контенту', () => {
    const { shift } = layout(1494, 500, 44, 950);
    expect(shift).toBe(0);
  });

  // Пустой центр (заставка «С чего начнём?») занимает вдвое меньше ленты, и запрос
  // к раскладке у него свой. Пока сюда отдавали CHAT_COLUMN_W, на окне ноутбука
  // запаса не оставалось вовсе и заставку перекашивало вслед за колонкой.
  it('заставка на окне ноутбука компенсируется полностью, лента — почти нет', () => {
    // Окно 1440: слева рельса (48) + панель (348), справа только рельса (48)
    const splash = layout(1440, 396, 48, SPLASH_W);
    expect(splash.shift).toBe(348);
    expect(splash.areaMid).toBe(splash.winMid);

    // Та же раскладка, но с бюджетом ленты: колонка 996 против запроса ленты 982 —
    // на компенсацию перекоса в 348px остаётся жалкий остаток, лента как стояла
    // перекошенной, так и стоит. Раньше запрос был 1054 (лента держала жёлоб 52
    // под индикатор ожидания) и остатка не было вовсе; поле ужали до 16 — появились
    // считанные пиксели, сути это не меняет
    const feed = layout(1440, 396, 48, CHAT_COLUMN_W);
    expect(feed.shift).toBe(996 - CHAT_COLUMN_W);
    expect(feed.areaWidth).toBe(CHAT_COLUMN_W);        // лента не сжалась
    expect(feed.areaMid).toBeGreaterThan(feed.winMid); // и осталась перекошенной вправо
  });

  it('координаты корня не обязаны начинаться с нуля', () => {
    // Каркас смещён вправо (страница со своими отступами) — важна только разница.
    // Корень [200, 2200], зоны 364 слева и 44 справа
    expect(computeCenterShift({ rootLeft: 200, rootRight: 2200, centerLeft: 564, centerRight: 2156, contentWidth: 950 }))
      .toBe(364 - 44);
  });
});
