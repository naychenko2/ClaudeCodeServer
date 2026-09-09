// Тесты чистой функции `groupNotesByFile` из lib/notes.ts.
// Хуки стора (`useNotes`, `useNotesByFile`) не покрываем здесь — они
// накручивают стор заметок поверх useSyncExternalStore и требуют живого
// окружения с подписками; это уже косвенно проверено в NoteConnections/
// NoteView. Источник истины для тестов — сами данные.

import { describe, it, expect } from 'vitest';
import type { NoteSummary } from '../types';
import { groupNotesByFile } from './notes';

// Минимальный конструктор тестовой заметки — все поля, нужные функции для
// фильтра (source, file) и сравнения путей. createdAt/updatedAt не важны
// для groupNotesByFile.
function note(partial: Partial<NoteSummary> & { id: string }): NoteSummary {
  return {
    id: partial.id,
    title: partial.title ?? `Title ${partial.id}`,
    path: partial.path ?? '',
    source: partial.source ?? 'personal',
    sourceLabel: partial.sourceLabel ?? 'Личный vault',
    tags: partial.tags ?? [],
    createdAt: partial.createdAt ?? '2026-01-01T00:00:00Z',
    updatedAt: partial.updatedAt ?? '2026-01-01T00:00:00Z',
    file: partial.file ?? null,
  };
}

describe('groupNotesByFile — группировка заметок по file-привязке', () => {
  it('возвращает пустую карту для пустого списка', () => {
    expect(groupNotesByFile([], 'project-1').size).toBe(0);
  });

  it('в одну группу попадают заметки одного файла одного проекта', () => {
    const notes: NoteSummary[] = [
      note({ id: 'a', file: 'src/foo.ts', source: 'project-1' }),
      note({ id: 'b', file: 'src/foo.ts', source: 'project-1' }),
    ];
    const map = groupNotesByFile(notes, 'project-1');
    expect(map.size).toBe(1);
    expect(map.get('src/foo.ts')?.map(n => n.id)).toEqual(['a', 'b']);
  });

  it('нормализует обратные слэши к прямым (Windows-пути)', () => {
    const notes: NoteSummary[] = [
      note({ id: 'a', file: 'src\\foo.ts', source: 'project-1' }),
      note({ id: 'b', file: 'src/foo.ts', source: 'project-1' }),
    ];
    const map = groupNotesByFile(notes, 'project-1');
    // Оба пути после нормализации равны — попадают в одну группу
    expect(map.size).toBe(1);
    expect(map.get('src/foo.ts')?.length).toBe(2);
  });

  it('отбрасывает заметки других проектов с тем же file', () => {
    const notes: NoteSummary[] = [
      note({ id: 'a', file: 'src/foo.ts', source: 'project-1' }),
      note({ id: 'b', file: 'src/foo.ts', source: 'project-2' }),
    ];
    const map = groupNotesByFile(notes, 'project-1');
    expect(map.size).toBe(1);
    expect(map.get('src/foo.ts')?.map(n => n.id)).toEqual(['a']);
  });

  it('отбрасывает заметки без file', () => {
    const notes: NoteSummary[] = [
      note({ id: 'a', file: null }),
      note({ id: 'b', file: 'src/foo.ts', source: 'project-1' }),
    ];
    const map = groupNotesByFile(notes, 'project-1');
    expect(map.size).toBe(1);
    expect(map.has('src/foo.ts')).toBe(true);
  });

  it('разносит разные файлы по разным группам', () => {
    const notes: NoteSummary[] = [
      note({ id: 'a', file: 'src/foo.ts', source: 'project-1' }),
      note({ id: 'b', file: 'src/bar.ts', source: 'project-1' }),
      note({ id: 'c', file: 'docs/notes.md', source: 'project-1' }),
    ];
    const map = groupNotesByFile(notes, 'project-1');
    expect(map.size).toBe(3);
    expect(map.get('src/foo.ts')?.map(n => n.id)).toEqual(['a']);
    expect(map.get('src/bar.ts')?.map(n => n.id)).toEqual(['b']);
    expect(map.get('docs/notes.md')?.map(n => n.id)).toEqual(['c']);
  });
});
