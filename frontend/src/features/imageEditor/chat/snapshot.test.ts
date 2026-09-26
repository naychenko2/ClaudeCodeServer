import { describe, expect, it } from 'vitest';
import type { Mark } from '../marks';
import { canvasRevision, snapshotSize, SNAPSHOT_MAX_SIDE } from './snapshot';

const size = { w: 1600, h: 1200 };
const arrow: Mark = { type: 'arrow', x1: 100, y1: 100, x2: 800, y2: 600 };

describe('canvasRevision', () => {
  it('меняется от пометок, шага истории и файла', () => {
    const base = canvasRevision('images/hero.png', 'original', [], size);
    expect(canvasRevision('images/hero.png', 'original', [arrow], size)).not.toBe(base);
    expect(canvasRevision('images/hero.png', 't1', [], size)).not.toBe(base);
    expect(canvasRevision('images/hero.v2.png', 'original', [], size)).not.toBe(base);
  });

  it('не меняется, пока холст тот же: зум и прокрутка в ревизию не входят', () => {
    const a = canvasRevision('images/hero.png', 'original', [arrow], size);
    expect(canvasRevision('images/hero.png', 'original', [{ ...arrow }], size)).toBe(a);
    // Сдвиг меньше шага канонических долей (1e-4) — тот же marks.json, та же ревизия
    expect(canvasRevision('images/hero.png', 'original', [{ ...arrow, x2: 800.01 }], size)).toBe(a);
  });

  it('сдвинутая пометка — другая ревизия', () => {
    const a = canvasRevision('images/hero.png', 'original', [arrow], size);
    expect(canvasRevision('images/hero.png', 'original', [{ ...arrow, x2: 900 }], size)).not.toBe(a);
  });
});

describe('snapshotSize', () => {
  it('ужимает длинную сторону до 1568 и держит пропорции', () => {
    expect(snapshotSize(4000, 3000)).toEqual({ w: SNAPSHOT_MAX_SIDE, h: 1176 });
    expect(snapshotSize(1000, 3136)).toEqual({ w: 500, h: SNAPSHOT_MAX_SIDE });
  });

  it('маленькую картинку не растягивает', () => {
    expect(snapshotSize(640, 400)).toEqual({ w: 640, h: 400 });
  });
});
