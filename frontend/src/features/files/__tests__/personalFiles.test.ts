import { describe, it, expect, vi, beforeEach } from 'vitest';

// «Мои файлы» личного чата (ADR-040 Ф2-9): псевдо-проект переключает api-слой на личный маршрут чата,
// а набор действий (маршрут 'personal') — только то, что умеет бэкенд. Мок lib/offline — как в
// apiFilesTaskAnchor.test: цепочка projectRequest → request настоящая.
const { request, readStoredToken } = vi.hoisted(() => ({
  request: vi.fn(),
  readStoredToken: vi.fn(() => 'tok'),
}));
vi.mock('../../../lib/offline', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/offline')>();
  return { ...orig, request, readStoredToken };
});

import { filesApi } from '../api';
import { isPersonalFilesId, personalFilesId, personalFilesProject, personalFilesSupports } from '../personalFiles';
import { projectFilesRoute, routeSupports } from '../../../lib/projectCapabilities';

const ID = personalFilesId('chat 1');

beforeEach(() => {
  request.mockReset();
  request.mockResolvedValue([]);
});

describe('личные файлы: id и маршрут', () => {
  it('id с префиксом — личные файлы, обычный id проекта — нет', () => {
    expect(isPersonalFilesId(ID)).toBe(true);
    expect(isPersonalFilesId('p1')).toBe(false);
    expect(isPersonalFilesId(null)).toBe(false);
    expect(personalFilesProject('c1').id).toBe(personalFilesId('c1'));
  });

  it('маршрут personal: просмотр, создание, переименование и удаление есть; загрузки, сохранения текста и git нет', () => {
    const p = personalFilesProject('c1');
    expect(projectFilesRoute(p)).toBe('personal');
    for (const r of ['GET files', 'GET files/tree', 'GET files/content', 'GET files/stream', 'POST files/mkdir',
      'POST files/create', 'POST files/rename', 'DELETE files'] as const)
      expect(routeSupports('personal', r), r).toBe(true);
    for (const r of ['PUT files/content', 'POST files/upload', 'POST files/revert', 'GET git/status',
      'POST files/document/to-markdown'] as const)
      expect(personalFilesSupports(r), r).toBe(false);
  });

  it('проект без префикса остаётся на сервере', () => {
    expect(projectFilesRoute({ ...personalFilesProject('c1'), id: 'p1' })).toBe('server');
  });
});

describe('личные файлы: адреса api-слоя', () => {
  it('список, дерево, поиск, содержимое — личный маршрут чата (id чата кодируется)', async () => {
    await filesApi.files.list(ID, 'images');
    await filesApi.files.tree(ID, '');
    await filesApi.files.search(ID, 'кот');
    await filesApi.files.getContent(ID, 'a b.png').catch(() => undefined);
    expect(request.mock.calls.map(c => c[0])).toEqual([
      '/personal/chats/chat%201/files?path=images',
      '/personal/chats/chat%201/files/tree?path=',
      '/personal/chats/chat%201/files/search?q=%D0%BA%D0%BE%D1%82',
      '/personal/chats/chat%201/files/content?path=a%20b.png',
    ]);
  });

  it('создание, переименование и удаление — личный маршрут', async () => {
    await filesApi.files.mkdir(ID, 'x');
    await filesApi.files.rename(ID, 'x', 'y');
    await filesApi.files.delete(ID, 'y');
    const calls = request.mock.calls as [string, { method?: string; body?: string }][];
    expect(calls[0][0]).toBe('/personal/chats/chat%201/files/mkdir');
    expect(JSON.parse(calls[1][1].body!)).toEqual({ oldPath: 'x', newPath: 'y' });
    expect(calls[2]).toEqual(['/personal/chats/chat%201/files?path=y', expect.objectContaining({ method: 'DELETE' })]);
  });

  it('поток и скачивание — адреса личного маршрута с токеном; проектный адрес не меняется', () => {
    expect(filesApi.files.fileUrl(ID, 'images/a.png')).toBe('/api/personal/chats/chat%201/files/stream?path=images%2Fa.png&access_token=tok');
    expect(filesApi.files.downloadUrl(ID, 'a.png')).toBe('/api/personal/chats/chat%201/files/download?path=a.png&access_token=tok');
    expect(filesApi.files.fileUrl('p1', 'a.png')).toBe('/api/projects/p1/files/stream?path=a.png&access_token=tok');
  });
});
