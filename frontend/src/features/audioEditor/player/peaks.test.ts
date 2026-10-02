import { describe, expect, it } from 'vitest';
import { computePeaks } from './peaks';

describe('computePeaks', () => {
  // Плотно сведённая песня: максимум модуля в каждой корзине ≈1, различается только громкость
  it('столбики плотной песни разной высоты, а не все по единице', () => {
    const rate = 1000;
    const sig = new Float32Array(rate * 2);
    for (let i = 0; i < sig.length; i++) {
      const s = Math.sin((2 * Math.PI * 50 * i) / rate);
      sig[i] = i < rate ? s : Math.max(-1, Math.min(1, s * 30));
    }
    const [quiet, loud] = computePeaks([sig], 2);
    expect(loud).toBe(1);
    expect(quiet).toBeLessThan(0.85);
  });

  it('тишина не превращается в NaN', () => {
    expect(computePeaks([new Float32Array(100)], 4)).toEqual([0, 0, 0, 0]);
  });
});
