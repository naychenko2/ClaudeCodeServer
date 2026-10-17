import { describe, expect, it, vi } from 'vitest';
import type { FormatTierKey } from 'aihome_shell/kit';
import type { VideoFramePreview, VideoQuote } from '../api';
import { H3_FORMAT, QUOTE, VEO_FORMAT } from '../mocks';
import { etaText, framesAnchor, framesAxis, framesFromPreview, framesText, tierOptions, upscaleNote } from './format';

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
  it('подрезан один кадр — одно предложение; широкая цель режет сверху и снизу', () => {
    expect(framesText('16:9', [{ slot: 'B', aspect: '9:16', sides: false }]))
      .toBe('Кадр B — 9:16, сцена — 16:9: кадр будет подрезан сверху и снизу до модели.');
  });
  it('оба кадра режутся в одну сторону, пропорции разные — одно предложение про оба', () => {
    expect(framesText('16:9', [{ slot: 'A', aspect: '4:3', sides: false }, { slot: 'B', aspect: '9:16', sides: false }]))
      .toBe('Кадр A — 4:3, кадр B — 9:16, сцена — 16:9: кадры будут подрезаны сверху и снизу до модели.');
  });
  it('кадры режутся в разные стороны — «Сцена — 1:1: кадр A (16:9) будет подрезан по бокам, кадр B (9:16) — сверху и снизу до модели.»', () => {
    expect(framesText('1:1', [{ slot: 'A', aspect: '16:9', sides: true }, { slot: 'B', aspect: '9:16', sides: false }]))
      .toBe('Сцена — 1:1: кадр A (16:9) будет подрезан по бокам, кадр B (9:16) — сверху и снизу до модели.');
  });
});

describe('рамка подрезки: ось выбора и якорь', () => {
  it('все кадры режутся по бокам — «sides», по высоте — «height», в разные стороны — «mixed»', () => {
    expect(framesAxis([{ sides: true }, { sides: true }])).toBe('sides');
    expect(framesAxis([{ sides: false }])).toBe('height');
    expect(framesAxis([{ sides: true }, { sides: false }])).toBe('mixed');
  });
  it('сохранённый якорь чужой оси показывается «По центру»; при смешанной подрезке — всегда по центру', () => {
    expect(framesAnchor('sides', 'right')).toBe('right');
    expect(framesAnchor('sides', 'top')).toBe('center');
    expect(framesAnchor('height', 'bottom')).toBe('bottom');
    expect(framesAnchor('height', 'left')).toBe('center');
    expect(framesAnchor('mixed', 'left')).toBe('center');
  });
});

const frame = (aspect: string, sides: boolean, cropped = true): VideoFramePreview => ({
  sourceWidth: 1000, sourceHeight: 1000, sourceAspect: aspect, cropped,
  resultWidth: sides ? 600 : 1000, resultHeight: sides ? 1000 : 600, x: 0, y: 0, w: 1, h: 1,
});
const thumbOf = (slot: 'A' | 'B') => `thumb-${slot}`;

describe('framesFromPreview: модель предупреждения по frames/preview', () => {
  const on = vi.fn();
  it('подрезки нет ни у одного кадра — предупреждения нет', () => {
    expect(framesFromPreview({ aspect: '16:9', anchor: 'center', frameA: frame('16:9', true, false) }, '16:9', 'center', thumbOf, on)).toBeUndefined();
    expect(framesFromPreview({ aspect: '16:9', anchor: 'center' }, '16:9', 'center', thumbOf, on)).toBeUndefined();
  });
  it('подрезан только кадр B: одно превью с его миниатюрой, ось «height»', () => {
    const m = framesFromPreview({ aspect: '16:9', anchor: 'center', frameA: frame('16:9', true, false), frameB: frame('9:16', false) }, '16:9', 'center', thumbOf, on)!;
    expect(m.previews).toEqual([{ slot: 'B', aspect: '9:16', thumb: 'thumb-B' }]);
    expect(m.axis).toBe('height');
    expect(m.target).toBe('16:9');
  });
  it('подрезаны A и B с общей стороной: два превью, якорь из сохранённого', () => {
    const m = framesFromPreview({ aspect: '9:16', anchor: 'right', frameA: frame('16:9', true), frameB: frame('4:3', true) }, '9:16', 'right', thumbOf, on)!;
    expect(m.previews.map(p => p.slot)).toEqual(['A', 'B']);
    expect(m.axis).toBe('sides');
    expect(m.anchor).toBe('right');
    expect(m.text).toBe('Кадр A — 16:9, кадр B — 4:3, сцена — 9:16: кадры будут подрезаны по бокам до модели.');
  });
  it('кадры режутся в разные стороны: выбора нет (mixed), рамка по центру, текст про оба', () => {
    const m = framesFromPreview({ aspect: '1:1', anchor: 'left', frameA: frame('16:9', true), frameB: frame('9:16', false) }, '1:1', 'left', thumbOf, on)!;
    expect(m.axis).toBe('mixed');
    expect(m.anchor).toBe('center');
    expect(m.text).toBe('Сцена — 1:1: кадр A (16:9) будет подрезан по бокам, кадр B (9:16) — сверху и снизу до модели.');
  });
  it('ось «height»: якорь «сверху» сохраняется, выбор отдаётся серверу как есть', () => {
    const m = framesFromPreview({ aspect: '16:9', anchor: 'top', frameB: frame('9:16', false) }, '16:9', 'top', thumbOf, on)!;
    expect(m.anchor).toBe('top');
    m.onAnchor('bottom');
    expect(on).toHaveBeenCalledWith('bottom');
  });
});

