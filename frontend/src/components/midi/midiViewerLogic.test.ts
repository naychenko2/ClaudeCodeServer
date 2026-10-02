import { describe, expect, it } from 'vitest';
import {
  MAX_PX_PER_SEC, MIN_PX_PER_SEC, fitPxPerSec, formatTime, isSolo, shouldFollowPlayhead, toggleMute, toggleSolo,
  zoomPxPerSec,
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
  it('короткий трек влезает в ширину за вычетом клавиатуры', () => {
    expect(fitPxPerSec(656, 56, 10)).toBe(60);
  });

  it('трек целиком не шире ленты при дробной ширине (лишней прокрутки нет)', () => {
    for (const [viewW, keyW, dur] of [[306.6, 40, 7.3], [311, 56, 13], [1279.5, 56, 3.7]]) {
      const px = fitPxPerSec(viewW, keyW, dur);
      expect(keyW + dur * px).toBeLessThanOrEqual(Math.floor(viewW) + 1e-9);
    }
  });

  it('держится в границах', () => {
    expect(fitPxPerSec(656, 56, 0.1)).toBe(MAX_PX_PER_SEC);
    expect(fitPxPerSec(656, 56, 3600)).toBe(MIN_PX_PER_SEC);
    expect(zoomPxPerSec(MAX_PX_PER_SEC, 1)).toBe(MAX_PX_PER_SEC);
    expect(zoomPxPerSec(MIN_PX_PER_SEC, -1)).toBe(MIN_PX_PER_SEC);
  });

  it('без ширины — значение по умолчанию', () => {
    expect(fitPxPerSec(0, 56, 10)).toBe(100);
    expect(fitPxPerSec(40, 56, 10)).toBe(100);
  });
});

describe('слежение за playhead', () => {
  const win: [number, number] = [10, 20];

  it('виден — не прокручиваем', () => {
    expect(shouldFollowPlayhead(5, 15, win, false)).toBe(false);
    expect(shouldFollowPlayhead(5, 15, win, true)).toBe(false);
  });

  it('ушёл за край из окна — догоняем', () => {
    expect(shouldFollowPlayhead(19.9, 20.1, win, false)).toBe(true);
  });

  it('пользователь прокрутил в сторону — без явного действия не отбираем', () => {
    expect(shouldFollowPlayhead(0.5, 0.6, win, false)).toBe(false);
  });

  it('старт, переход, «В начало» — возвращаем к playhead', () => {
    expect(shouldFollowPlayhead(0, 0, win, true)).toBe(true);
    expect(shouldFollowPlayhead(30, 30, win, true)).toBe(true);
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
