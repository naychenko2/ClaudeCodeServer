import { describe, expect, it } from 'vitest';
import { outpaintFormatWish, quickPlan } from './editorInputs';

const NOW = new Date('2026-10-07T10:00:00Z');
// Пропорции из «Формата» шире трёх значений прежнего списка: тип узкий, значение в рантайме — любое W:H
const outpaint = (ratio: string) => quickPlan('outpaint', ratio as never);

describe('дорисовка из «Формата»: пропорция идёт желанием котировки', () => {
  it('под флагом из строки контекста 4:3 и 21:9 уходят желанием format', () => {
    expect(outpaintFormatWish(true, true, outpaint('4:3'), NOW)).toEqual({ mode: 'aspect', aspect: '4:3', by: 'human', at: NOW.toISOString() });
    expect(outpaintFormatWish(true, true, outpaint('21:9'), NOW)?.aspect).toBe('21:9');
  });
  it('без флага, вне контекста и у других операций желания нет — прежний путь aspectRatio', () => {
    expect(outpaintFormatWish(false, true, outpaint('16:9'))).toBeNull();
    expect(outpaintFormatWish(true, false, outpaint('16:9'))).toBeNull();
    expect(outpaintFormatWish(true, true, quickPlan('upscale', '16:9'))).toBeNull();
  });
});
