// Общий кэш списка проектов (QA-1): отзыв доступа убирает проект из кэша без запроса —
// в офлайне load() оставил бы прежний список, а строка отозванного проекта в зоне шапки
// и палитре кликалась бы в 404. Модуль с состоянием — перечитаем на каждый тест.
import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { Project } from '../../../types';

const { apiMock } = vi.hoisted(() => ({
  apiMock: { projects: { list: vi.fn() } },
}));
vi.mock('../../../lib/api', () => ({ api: apiMock }));
vi.mock('../../../lib/pinnedProjects', () => ({ __esModule: true, recordRecentProject: () => {} }));
vi.mock('../../../lib/imageBackfill', () => ({ __esModule: true, IMAGE_PLACE: { icon: 'icon' }, onImageBackfilled: () => () => {} }));

const mk = (id: string): Project => ({
  id, name: `Проект ${id}`, rootPath: `/p/${id}`,
  createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
  tagRegistry: [], subjectRegistry: [], subjectsSweepSince: null,
} as Project);

let mod: typeof import('../useAllProjects');

beforeEach(async () => {
  vi.resetModules();
  apiMock.projects.list.mockReset();
  apiMock.projects.list.mockResolvedValue([mk('p1'), mk('p2')]);
  mod = await import('../useAllProjects');
});

describe('dropProjectFromCache: отзыв доступа убирает проект из общего кэша', () => {
  it('без повторного запроса; последующие чтения отдают урезанный список', async () => {
    const first = await mod.ensureProjectsLoaded();
    expect(first.map(p => p.id)).toEqual(['p1', 'p2']);

    mod.dropProjectFromCache('p2');

    expect(mod.getCachedProject('p2'), 'отозванный проект остался в кэше').toBeUndefined();
    const again = await mod.ensureProjectsLoaded(); // кэш свежий — нового запроса быть не должно
    expect(again.map(p => p.id)).toEqual(['p1']);
    expect(apiMock.projects.list, 'лишний запрос к серверу').toHaveBeenCalledTimes(1);
  });

  it('несуществующий id кэш не трогает', async () => {
    await mod.ensureProjectsLoaded();
    mod.dropProjectFromCache('нет-такого');
    expect((await mod.ensureProjectsLoaded()).map(p => p.id)).toEqual(['p1', 'p2']);
  });
});
