// Мок ручки format (ADR-025) для мок-режима редактора и e2e: возможности трёх условных моделей и
// упрощённый резолвер. Настоящий считает сервер (MediaFormatResolver); здесь он повторяет только то,
// что нужно сценариям макета media-format-v1: подмена пропорции, «Точно…» с округлением, серые ступени.

import type { MediaFormatCaps, MediaFormatState, MediaFormatWish, FormatTierKey, FormatTierOption, ResolvedFormat } from 'aihome_shell/kit';

const SIZES_1MP: Record<string, { w: number; h: number }> = {
  '1:1': { w: 1024, h: 1024 }, '16:9': { w: 1344, h: 768 }, '9:16': { w: 768, h: 1344 }, '4:3': { w: 1152, h: 864 }, '3:2': { w: 1248, h: 832 }, '2:3': { w: 832, h: 1248 },
};
const SIZES_2MP: Record<string, { w: number; h: number }> = {
  '1:1': { w: 1408, h: 1408 }, '16:9': { w: 1792, h: 1024 }, '9:16': { w: 1024, h: 1792 }, '4:3': { w: 1536, h: 1152 }, '3:2': { w: 1728, h: 1152 }, '2:3': { w: 1152, h: 1728 },
};
const SIZES_4MP: Record<string, { w: number; h: number }> = {
  '1:1': { w: 2048, h: 2048 }, '16:9': { w: 2688, h: 1536 }, '9:16': { w: 1536, h: 2688 }, '4:3': { w: 2304, h: 1728 }, '3:2': { w: 2496, h: 1664 }, '2:3': { w: 1664, h: 2496 },
};
const GPT: Record<string, { w: number; h: number }> = { '1:1': { w: 1024, h: 1024 }, '3:2': { w: 1536, h: 1024 }, '2:3': { w: 1024, h: 1536 } };

const MULTI: MediaFormatCaps = {
  modelLabel: 'FLUX', aspects: ['1:1', '16:9', '9:16', '4:3', '3:2', '2:3'],
  tiers: [
    { tier: 'standard', label: '1 Мп', sizes: SIZES_1MP, priceFactor: 1 },
    { tier: 'high', label: '2 Мп', sizes: SIZES_2MP, priceFactor: 2 },
    { tier: 'max', label: '4 Мп', sizes: SIZES_4MP, priceFactor: 4 },
  ],
  exact: { step: 64, minSide: 256, maxSide: 2048, maxMegapixels: 4 },
};
const ONE_SIZE: MediaFormatCaps = {
  modelLabel: 'GPT Image', aspects: ['1:1', '3:2', '2:3'],
  tiers: [{ tier: 'standard', label: '1 Мп', sizes: GPT, priceFactor: 1 }],
  nativeNote: 'один из трёх размеров около 1,6 Мп',
};

// Модели мока: нет в таблице — размеров не сообщает («Как у модели»)
const CAPS: Record<string, MediaFormatCaps> = {
  'fal-ai/nano-banana-2/edit': MULTI,
  'fal-ai/flux-pro/kontext': MULTI,
  'gpt_image_2_5': ONE_SIZE,
};

const PRICE = 0.04;
const GREY = 'Модель рисует один из трёх размеров около 1,6 Мп — «Высокое» и «Макс» у неё недоступны';

const ratio = (a: string) => { const [x, y] = a.split(':').map(Number); return x / y; };
const nearest = (aspect: string, list: readonly string[]) =>
  [...list].sort((p, q) => Math.abs(Math.log(ratio(p) / ratio(aspect))) - Math.abs(Math.log(ratio(q) / ratio(aspect))))[0];

export function mockFormatState(model: string, op: string, wish: MediaFormatWish | null): MediaFormatState {
  const caps = CAPS[model] ?? null;
  const isGen = op === 'generate';
  if (!caps) {
    return { caps: null, wish: isGen ? wish : null, resolved: { mode: 'model', substituted: false, rounded: false }, tiers: [] };
  }
  const w = isGen ? wish : null;
  const tierKey: FormatTierKey = (w?.tier && caps.tiers.some(t => t.tier === w.tier) ? w.tier : 'standard');
  let resolved: ResolvedFormat;
  const notes: string[] = [];
  if (w?.mode === 'exact' && caps.exact && w.width && w.height) {
    const e = caps.exact;
    let W = w.width; let H = w.height;
    const side = Math.max(W, H);
    if (e.maxSide && side > e.maxSide) { const k = e.maxSide / side; W = Math.round(W * k); H = Math.round(H * k); notes.push(`Модель рисует до ${e.maxSide} px по длинной стороне`); }
    const rw = Math.max(e.step, Math.round(W / e.step) * e.step);
    const rh = Math.max(e.step, Math.round(H / e.step) * e.step);
    if (rw !== W || rh !== H || notes.length) notes.push(`Модель рисует кратно ${e.step}`);
    resolved = { mode: 'exact', width: rw, height: rh, substituted: false, rounded: notes.length > 0, note: notes.length ? `${notes.join('; ')}: ${rw}×${rh}.` : null };
  } else {
    const want = w?.mode === 'aspect' && w.aspect ? w.aspect : null;
    const aspect = want && caps.aspects.includes(want) ? want : want ? nearest(want, caps.aspects) : caps.aspects[0];
    const size = caps.tiers.find(t => t.tier === tierKey)?.sizes[aspect];
    resolved = {
      mode: 'aspect', aspect, tier: tierKey, width: size?.w, height: size?.h,
      substituted: !!want && aspect !== want, rounded: false,
      note: want && aspect !== want ? `${want} нет у ${caps.modelLabel} — взяли ${aspect}.` : null,
    };
  }
  const tiers: FormatTierOption[] = (['standard', 'high', 'max'] as const).map(t => {
    const spec = caps.tiers.find(x => x.tier === t);
    if (!spec) return { tier: t, label: 'нет у модели', disabledReason: GREY };
    const size = resolved.aspect ? spec.sizes[resolved.aspect] : undefined;
    return { tier: t, label: spec.label, size, price: { amount: Math.round(PRICE * (spec.priceFactor ?? 1) * 100) / 100, unit: 'usd' } };
  });
  return {
    caps, wish: w, resolved, tiers, exact: caps.exact ?? null,
    exactUnavailableReason: caps.exact ? null : `«Точно…» недоступно: ${caps.modelLabel} рисует только три размера — 1024×1024, 1536×1024 и 1024×1536.`,
  };
}
