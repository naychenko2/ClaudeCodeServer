import { describe, it, expect, vi } from 'vitest';
import type { NoteSummary } from '../../types';

// lib/notes тянет realtime-обвязку (api, signalr, offline, кэш MarkdownViewer) —
// тестируем чистую groupNotesByFile, тяжёлые модули глушим заглушками.
vi.mock('../api', () => ({ api: { notes: { list: vi.fn(), folders: vi.fn() } } }));
vi.mock('../signalr', () => ({ joinUser: vi.fn(), onMessage: vi.fn(), onReconnected: vi.fn() }));
vi.mock('../../components/MarkdownViewer', () => ({ clearResolveCache: vi.fn() }));
vi.mock('../offline', () => ({ isOnline: () => false, OfflineError: class extends Error {}, subscribeOnline: vi.fn() }));
vi.mock('../notesOffline', () => ({ drainNotesOutbox: vi.fn(), overlayNotesList: vi.fn() }));

import { groupNotesByFile } from '../notes';

const P1 = 'proj-1';
const P2 = 'proj-2';

function note(id: string, source: string, file?: string | null): NoteSummary {
  return {
    id, title: id, source, sourceLabel: source, path: `${id}.md`, tags: [],
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
    file: file ?? undefined,
  };
}

describe('groupNotesByFile — привязки «файл → заметки»', () => {
  it('фильтрует по projectId и отбрасывает заметки без file:', () => {
    const map = groupNotesByFile([
      note('a', P1, 'src/App.cs'),
      note('b', P1),                    // без привязки
      note('c', 'personal', 'src/App.cs'),   // чужой источник
    ], P1);

    expect([...map.keys()]).toEqual(['src/App.cs']);
    expect(map.get('src/App.cs')!.map(n => n.id)).toEqual(['a']);
  });

  it('группирует несколько заметок одного файла', () => {
    const map = groupNotesByFile([
      note('a', P1, 'docs/readme.md'),
      note('b', P1, 'docs/readme.md'),
      note('c', P1, 'src/Program.cs'),
    ], P1);

    expect(map.size).toBe(2);
    expect(map.get('docs/readme.md')!.map(n => n.id)).toEqual(['a', 'b']);
    expect(map.get('src/Program.cs')!.map(n => n.id)).toEqual(['c']);
  });

  it('не склеивает одинаковые пути разных проектов', () => {
    const notes = [
      note('a', P1, 'src/App.cs'),
      note('b', P2, 'src/App.cs'),
    ];

    expect(groupNotesByFile(notes, P1).get('src/App.cs')!.map(n => n.id)).toEqual(['a']);
    expect(groupNotesByFile(notes, P2).get('src/App.cs')!.map(n => n.id)).toEqual(['b']);
  });

  it('нормализует обратные слеши в ключе', () => {
    const map = groupNotesByFile([note('a', P1, 'src\\App.cs')], P1);
    expect([...map.keys()]).toEqual(['src/App.cs']);
  });
});
