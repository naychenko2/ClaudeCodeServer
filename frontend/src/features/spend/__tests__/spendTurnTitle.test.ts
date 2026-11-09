// Подпись генерационной записи в списке анализа: «без картинок» не прячется за моделью.
import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { genTurnTitle, sourceLabel } from '../spendModel';
import { SPEND_LABEL_NO_IMAGES } from '../../../lib/spendContract';

const HERE = dirname(fileURLToPath(import.meta.url));

describe('genTurnTitle: подпись генерации в списке анализа', () => {
  it('запись без картинок — видна подпись, а не модель', () => {
    const t = genTurnTitle({ source: 'fal', model: 'fal-ai/flux', label: SPEND_LABEL_NO_IMAGES });
    expect(t).toBe(`${sourceLabel('fal')} · ${SPEND_LABEL_NO_IMAGES}`);
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

  it('константа фронта равна бэковой ImageGenerationService.SpendLabelNoImages', () => {
    const cs = readFileSync(
      join(HERE, '../../../../../backend/ClaudeHomeServer.Images/Services/ImageGenerationService.cs'), 'utf8');
    const m = cs.match(/SpendLabelNoImages\s*=\s*"([^"]+)"/);
    expect(m?.[1]).toBe(SPEND_LABEL_NO_IMAGES);
  });
});
