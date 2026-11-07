import { describe, it, expect, vi, beforeEach } from 'vitest';

// Основа конфликта сохранения файла (S2c-F2a, К8): getContent отдаёт ещё и etag прочитанной
// версии, saveContent с ifMatch шлёт If-Match, 409 file_changed становится FileConflictError
// с полями тела. request() мокаем сервером: заполняет etagRef и бросает ошибку с status/body
// — той же формой, что настоящий request() в offline.ts (его механизм etagRef тестится там).
const { request, readStoredToken } = vi.hoisted(() => ({
  request: vi.fn(),
  readStoredToken: vi.fn(() => 'tok'),
}));
// request() и readStoredToken() мокаем: тест задаёт поведение сервера. Остальное — настоящее:
// catch saveContent зовёт настоящий conflictBodyOf, цепочка 409 → FileConflictError проверяется по нему.
vi.mock('../offline', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../offline')>();
  return { ...orig, request, readStoredToken };
});

import { api } from '../api';
import { FileConflictError } from '../../features/files/api';

type ReqOpts = { method?: string; body?: string; headers?: Record<string, string>; etagRef?: { etag?: string } };

const contentBody = { content: 'текст файла', isBinary: false, isImage: false };

beforeEach(() => {
  request.mockReset();
});

describe('api.files.getContent — etag прочитанной версии', () => {
  it('текстовый файл: сервер отдал ETag — ответ несёт etag, поля прежние', async () => {
    request.mockImplementation((_url: string, options?: ReqOpts) => {
      if (options?.etagRef) options.etagRef.etag = '"aaa111"';
      return Promise.resolve(contentBody);
    });

    const r = await api.files.getContent('p1', 'a.md');
    expect(r.etag).toBe('"aaa111"');
    expect(r.content).toBe('текст файла');
    expect(r.isBinary).toBe(false);
  });

  it('URL запроса не изменился (featureId и sessionId — как раньше)', async () => {
    request.mockResolvedValue(contentBody);
    await api.files.getContent('p1', 'a.md', 'f1', { sessionId: 's1' });
    expect(request.mock.calls[0][0]).toBe('/projects/p1/files/content?path=a.md&featureId=f1&sessionId=s1');
  });

  it('без ETag (бинарник, документ, сверх 10 МиБ, агент/ретранслятор) — etag нет', async () => {
    request.mockResolvedValue({ content: null, isBinary: true, isImage: false });
    const r = await api.files.getContent('p1', 'v.mp4');
    expect(r.etag).toBeUndefined();
  });
});

describe('api.files.saveContent — If-Match и 409', () => {
  it('с ifMatch: If-Match уходит заголовком, тело прежнее', async () => {
    request.mockResolvedValue(undefined);
    await api.files.saveContent('p1', 'a.md', 'мой текст', '"aaa111"');

    const [url, options] = request.mock.calls[0] as [string, ReqOpts];
    expect(url).toBe('/projects/p1/files/content?path=a.md');
    expect(options.method).toBe('PUT');
    expect(JSON.parse(options.body!)).toEqual({ content: 'мой текст' });
    expect(options.headers?.['If-Match']).toBe('"aaa111"');
  });

  it('без ifMatch: заголовка If-Match нет — поведение ровно как раньше', async () => {
    request.mockResolvedValue(undefined);
    await api.files.saveContent('p1', 'a.md', 'мой текст');

    const [, options] = request.mock.calls[0] as [string, ReqOpts];
    expect(options.headers).toBeUndefined();
  });

  it('409 file_changed → FileConflictError с полями тела 409', async () => {
    request.mockRejectedValue(Object.assign(new Error('HTTP 409'), {
      status: 409,
      body: {
        code: 'file_changed',
        sha256: 'bbb222',
        content: 'чужой текст',
        author: null,
        changedAt: '2026-11-01T10:00:00Z',
        source: null,
      },
    }));

    const err = await api.files.saveContent('p1', 'a.md', 'мой текст', '"старый"').catch(e => e);
    expect(err).toBeInstanceOf(FileConflictError);
    expect(err.status).toBe(409);
    expect(err.code).toBe('file_changed');
    expect(err.sha256).toBe('bbb222');
    expect(err.content).toBe('чужой текст');
    expect(err.author).toBeNull();
    expect(err.changedAt).toBe('2026-11-01T10:00:00Z');
    expect(err.source).toBeNull();
  });

  it('409 с другим кодом — прежняя ошибка, не FileConflictError', async () => {
    request.mockRejectedValue(Object.assign(new Error('diverged'), { status: 409, body: { code: 'diverged' } }));
    const err = await api.files.saveContent('p1', 'a.md', 'мой', '"старый"').catch(e => e);
    expect(err).not.toBeInstanceOf(FileConflictError);
    expect(err.status).toBe(409);
  });

  it('200 с новым ETag — etagOut получает его для следующего сохранения', async () => {
    request.mockImplementation((_url: string, options?: ReqOpts) => {
      if (options?.etagRef) options.etagRef.etag = '"bbb222"';
      return Promise.resolve(undefined);
    });

    const etagOut: { etag?: string } = {};
    await api.files.saveContent('p1', 'a.md', 'мой текст', '"aaa111"', etagOut);
    expect(etagOut.etag).toBe('"bbb222"');
  });

  it('200 без ETag (нынешний PUT файлов) — etagOut остаётся пустым', async () => {
    request.mockResolvedValue(undefined);
    const etagOut: { etag?: string } = {};
    await api.files.saveContent('p1', 'a.md', 'мой текст', '"aaa111"', etagOut);
    expect(etagOut.etag).toBeUndefined();
  });
});
