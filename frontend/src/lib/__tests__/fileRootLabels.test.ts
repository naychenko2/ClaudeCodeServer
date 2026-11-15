// Подписи корня дерева файлов: единый источник для проекта и личной панели.
// Проектные тексты — прежние, личные — без слова «проект» (в личном чате проекта нет).

import { describe, it, expect } from 'vitest';
import { PERSONAL_ROOT_LABELS, PROJECT_ROOT_LABELS } from '../fileRootLabels';

describe('подписи корня дерева файлов', () => {
  it('проект: прежние тексты, слово «проект» есть', () => {
    expect(PROJECT_ROOT_LABELS.short).toBe('корень проекта');
    expect(PROJECT_ROOT_LABELS.inRoot).toBe('В корне проекта');
    expect(PROJECT_ROOT_LABELS.inRootAction).toBe('Создавать в корне проекта');
    expect(PROJECT_ROOT_LABELS.list).toBe('/ (корень проекта)');
    expect(PROJECT_ROOT_LABELS.emptyTitle).toBe('Проект пуст');
    expect(PROJECT_ROOT_LABELS.emptyHint).toBe('Здесь пока нет файлов');
    expect(PROJECT_ROOT_LABELS.filesTipTitle).toBe('Файлы и структура проекта');
  });

  it('личная панель: слова «проект» нет ни в одной подписи', () => {
    const all = Object.values(PERSONAL_ROOT_LABELS).join(' ');
    expect(all.toLowerCase()).not.toContain('проект');
    expect(PERSONAL_ROOT_LABELS.emptyTitle).toBe('Личное пусто');
    expect(PERSONAL_ROOT_LABELS.filesTipTitle).toBe('Файлы и структура Личного');
    expect(PERSONAL_ROOT_LABELS.filesTipText).toContain('«Личного»');
  });
});
