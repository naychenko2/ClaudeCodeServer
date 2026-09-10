// Тесты чистой функции `groupNotesByFile` из lib/notes.ts.
// Хуки стора (`useNotes`, `useNotesByFile`) не покрываем здесь — они
// накручивают стор заметок поверх useSyncExternalStore и требуют живого
// окружения с подписками; это уже косвенно проверено в NoteConnections/
// NoteView. Источник истины для тестов — сами данные.

import { describe, it, expect, vi } from 'vitest';
import type { NoteSummary } from '../types';
import { groupNotesByFile } from './notes';

// Гейт подсистемы заметок на `ensureNotesLoaded`: при выключенной notes
// функция не должна обращаться к /api/notes* (иначе при выключенной подсистеме
// каждый вызов даёт 500 в консоли — см. Д-4/Д-6 отчёта QA).
// Тест мокает api и isSubsystemEnabled и проверяет, что при выключенной notes
// обращений к api не происходит, а при включённой — происходит обычным путём.
vi.mock('./api', () => ({
  api: {
    notes: {
      list: vi.fn(async () => []),
      folders: vi.fn(async () => []),
    },
  },
}));
vi.mock('./signalr', () => ({
  joinUser: vi.fn(async () => {}),
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
}));
vi.mock('../components/MarkdownViewer', () => ({
  clearResolveCache: vi.fn(),
}));
vi.mock('./offline', () => ({
  isOnline: () => true,
  OfflineError: class extends Error {},
  subscribeOnline: vi.fn(() => () => {}),
}));
vi.mock('./notesOffline', () => ({
  drainNotesOutbox: vi.fn(async () => {}),
  overlayNotesList: vi.fn(async (l: unknown) => l),
}));

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

// Тест гейта: при выключенной подсистеме `ensureNotesLoaded` НЕ должен
// дёргать api.notes.list() — иначе консоль шумит 500-ками (см. Д-4/Д-6 QA).
// Тест на «включена → обращается» не делаем: `ensureNotesLoaded` в включённом
// режиме зависит от localStorage/sessionStorage (joinUserGroup), а vitest
// работает в node-окружении — регрессия гейта тут не проверится, зато
// непрошный тест будет ломаться при любом пересечении с DOM.
describe('ensureNotesLoaded — гейт подсистемы', () => {
  it('при выключенной подсистеме НЕ обращается к /api/notes*', async () => {
    vi.resetModules();
    vi.doMock('./subsystems', () => ({
      isSubsystemEnabled: () => false,
      setAllSubsystems: () => {},
      getAllSubsystems: () => ({}),
      subscribeSubsystems: () => () => {},
      useSubsystem: () => false,
    }));
    const { ensureNotesLoaded } = await import('./notes');
    const { api } = await import('./api');
    await ensureNotesLoaded();
    expect(api.notes.list).not.toHaveBeenCalled();
  });
});

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
