import { describe, expect, it } from 'vitest';
import type { ImageEditQuote, ImageEditQuoteRequest } from './api';
import { staleEstimate } from './useQuote';

const req = (patch: Partial<ImageEditQuoteRequest> = {}): ImageEditQuoteRequest => ({
  provider: 'higgsfield', model: 'auto', mode: 'auto', op: 'generate', count: 2,
  hasMask: false, references: 0, hasCharacter: false, ...patch,
});
const quote = (amount: number | null): ImageEditQuote => ({
  quoteId: 'q1', provider: 'higgsfield', model: 'nano_banana_2',
  estimate: { amount, unit: 'credits', approx: false, source: 'provider' }, expiresAt: '', expectedSeconds: 12,
});

describe('цена, пока котировка едет заново', () => {
  it('добавили образец-стиль: цена той же модели остаётся, а не «Цена уточняется»', () => {
    const e = staleEstimate({ req: req(), quote: quote(4) }, req({ references: 1 }));
    expect(e).toMatchObject({ amount: 4, unit: 'credits', approx: true });
  });

  it('сменили число вариантов — пересчёт за картинку', () => {
    expect(staleEstimate({ req: req(), quote: quote(4) }, req({ count: 3 }))?.amount).toBe(6);
  });

  it('другая модель или поставщик, неизвестная цена — прошлой цены нет', () => {
    expect(staleEstimate({ req: req(), quote: quote(4) }, req({ model: 'soul_2' }))).toBeNull();
    expect(staleEstimate({ req: req(), quote: quote(4) }, req({ provider: 'fal' }))).toBeNull();
    expect(staleEstimate({ req: req(), quote: quote(null) }, req())).toBeNull();
    expect(staleEstimate(null, req())).toBeNull();
  });
});
