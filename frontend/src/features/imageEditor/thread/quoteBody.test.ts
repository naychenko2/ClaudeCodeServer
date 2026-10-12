import { describe, expect, it } from 'vitest';
import { buildQuoteBody, type QuoteBodyInput } from './quoteBody';
import type { FaceTarget } from '../api';

const input: QuoteBodyInput = {
  provider: 'fal', model: 'fal-ai/nano-banana-2/edit', mode: 'auto', op: 'generate', count: 1, hasImage: false, marks: [],
  withMask: false, removal: false, references: 0, hasCharacter: false, size: null,
};
const wish = { mode: 'aspect' as const, aspect: '16:9', tier: 'high' as const, by: 'human' as const, at: '2026-10-06T12:00:00Z' };
const faceTarget: FaceTarget = { versionId: 'v2', face: 2, facesKey: 'fk-1' };

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

describe('тело котировки: «Подставить персонажа» (этап 2 · шаг 10a)', () => {
  it('с faceTarget: цель и персонаж в теле, ContextRevision (sessionId/contextRevision) не едут', () => {
    const body = buildQuoteBody({
      ...input,
      context: { sessionId: 's1', contextRevision: 7 },
      faceTarget,
      characterSlug: 'marina',
    });
    expect(body).toMatchObject({ faceTarget, characterSlug: 'marina' });
    // ContextRevision вместе с FaceTarget несовместим: сервер вернёт invalid_request
    expect(body).not.toHaveProperty('contextRevision');
    expect(body).not.toHaveProperty('sessionId');
  });

  it('без faceTarget: тело прежнее (нет faceTarget/characterSlug, context на месте) — нет регрессии', () => {
    const body = buildQuoteBody({
      ...input,
      context: { sessionId: 's1', contextRevision: 7 },
    });
    expect(body).toMatchObject({ sessionId: 's1', contextRevision: 7 });
    expect(body).not.toHaveProperty('faceTarget');
    expect(body).not.toHaveProperty('characterSlug');
  });

  it('faceTarget без персонажа: characterSlug пустой (обязателен, но ещё не выбран)', () => {
    // Клиент в продукте не отправляет запрос котировки с faceTarget и без персонажа
    // (canSubmit ложен); если отправит — сервер вернёт invalid_request
    const body = buildQuoteBody({ ...input, faceTarget });
    expect(body).toMatchObject({ faceTarget, characterSlug: null });
  });

  it('faceTarget + context одновременно: context отброшен, faceTarget и characterSlug на месте', () => {
    const body = buildQuoteBody({
      ...input,
      context: { sessionId: 's1', contextRevision: 7 },
      faceTarget,
      characterSlug: 'marina',
    });
    // ContextRevision не едет вместе с FaceTarget
    expect(body).not.toHaveProperty('sessionId');
    expect(body).not.toHaveProperty('contextRevision');
    // FaceTarget и characterSlug на месте
    expect(body).toMatchObject({ faceTarget, characterSlug: 'marina' });
  });
});
