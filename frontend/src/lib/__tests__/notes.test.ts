import { describe, it, expect } from 'vitest';
import { existingTitleSet, isFavorite, withFavoriteTag, withoutFavoriteTag } from '../notes';
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

describe('избранное (тег в тексте заметки)', () => {
  it('дописывает тег в конец тела и не дублирует его', () => {
    const once = withFavoriteTag('Текст заметки');
    expect(once).toBe('Текст заметки #избранное\n');
    expect(withFavoriteTag(once)).toBe(once);
  });

  it('снимает inline-тег, не трогая однокоренные', () => {
    expect(withoutFavoriteTag('Начало #избранное конец')).toBe('Начало конец');
    expect(withoutFavoriteTag('Мысль #избранное-2')).toBe('Мысль #избранное-2');
  });

  it('снимает тег из frontmatter — и из inline-списка, и из столбика', () => {
    expect(withoutFavoriteTag('---\ntags: [идея, избранное, ccs]\n---\nТело\n'))
      .toBe('---\ntags: [идея, ccs]\n---\nТело\n');
    expect(withoutFavoriteTag('---\ntags: [избранное]\n---\nТело\n'))
      .toBe('---\n---\nТело\n');
    expect(withoutFavoriteTag('---\ntags:\n  - идея\n  - избранное\n---\nТело\n'))
      .toBe('---\ntags:\n  - идея\n---\nТело\n');
  });

  it('isFavorite не зависит от регистра', () => {
    expect(isFavorite(['идея', 'Избранное'])).toBe(true);
    expect(isFavorite(['идея'])).toBe(false);
  });
});
