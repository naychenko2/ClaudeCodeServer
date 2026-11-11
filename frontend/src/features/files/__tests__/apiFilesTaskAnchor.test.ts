import { describe, it, expect, vi, beforeEach } from 'vitest';

// FS-F1a: клиент передаёт задачу-автора — getContent/fileUrl/locate с taskId ставят
// taskId в запрос и НЕ шлют sessionId (бэк отвергает два анкера сразу 400:
// FilesController.Stream/Locate). 404 с reason (FS-B2: branch_deleted/binary)
// доходит до вызывающего полем reason, а не исключением; прочие 404 — ошибка, как раньше.
// Мок и форма ошибки — как в apiFilesConflict.test: мокается lib/offline, цепочка
// projectRequest → request настоящая (для проекта без устройства маршрут — server).
const { request, readStoredToken } = vi.hoisted(() => ({
  request: vi.fn(),
  readStoredToken: vi.fn(() => 'tok'),
}));
vi.mock('../../../lib/offline', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/offline')>();
  return { ...orig, request, readStoredToken };
});

import { filesApi } from '../api';

type ReqOpts = { method?: string; body?: string };

const contentBody = { content: 'текст файла', isBinary: false, isImage: false };

beforeEach(() => {
  request.mockReset();
});

describe('filesApi.files.getContent — анкер задачи (taskId)', () => {
  it('с taskId: URL несёт taskId (encodeURIComponent) и не несёт sessionId', async () => {
    request.mockResolvedValue(contentBody);
    await filesApi.files.getContent('p1', 'a.md', undefined, { sessionId: 's1', taskId: 't/1' });
    expect(request.mock.calls[0][0]).toBe('/projects/p1/files/content?path=a.md&taskId=t%2F1');
  });

  it('без taskId: featureId и sessionId — как раньше', async () => {
    request.mockResolvedValue(contentBody);
    await filesApi.files.getContent('p1', 'a.md', 'f1', { sessionId: 's1' });
    expect(request.mock.calls[0][0]).toBe('/projects/p1/files/content?path=a.md&featureId=f1&sessionId=s1');
  });

  it('из коммита ветки (FS-B2): fromCommit и branch доходят до вызывающего', async () => {
    request.mockResolvedValue({ content: 'текст из коммита', isBinary: false, isImage: false, branch: 'feat/x', fromCommit: true });
    const r = await filesApi.files.getContent('p1', 'a.md', undefined, { taskId: 't1' });
    expect(r.fromCommit).toBe(true);
    expect(r.branch).toBe('feat/x');
    expect(r.content).toBe('текст из коммита');
  });

  it('404 {reason: branch_deleted} — не исключение: reason доходит до вызывающего', async () => {
    request.mockRejectedValue(Object.assign(new Error('HTTP 404'), { status: 404, body: { reason: 'branch_deleted' } }));
    const r = await filesApi.files.getContent('p1', 'a.md', undefined, { taskId: 't1' });
    expect(r.reason).toBe('branch_deleted');
    expect(r.content).toBeNull();
  });

  it('404 {reason: binary} — тот же приём: поле reason', async () => {
    request.mockRejectedValue(Object.assign(new Error('HTTP 404'), { status: 404, body: { reason: 'binary' } }));
    const r = await filesApi.files.getContent('p1', 'a.md', undefined, { taskId: 't1' });
    expect(r.reason).toBe('binary');
  });

  it('обычный 404 без reason — остаётся исключением, как раньше', async () => {
    request.mockRejectedValue(Object.assign(new Error('Файл не найден ни в одном дереве проекта'), { status: 404, body: { error: 'Файл не найден ни в одном дереве проекта' } }));
    const e = await filesApi.files.getContent('p1', 'a.md', undefined, { taskId: 't1' }).catch((x: unknown) => x);
    expect(e).toBeInstanceOf(Error);
    expect((e as Error).message).toBe('Файл не найден ни в одном дереве проекта');
  });
});

describe('filesApi.files.fileUrl — анкер задачи', () => {
  it('с taskId: ?taskId= в URL, sessionId не шлётся', () => {
    expect(filesApi.files.fileUrl('p1', 'img.png', 's1', 't1')).toBe('/api/projects/p1/files/stream?path=img.png&taskId=t1&access_token=tok');
  });

  it('без taskId: sessionId — как раньше', () => {
    expect(filesApi.files.fileUrl('p1', 'img.png', 's1')).toBe('/api/projects/p1/files/stream?path=img.png&sessionId=s1&access_token=tok');
  });
});

describe('filesApi.files.locate — анкер задачи', () => {
  it('с taskId: тело несёт taskId и не несёт sessionId', async () => {
    request.mockResolvedValue([]);
    await filesApi.files.locate('p1', ['a.md', 'b.md'], 's1', 't1');
    const [url, options] = request.mock.calls[0] as [string, ReqOpts];
    expect(url).toBe('/projects/p1/files/locate');
    expect(JSON.parse(options.body!)).toEqual({ paths: ['a.md', 'b.md'], taskId: 't1' });
  });

  it('без taskId: тело с sessionId — как раньше', async () => {
    request.mockResolvedValue([]);
    await filesApi.files.locate('p1', ['a.md'], 's1');
    expect(JSON.parse((request.mock.calls[0][1] as ReqOpts).body!)).toEqual({ paths: ['a.md'], sessionId: 's1' });
  });
});
