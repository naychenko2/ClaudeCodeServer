import { describe, it, expect, vi, beforeEach } from 'vitest';

// Методы git строят URL через gq(projectId) в features/git/api.ts; состояние контекста
// worktree-чата — единственный экземпляр там, lib/api.ts лишь переэкспортирует сеттер.
// Проверка через шпион request (projectRequest для неизвестного проекта идёт в
// offline.request): URL api.git.* следует выставленному контексту.
const { request, readStoredToken, writeStoredToken } = vi.hoisted(() => ({
  request: vi.fn(),
  readStoredToken: vi.fn(() => 'tok'),
  writeStoredToken: vi.fn(),
}));
vi.mock('../../lib/offline', () => ({ request, readStoredToken, writeStoredToken }));

import { api, setGitSessionContext } from '../../lib/api';

const lastUrl = () => (request.mock.calls[0] as [string])[0];

describe('Контекст worktree-чата для git-запросов', () => {
  beforeEach(() => {
    request.mockReset();
    request.mockResolvedValue({});
    setGitSessionContext('p1', null); // контекст не течёт между тестами
  });

  it('контекст («p1», «s1») → git-URL p1 несёт sessionId=s1', async () => {
    setGitSessionContext('p1', 's1');
    await api.git.status('p1');
    expect(lastUrl()).toBe('/projects/p1/git/status?sessionId=s1');
  });

  it('контекст («p1», «s1»), вызов для p2 → URL без sessionId', async () => {
    setGitSessionContext('p1', 's1');
    await api.git.status('p2');
    expect(lastUrl()).toBe('/projects/p2/git/status');
  });

  it('setGitSessionContext(«p1», null) → URL без sessionId', async () => {
    setGitSessionContext('p1', 's1');
    setGitSessionContext('p1', null);
    await api.git.status('p1');
    expect(lastUrl()).toBe('/projects/p1/git/status');
  });
});
