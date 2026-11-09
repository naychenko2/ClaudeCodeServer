import { describe, expect, it } from 'vitest';
import { aspectLabel, outpaintSize } from './format';

// format.ts тянет kit и сторы; чистые функции проверяем отдельно от них
describe('подпись пропорции исходника', () => {
  it('известные пропорции узнаются с допуском 2 %', () => {
    expect(aspectLabel(1600, 1200)).toBe('4:3');
    expect(aspectLabel(1920, 1080)).toBe('16:9');
    expect(aspectLabel(1000, 1010)).toBe('1:1');
  });
  it('неизвестная — сокращённая дробь', () => {
    expect(aspectLabel(1000, 700)).toBe('10:7');
  });
});

describe('холст «дорисовать»', () => {
  it('4:3 → 1:1: растёт по высоте до квадрата (1600×1200 → 1600×1600)', () => {
    expect(outpaintSize(1600, 1200, '1:1')).toEqual({ w: 1600, h: 1600 });
  });
  it('4:3 → 16:9: растёт по ширине', () => {
    expect(outpaintSize(1200, 900, '16:9')).toEqual({ w: 1600, h: 900 });
  });
  it('мусорные пропорции — нет холста', () => {
    expect(outpaintSize(1600, 1200, 'x')).toBeNull();
  });
});
