import { describe, expect, it } from 'vitest';
import { FORMAT_TEXT, generateChip, priceLabel, tierCaption } from './formatText';
import type { MediaFormatState } from './formatTypes';

const base = (resolved: MediaFormatState['resolved'], caps = true): MediaFormatState => ({
  caps: caps ? { modelLabel: 'FLUX', aspects: ['16:9'], tiers: [] } : null, wish: null, resolved, tiers: [],
});

describe('подписи чипа «Формат» (таблица «Тексты чипа» записки)', () => {
  it('новая картинка: «16:9 · 1792×1024», на телефоне хвост «· 1792»', () => {
    const c = generateChip(base({ mode: 'aspect', aspect: '16:9', tier: 'high', width: 1792, height: 1024, substituted: false, rounded: false }));
    expect(c).toMatchObject({ main: '16:9', sub: '· 1792×1024', subShort: '· 1792', tone: 'normal' });
  });

  it('точный размер: «1920×1088 · точно», жёлтый, если было округление', () => {
    const exact = { mode: 'exact' as const, width: 1920, height: 1088, substituted: false };
    expect(generateChip(base({ ...exact, rounded: true }))).toMatchObject({ main: '1920×1088', sub: '· точно', tone: 'warn' });
    expect(generateChip(base({ ...exact, rounded: false })).tone).toBe('normal');
  });

  it('модель не сообщила размеры: «Формат Как у модели», приглушён', () => {
    expect(generateChip(base({ mode: 'model', substituted: false, rounded: false }, false)))
      .toEqual({ prefix: 'Формат', main: 'Как у модели', tone: 'dim' });
  });

  it('ответ ручки ещё не пришёл: заглушка, а не «Как у модели»', () => {
    expect(generateChip(null)).toMatchObject({ main: FORMAT_TEXT.loading, tone: 'dim' });
  });
});

describe('цена и размер под ступенью', () => {
  it('«$0.08», «~$0.08», кредиты, бесплатно', () => {
    expect(priceLabel({ amount: 0.08, unit: 'usd' })).toBe('$0.08');
    expect(priceLabel({ amount: 0.08, unit: 'usd', approx: true })).toBe('~$0.08');
    expect(priceLabel({ amount: 2, unit: 'credits' })).toBe('2 кр.');
    expect(priceLabel({ amount: 0, unit: 'free' })).toBe('бесплатно');
    expect(priceLabel(null)).toBe('');
  });

  it('серая ступень — «нет у модели» без цены', () => {
    expect(tierCaption({ tier: 'max', label: 'нет у модели', disabledReason: 'причина' })).toEqual({ size: 'нет у модели', price: '' });
    expect(tierCaption({ tier: 'high', label: '2 Мп', size: { w: 1792, h: 1024 }, price: { amount: 0.08, unit: 'usd' } }))
      .toEqual({ size: '1792×1024', price: '$0.08' });
  });
});
