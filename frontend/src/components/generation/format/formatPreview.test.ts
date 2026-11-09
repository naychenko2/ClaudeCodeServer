import { describe, expect, it } from 'vitest';
import { previewRects, ratioOf } from './formatPreview';

describe('геометрия превью «исходник в рамке»', () => {
  it('ratioOf читает «W:H» и отвергает мусор', () => {
    expect(ratioOf('16:9')).toBeCloseTo(16 / 9);
    expect(ratioOf('0:9')).toBeNull();
    expect(ratioOf('abc')).toBeNull();
  });

  it('обрезать 4:3 → 1:1 с рамкой «слева»: рамка у левого края, исходник целиком', () => {
    const g = previewRects('4:3', '1:1', 'crop', 'left', 60)!;
    expect(g.image).toEqual({ x: 0, y: 0, w: 80, h: 60 });
    expect(g.frame.x).toBe(0);
    expect(g.frame.w).toBeCloseTo(60);
    expect(g.frame.h).toBe(60);
  });

  it('«справа» прижимает рамку к правому краю, «по центру» — ровно в середину', () => {
    const r = previewRects('4:3', '1:1', 'crop', 'right', 60)!;
    expect(r.frame.x + r.frame.w).toBeCloseTo(r.width);
    const c = previewRects('4:3', '1:1', 'crop', 'center', 60)!;
    expect(c.frame.x).toBeCloseTo((c.width - c.frame.w) / 2);
  });

  it('дорисовать 4:3 → 1:1: холст квадратный, исходник по центру, рамка на весь холст', () => {
    const g = previewRects('4:3', '1:1', 'pad', 'center', 60)!;
    expect(g.width).toBe(60);
    expect(g.image.w).toBe(60);
    expect(g.image.h).toBeCloseTo(45);
    expect(g.image.y).toBeCloseTo(7.5);
    expect(g.frame).toEqual({ x: 0, y: 0, w: 60, h: 60 });
  });

  it('пропорции не читаются — превью нет', () => {
    expect(previewRects('x', '1:1', 'crop', 'center', 60)).toBeNull();
  });
});
