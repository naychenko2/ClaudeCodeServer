import { describe, it, expect, vi, beforeEach } from 'vitest';

// К2b (хвост ревью b4e2b922): цепочка 409 → NoteConflictError на РЕАЛЬНОМ notesApi.notes.update.
// NoteViewSaveConflict.test.ts подменяет notesApi.update через vi.spyOn — разбор
// conflictBodyOf(e, 'note_changed') внутри update в тестах не исполнялся ни разу.
// Здесь наоборот: request() мокаем сервером (та же форма, что настоящий request() в
// offline.ts — его механизм etagRef тестится там), notesApi.notes.update настоящий,
// его catch зовёт настоящий conflictBodyOf из lib/offline.
// Тест доменного api живёт в каталоге домена (хвост ревью R-S5): notesApi и NoteConflictError
// — локальный импорт ../api; рядом с NoteViewSaveConflict.test.ts, который этот же путь мокает.
const { request, readStoredToken } = vi.hoisted(() => ({
  request: vi.fn(),
  readStoredToken: vi.fn(() => 'tok'),
}));
// request() и readStoredToken() мокаем: тест задаёт поведение сервера. Всё остальное — настоящее.
vi.mock('../../../lib/offline', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/offline')>();
  return { ...orig, request, readStoredToken };
});

import { notesApi, NoteConflictError } from '../api';

type ReqOpts = { method?: string; body?: string; headers?: Record<string, string>; etagRef?: { etag?: string } };

const noteBody = {
  id: 'n1', title: 'Заметка', source: 'personal', sourceLabel: 'Личный', path: 'a.md',
  content: 'текст заметки', tags: [], links: [], backlinks: [], unlinkedMentions: [],
};

beforeEach(() => {
  request.mockReset();
});

describe('notesApi.notes.update — If-Match и 409 (реальная цепочка)', () => {
  it('с ifMatch: If-Match уходит заголовком, URL и тело прежние', async () => {
    request.mockResolvedValue(noteBody);
    await notesApi.notes.update('n1', { content: 'мой текст' }, '"aaa111"');

    const [url, options] = request.mock.calls[0] as [string, ReqOpts];
    expect(url).toBe('/notes/n1');
    expect(options.method).toBe('PUT');
    expect(JSON.parse(options.body!)).toEqual({ content: 'мой текст' });
    expect(options.headers?.['If-Match']).toBe('"aaa111"');
  });

  it('409 note_changed → NoteConflictError с полями тела 409', async () => {
    request.mockRejectedValue(Object.assign(new Error('HTTP 409'), {
      status: 409,
      body: {
        code: 'note_changed',
        sha256: 'bbb222',
        content: 'чужой текст',
        author: { userId: 'u1', displayName: 'Андрей' },
        changedAt: '2026-11-01T10:00:00Z',
        source: { chatTitle: 'Чат', private: true },
      },
    }));

    const err = await notesApi.notes.update('n1', { content: 'мой текст' }, '"старый"').catch(e => e);
    expect(err).toBeInstanceOf(NoteConflictError);
    expect(err.status).toBe(409);
    expect(err.code).toBe('note_changed');
    expect(err.sha256).toBe('bbb222');
    expect(err.content).toBe('чужой текст');
    // author/changedAt — ненулевыми значениями: бэк заполняет их с S3-3, тест это держит
    expect(err.author).toEqual({ userId: 'u1', displayName: 'Андрей' });
    expect(err.changedAt).toBe('2026-11-01T10:00:00Z');
    expect(err.source).toEqual({ chatTitle: 'Чат', private: true });
  });

  it('409 с другим кодом — прежняя ошибка, не NoteConflictError', async () => {
    request.mockRejectedValue(Object.assign(new Error('diverged'), { status: 409, body: { code: 'diverged' } }));
    const err = await notesApi.notes.update('n1', { content: 'мой' }, '"старый"').catch(e => e);
    expect(err).not.toBeInstanceOf(NoteConflictError);
    expect(err.message).toBe('diverged');
    expect(err.status).toBe(409);
  });

  it('200 с новым ETag — etagOut и ответ получают его для следующего сохранения', async () => {
    request.mockImplementation((_url: string, options?: ReqOpts) => {
      if (options?.etagRef) options.etagRef.etag = '"bbb222"';
      return Promise.resolve(noteBody);
    });

    const etagOut: { etag?: string } = {};
    const r = await notesApi.notes.update('n1', { content: 'мой текст' }, '"aaa111"', etagOut);
    expect(etagOut.etag).toBe('"bbb222"');
    expect(r.etag).toBe('"bbb222"');
  });

  it('без ifMatch: заголовка If-Match нет — поведение ровно как раньше', async () => {
    request.mockResolvedValue(noteBody);
    await notesApi.notes.update('n1', { content: 'мой текст' });

    const [, options] = request.mock.calls[0] as [string, ReqOpts];
    expect(options.method).toBe('PUT');
    expect(options.headers).toBeUndefined();
  });

  it('200 без ETag — etag в ответе и в etagOut не появляется', async () => {
    request.mockResolvedValue(noteBody);

    const etagOut: { etag?: string } = {};
    const r = await notesApi.notes.update('n1', { content: 'мой текст' }, '"aaa111"', etagOut);
    expect(etagOut.etag).toBeUndefined();
    expect(r.etag).toBeUndefined();
    expect(r.content).toBe('текст заметки');
  });
});
