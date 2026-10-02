import { describe, expect, it } from 'vitest';
import { RULER_H, isBlackKey, makeLayout, noteSounding, noteVisible, pitchToY, timeToX, visibleWindow, xToTime } from './PianoRoll';

const doc = { minPitch: 60, maxPitch: 71, noteCount: 3 };

describe('раскладка нотной ленты', () => {
  it('время ↔ x с учётом клавиатуры и прокрутки', () => {
    const l = makeLayout(doc, { keyW: 50, pxPerSec: 100, scrollLeft: 200, viewW: 450, height: 200 });
    expect(timeToX(l, 2)).toBe(50);
    expect(timeToX(l, 3)).toBe(150);
    expect(xToTime(l, timeToX(l, 3.7))).toBeCloseTo(3.7);
  });

  it('диапазон высот с запасом, верхняя строка — самая высокая нота', () => {
    const l = makeLayout(doc, { keyW: 50, pxPerSec: 100, scrollLeft: 0, viewW: 450, height: 16 * 10 + RULER_H });
    expect(l.lowPitch).toBe(58);
    expect(l.highPitch).toBe(73);
    expect(l.rowH).toBe(10);
    expect(pitchToY(l, 73)).toBe(RULER_H);
    expect(pitchToY(l, 58)).toBe(RULER_H + 150);
  });

  it('узкий диапазон растягивается до минимума строк', () => {
    const l = makeLayout({ minPitch: 60, maxPitch: 60, noteCount: 1 }, { keyW: 0, pxPerSec: 1, scrollLeft: 0, viewW: 1, height: 100 });
    expect(l.highPitch - l.lowPitch + 1).toBe(12);
  });

  it('видимое окно по scrollLeft', () => {
    const l = makeLayout(doc, { keyW: 50, pxPerSec: 100, scrollLeft: 200, viewW: 450, height: 200 });
    expect(visibleWindow(l)).toEqual([2, 6]);
    const win = visibleWindow(l);
    expect(noteVisible({ midi: 60, time: 1, duration: 1.5, velocity: 1 }, win)).toBe(true);
    expect(noteVisible({ midi: 60, time: 0, duration: 1, velocity: 1 }, win)).toBe(false);
    expect(noteVisible({ midi: 60, time: 6.5, duration: 1, velocity: 1 }, win)).toBe(false);
  });

  it('звучащая нота и чёрные клавиши', () => {
    const n = { midi: 61, time: 1, duration: 0.5, velocity: 1 };
    expect(noteSounding(n, 1)).toBe(true);
    expect(noteSounding(n, 1.5)).toBe(false);
    expect(isBlackKey(61)).toBe(true);
    expect(isBlackKey(60)).toBe(false);
  });
});
