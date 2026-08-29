import { describe, expect, it } from 'vitest';
import { fitStrip, STRIP_GAP } from '../videoStrip';

// Ширины кнопок в тестах круглые: 100 на кнопку, 30 на «⋯» — считать глазами проще,
// а поведение от конкретных чисел не зависит.
const W = [100, 100, 100, 100];
const MORE = 30;

describe('fitStrip', () => {
  it('всё влезло — попапа нет', () => {
    const total = 400 + STRIP_GAP * 3;
    expect(fitStrip(W, total, MORE, 0)).toEqual({ visible: [0, 1, 2, 3], hidden: [] });
  });

  it('не влезло — хвост уходит в попап, место под «⋯» зарезервировано', () => {
    // 250px: без резерва влезли бы две кнопки и «⋯» вытолкнула бы вторую
    const fit = fitStrip(W, 250, MORE, 0);
    expect(fit.visible).toEqual([0, 1]);
    expect(fit.hidden).toEqual([2, 3]);
  });

  it('активный канал из хвоста вытесняет последнюю видимую кнопку', () => {
    const fit = fitStrip(W, 250, MORE, 3);
    // Первый остаётся на своём месте, второй уступает активному
    expect(fit.visible).toEqual([0, 3]);
    expect(fit.hidden).toEqual([1, 2]);
  });

  it('активный виден даже когда места хватает ровно на одну кнопку', () => {
    const fit = fitStrip(W, 100 + MORE + STRIP_GAP, MORE, 2);
    expect(fit.visible).toEqual([2]);
    expect(fit.hidden).toEqual([0, 1, 3]);
  });

  it('места нет ни на одну кнопку — всё в попапе', () => {
    const fit = fitStrip(W, 50, MORE, 1);
    expect(fit.visible).toEqual([]);
    expect(fit.hidden).toEqual([0, 1, 2, 3]);
  });

  it('канал не выбран — вытеснять некого', () => {
    const fit = fitStrip(W, 250, MORE, -1);
    expect(fit.visible).toEqual([0, 1]);
  });

  it('пустой список не падает', () => {
    expect(fitStrip([], 300, MORE, -1)).toEqual({ visible: [], hidden: [] });
  });

  it('активный уже видим — порядок не меняется', () => {
    const fit = fitStrip(W, 250, MORE, 1);
    expect(fit.visible).toEqual([0, 1]);
    expect(fit.hidden).toEqual([2, 3]);
  });
});
