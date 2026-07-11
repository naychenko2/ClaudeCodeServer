import { describe, it, expect } from 'vitest';
import { computeCropRect, clampOffset } from '../avatarCrop';

describe('computeCropRect — геометрия кропа аватара', () => {
  it('без зума и смещения — центральный квадрат по меньшей стороне', () => {
    const r = computeCropRect(400, 200, 1, 0, 0, 512);
    expect(r.sSize).toBe(200);
    expect(r.sx).toBe(100);   // (400-200)/2
    expect(r.sy).toBe(0);
    expect(r.outSize).toBe(512);
  });

  it('квадратная картинка без зума — весь кадр', () => {
    const r = computeCropRect(300, 300, 1, 0, 0, 512);
    expect(r).toMatchObject({ sx: 0, sy: 0, sSize: 300 });
  });

  it('зум 2 — окно в исходнике вдвое меньше', () => {
    const r = computeCropRect(400, 400, 2, 0, 0, 512);
    expect(r.sSize).toBe(200);
    expect(r.sx).toBe(100);
    expect(r.sy).toBe(100);
  });

  it('смещение сдвигает окно в пикселях исходника', () => {
    const r = computeCropRect(400, 400, 2, 50, -30, 512);
    expect(r.sx).toBe(150);
    expect(r.sy).toBe(70);
  });

  it('смещение клампится на краях — окно не выходит за картинку', () => {
    const r = computeCropRect(400, 400, 2, 9999, -9999, 512);
    expect(r.sx).toBe(200);   // imgW - sSize
    expect(r.sy).toBe(0);
  });

  it('масштаб клампится в диапазон 1..4', () => {
    // scale 0.5 → как 1; scale 10 → как 4
    expect(computeCropRect(400, 400, 0.5, 0, 0, 512).sSize).toBe(400);
    expect(computeCropRect(400, 400, 10, 0, 0, 512).sSize).toBe(100);
  });

  it('прямоугольная картинка с зумом — клампы по каждой оси свои', () => {
    // 600×300, scale 1.5 → sSize = 200; допустимое |offsetX| = 200, |offsetY| = 50
    const r = computeCropRect(600, 300, 1.5, 1000, 1000, 256);
    expect(r.sSize).toBe(200);
    expect(r.sx).toBe(400);   // 600 - 200
    expect(r.sy).toBe(100);   // 300 - 200
  });
});

describe('clampOffset', () => {
  it('внутри допустимого — не меняет', () => {
    expect(clampOffset(400, 400, 2, 50, -50)).toEqual({ offsetX: 50, offsetY: -50 });
  });

  it('без запаса по оси (квадрат без зума) — смещение нулевое', () => {
    expect(clampOffset(300, 300, 1, 20, -20)).toEqual({ offsetX: 0, offsetY: 0 });
  });

  it('за пределами — прижимает к границе', () => {
    expect(clampOffset(400, 200, 1, 999, 999)).toEqual({ offsetX: 100, offsetY: 0 });
  });
});
