import { describe, it, expect } from 'vitest';
import { existingTitleSet } from '../notes';
import type { NoteSummary } from '../../types';

function note(title: string): NoteSummary {
  return { id: title, title, source: 'personal', sourceLabel: 'Личный', path: `${title}.md`, tags: [], createdAt: '', updatedAt: '' };
}

describe('existingTitleSet', () => {
  it('нормализует заголовки в lower/trim для сопоставления [[wikilinks]]', () => {
    const set = existingTitleSet([note('Идея про кэш'), note('Архитектура')]);
    expect(set.has('идея про кэш')).toBe(true);
    expect(set.has('архитектура')).toBe(true);
    expect(set.has('несуществующая')).toBe(false);
  });

  it('пустой список даёт пустой набор', () => {
    expect(existingTitleSet([]).size).toBe(0);
  });
});
