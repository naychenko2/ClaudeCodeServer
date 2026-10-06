import { describe, expect, it } from 'vitest';
import { buildQuoteBody, type QuoteBodyInput } from './quoteBody';

const input: QuoteBodyInput = {
  provider: 'fal', model: 'fal-ai/nano-banana-2/edit', mode: 'auto', op: 'generate', count: 1, hasImage: false, marks: [],
  withMask: false, removal: false, references: 0, hasCharacter: false, size: null,
};
const wish = { mode: 'aspect' as const, aspect: '16:9', tier: 'high' as const, by: 'human' as const, at: '2026-10-06T12:00:00Z' };

describe('тело котировки: формат (ADR-025)', () => {
  it('формат генерации уходит в тело явно: общий кэш котировок ключуется телом, и смена ступени пересчитывает цену', () => {
    expect(buildQuoteBody({ ...input, format: wish })).toMatchObject({ format: wish });
    expect(buildQuoteBody({ ...input, format: wish })).not.toEqual(buildQuoteBody({ ...input, format: { ...wish, tier: 'standard' } }));
  });

  it('флаг выключен (format не передан) — поля format в теле нет вовсе', () => {
    expect(buildQuoteBody(input)).not.toHaveProperty('format');
  });

  it('формат проекта не задан (null) — поле есть и пустое: сервер возьмёт «Как у модели»', () => {
    expect(buildQuoteBody({ ...input, format: null })).toMatchObject({ format: null });
  });
});