const quoteOf = (price: Partial<VideoQuote['price']>): VideoQuote => ({ ...QUOTE, price: { unit: 'free', approx: false, source: 'local', ...price } });
const quotes = (entries: Partial<Record<FormatTierKey, VideoQuote>>) => new Map<FormatTierKey, VideoQuote | null>(Object.entries(entries) as [FormatTierKey, VideoQuote][]);

describe('tierOptions: ступени чипа «Формат» видео', () => {
  it('«Макс» с увеличением: цена, время обоих прогонов в extra и пояснение «два прогона»', () => {
    const t = tierOptions(H3_FORMAT, '16:9', quotes({ max: quoteOf({ eta: 1245, upscaleEta: 915, shotEta: 330, postStep: 'upscale' }) }));
    const max = t.find(x => x.tier === 'max')!;
    expect(max.price).toEqual({ amount: 0, unit: 'free', approx: false });
    expect(max.extra).toBe('~21 мин');
    expect(max.caption).toBe('1440p · 2528×1440');
    expect(max.note).toContain('два прогона');
    expect(t.find(x => x.tier === 'high')!.note).toBeUndefined();
  });
  it('нет у модели: серая ступень с причиной, без размера и цены', () => {
    const t = tierOptions(VEO_FORMAT, '16:9', quotes({ standard: quoteOf({ eta: 240 }) }));
    expect(t.find(x => x.tier === 'standard')!.caption).toBe('720p · 1280×720');
    for (const k of ['high', 'max'] as const) {
      const o = t.find(x => x.tier === k)!;
      expect(o.label).toBe('нет у модели');
      expect(o.disabledReason).toMatch(/нет у модели/);
      expect(o.price).toBeUndefined();
    }
  });
  it('пропорции нет в размерах ступени: размера нет, подпись — одно имя ступени', () => {
    const o = tierOptions(H3_FORMAT, '1:1', quotes({})).find(x => x.tier === 'high')!;
    expect(o.size).toBeNull();
    expect(o.caption).toBe('768p');
  });
  it('время не замерено: у «Макс» — «время уточнится», у обычной ступени — пусто; пока котировки нет — пусто', () => {
    const t = tierOptions(H3_FORMAT, '16:9', quotes({ max: quoteOf({ postStep: 'upscale' }), high: quoteOf({}) }));
    expect(t.find(x => x.tier === 'max')!.extra).toBe('время уточнится');
    expect(t.find(x => x.tier === 'high')!.extra).toBeNull();
    expect(t.find(x => x.tier === 'standard')!.extra).toBeNull();
  });
  it('без каталога ступеней — пустой список', () => {
    expect(tierOptions({ ...H3_FORMAT, tiers: [] }, '16:9', quotes({}))).toEqual([]);
  });
});

describe('upscaleNote: «Макс» — два прогона', () => {
  const full = quoteOf({ eta: 1245, upscaleEta: 915, shotEta: 330, postStep: 'upscale' });
  it('время съёмки — shotEta, увеличения — upscaleEta, размеры из каталога', () => {
    expect(upscaleNote(H3_FORMAT, '1440p', '16:9', full))
      .toBe('«Макс» — два прогона на вашей видеокарте: съёмка 1344×768 (~5,5 мин), затем увеличение до 2528×1440 (~15 мин). Увеличение можно отменить.');
  });
  it('время съёмки не вычисляется разностью eta − upscaleEta: нет shotEta — скобок у съёмки нет', () => {
    const q = quoteOf({ eta: 1245, upscaleEta: 915, postStep: 'upscale' });
    expect(upscaleNote(H3_FORMAT, '1440p', '16:9', q)).toContain('съёмка 1344×768, затем увеличение до 2528×1440 (~15 мин)');
  });
  it('не замерено увеличение — без скобок у увеличения; нет котировки — без обоих', () => {
    expect(upscaleNote(H3_FORMAT, '1440p', '16:9', quoteOf({ shotEta: 330, postStep: 'upscale' }))).toContain('съёмка 1344×768 (~5,5 мин), затем увеличение до 2528×1440. ');
    expect(upscaleNote(H3_FORMAT, '1440p', '16:9', null)).toContain('съёмка 1344×768, затем увеличение до 2528×1440. ');
  });
  it('9:16 — размеры перевёрнуты', () => {
    expect(upscaleNote(H3_FORMAT, '1440p', '9:16', full)).toContain('съёмка 768×1344 (~5,5 мин), затем увеличение до 1440×2528 (~15 мин)');
  });
});
