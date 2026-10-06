import { describe, expect, it } from 'vitest';
import { etaText, framesText } from './format';

describe('время ступени: «~5,5 мин», «~21 мин», «~45 с»', () => {
  it('минуты с половинами через запятую, секунды — только до минуты', () => {
    expect(etaText(90)).toBe('~1,5 мин');
    expect(etaText(330)).toBe('~5,5 мин');
    expect(etaText(1245)).toBe('~21 мин');
    expect(etaText(45)).toBe('~45 с');
  });
  it('нет замера — null: число не выдумываем', () => {
    expect(etaText(undefined)).toBeNull();
    expect(etaText(0)).toBeNull();
  });
});

describe('предупреждение «кадры будут подрезаны» (записка, «Кадры видео»)', () => {
  it('оба кадра одной пропорции — дословно как в записке', () => {
    expect(framesText('9:16', [{ slot: 'A', aspect: '16:9', sides: true }, { slot: 'B', aspect: '16:9', sides: true }]))
      .toBe('Кадры A и B — 16:9, сцена — 9:16: кадры будут подрезаны по бокам до модели.');
  });
  it('подрезан один кадр или кадры разные — по предложению на кадр; широкая цель режет сверху и снизу', () => {
    expect(framesText('16:9', [{ slot: 'B', aspect: '9:16', sides: false }]))
      .toBe('Кадр B — 9:16, сцена — 16:9: кадр будет подрезан сверху и снизу до модели.');
    expect(framesText('9:16', [{ slot: 'A', aspect: '16:9', sides: true }, { slot: 'B', aspect: '4:3', sides: true }]))
      .toBe('Кадр A — 16:9, сцена — 9:16: кадр будет подрезан по бокам до модели. Кадр B — 4:3, сцена — 9:16: кадр будет подрезан по бокам до модели.');
  });
});
