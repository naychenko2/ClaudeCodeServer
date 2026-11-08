// Подпись генерационной записи в списке анализа: «без картинок» не прячется за моделью.
import { describe, it, expect } from 'vitest';
import { genTurnTitle, sourceLabel } from '../spendModel';

describe('genTurnTitle: подпись генерации в списке анализа', () => {
  it('запись без картинок — видна подпись, а не модель', () => {
    const t = genTurnTitle({ source: 'fal', model: 'fal-ai/flux', label: 'image-generation: без картинок' });
    expect(t).toBe(`${sourceLabel('fal')} · image-generation: без картинок`);
    expect(t).not.toContain('fal-ai/flux');
  });

  it('обычная запись — читается модель', () => {
    expect(genTurnTitle({ source: 'fal', model: 'fal-ai/flux', label: 'image-generation' }))
      .toBe(`${sourceLabel('fal')} · fal-ai/flux`);
  });

  it('без модели — подпись, без обоих — пусто после источника', () => {
    expect(genTurnTitle({ source: 'fal', model: null, label: 'x' })).toBe(`${sourceLabel('fal')} · x`);
    expect(genTurnTitle({ source: 'fal' })).toBe(`${sourceLabel('fal')} · `);
  });
});
