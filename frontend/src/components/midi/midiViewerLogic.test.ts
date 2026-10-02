import { describe, expect, it } from 'vitest';
import {
  MAX_PX_PER_SEC, MIN_PX_PER_SEC, fitPxPerSec, formatTime, isSolo, toggleMute, toggleSolo, zoomPxPerSec,
} from './midiViewerLogic';

describe('formatTime', () => {
  it('форматирует m:ss', () => {
    expect(formatTime(0)).toBe('0:00');
    expect(formatTime(9.9)).toBe('0:09');
    expect(formatTime(65)).toBe('1:05');
    expect(formatTime(600)).toBe('10:00');
  });

  it('отрицательное и не число — ноль', () => {
    expect(formatTime(-3)).toBe('0:00');
    expect(formatTime(Number.NaN)).toBe('0:00');
  });
});

describe('масштаб', () => {
  it('короткий трек влезает в ширину', () => {
    expect(fitPxPerSec(600, 10)).toBe(60);
  });

  it('держится в границах', () => {
    expect(fitPxPerSec(600, 0.1)).toBe(MAX_PX_PER_SEC);
    expect(fitPxPerSec(600, 3600)).toBe(MIN_PX_PER_SEC);
    expect(zoomPxPerSec(MAX_PX_PER_SEC, 1)).toBe(MAX_PX_PER_SEC);
    expect(zoomPxPerSec(MIN_PX_PER_SEC, -1)).toBe(MIN_PX_PER_SEC);
  });

  it('без ширины — значение по умолчанию', () => {
    expect(fitPxPerSec(0, 10)).toBe(100);
  });
});

describe('заглушение и соло', () => {
  const idx = [0, 2, 5];

  it('M переключает одну дорожку', () => {
    const a = toggleMute(new Set(), 2);
    expect([...a]).toEqual([2]);
    expect([...toggleMute(a, 2)]).toEqual([]);
  });

  it('S глушит остальные, повторное S снимает', () => {
    const solo = toggleSolo(idx, new Set(), 2);
    expect([...solo].sort()).toEqual([0, 5]);
    expect(isSolo(idx, solo, 2)).toBe(true);
    expect(isSolo(idx, solo, 0)).toBe(false);
    expect([...toggleSolo(idx, solo, 2)]).toEqual([]);
  });

  it('S на другой дорожке переносит соло', () => {
    const solo = toggleSolo(idx, new Set([0, 5]), 0);
    expect([...solo].sort()).toEqual([2, 5]);
  });

  it('при одной дорожке соло нет', () => {
    expect(isSolo([3], new Set(), 3)).toBe(false);
  });
});
