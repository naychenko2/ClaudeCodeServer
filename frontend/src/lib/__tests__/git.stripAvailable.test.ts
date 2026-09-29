import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { GitStatus } from '../../types';

// lib/git тянет realtime-обвязку (api, signalr) — глушим тяжёлые модули заглушками,
// как в git.changedBy.test.ts
const { statusMock } = vi.hoisted(() => ({ statusMock: vi.fn() }));
vi.mock('../api', () => ({
  api: { git: { status: statusMock }, files: { changedBy: vi.fn() } },
  getGitSessionContext: vi.fn(() => null),
}));
vi.mock('../signalr', () => ({
  joinUser: vi.fn(), onFilesChanged: vi.fn(), onGitStatusChanged: vi.fn(), onReconnected: vi.fn(),
}));

import { isGitStripAvailable, loadGitStatus } from '../git';

function repoStatus(isRepo: boolean): GitStatus {
  return {
    isRepo, branch: 'main', upstream: null, ahead: 0, behind: 0, detached: false,
    staged: [], unstaged: [], untracked: [], isWorktree: false,
  };
}

let seq = 0;
const freshProjectId = () => `proj-strip-${++seq}`;

describe('lib/git — isGitStripAvailable (доступность полосы Git над композером)', () => {
  beforeEach(() => { statusMock.mockReset(); });

  it('статус ещё не загружен — доступна (без дребезга «Картинки → Git» при открытии чата)', () => {
    expect(isGitStripAvailable(freshProjectId())).toBe(true);
  });

  it('статус загружен, isRepo — доступна', async () => {
    const projectId = freshProjectId();
    statusMock.mockResolvedValue(repoStatus(true));

    await loadGitStatus(projectId);

    expect(isGitStripAvailable(projectId)).toBe(true);
  });

  it('статус загружен, не репозиторий — недоступна (уступает «Картинкам»)', async () => {
    const projectId = freshProjectId();
    statusMock.mockResolvedValue(repoStatus(false));

    await loadGitStatus(projectId);

    expect(isGitStripAvailable(projectId)).toBe(false);
  });
});
