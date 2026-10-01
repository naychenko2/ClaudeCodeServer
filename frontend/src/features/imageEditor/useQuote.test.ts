import { beforeEach, describe, expect, it } from 'vitest';
import type { ImageEditorApi, ImageEditQuote, ImageEditQuoteRequest } from './api';
import { __resetSharedQuotes, sharedQuote, staleEstimate } from './useQuote';

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

describe('одинаковая котировка у нескольких хозяев', () => {
  const api = (calls: ImageEditQuoteRequest[]) =>
    ({ quote: (_p: string, r: ImageEditQuoteRequest) => { calls.push(r); return Promise.resolve(quote(4)); } }) as unknown as ImageEditorApi;

  beforeEach(() => __resetSharedQuotes());

  it('полоса, панель, поле ввода и карточка спросили одно и то же — на сервер один запрос', async () => {
    const calls: ImageEditQuoteRequest[] = [];
    const a = api(calls);
    const all = await Promise.all([1, 2, 3, 4].map(() => sharedQuote(a, 'p1', req())));
    expect(calls).toHaveLength(1);
    expect(all.every(q => q.estimate.amount === 4)).toBe(true);
    await sharedQuote(a, 'p1', req());
    expect(calls).toHaveLength(1);
  });

  it('другой запрос или другой проект — свой запрос', async () => {
    const calls: ImageEditQuoteRequest[] = [];
    const a = api(calls);
    await Promise.all([sharedQuote(a, 'p1', req()), sharedQuote(a, 'p1', req({ count: 3 })), sharedQuote(a, 'p2', req())]);
    expect(calls).toHaveLength(3);
  });

  it('ошибка не запоминается: следующий запрос идёт заново', async () => {
    let n = 0;
    const a = { quote: () => (++n === 1 ? Promise.reject(new Error('сеть')) : Promise.resolve(quote(4))) } as unknown as ImageEditorApi;
    await expect(sharedQuote(a, 'p1', req())).rejects.toThrow('сеть');
    await expect(sharedQuote(a, 'p1', req())).resolves.toMatchObject({ quoteId: 'q1' });
    expect(n).toBe(2);
  });
});
