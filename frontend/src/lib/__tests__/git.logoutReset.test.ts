import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { GitStatus, GitLogEntry, GitBranchInfo, GitStashEntry } from '../../types';

// Дефект bfaa0df1: git-стор живёт в module-level _state (lib/git.ts) и переживает выход
// из аккаунта — logout в App.tsx идёт без location.reload. Сценарий: взрослый открыл
// проект (в кеше status/ветки/stash/unpushed) → logout → логин ребёнка в том же проекте:
// ребёнок видел кеш взрослого, а canPublish панели «Изменения» (GitChangesRail:
// ahead || unpushed) становился истинным → активная кнопка публикации → 403.
//
// Сброс регистрируется самим git.ts через onLogoutReset (тот же паттерн, что
// useCharacters): App зовёт runLogoutResets на ОБОИХ путях выхода (явный logout и
// принудительный по cc-unauthorized) — проводка App проверена в useCharacters.test.ts
// на том же runLogoutResets, здесь — сам сброс стора.
//
// Обвязку (api/signalr) глушим заглушками, как в git.changedBy.test.ts.

const m = vi.hoisted(() => ({
  status: vi.fn(), unpushed: vi.fn(), branches: vi.fn(), stashList: vi.fn(), changedBy: vi.fn(),
  joinUser: vi.fn(), onFilesChanged: vi.fn(), onGitStatusChanged: vi.fn(), onReconnected: vi.fn(),
}));
vi.mock('../api', () => ({
  api: { git: { status: m.status, unpushed: m.unpushed, branches: m.branches, stashList: m.stashList }, files: { changedBy: m.changedBy } },
  getGitSessionContext: () => null,
}));
vi.mock('../signalr', () => ({
  joinUser: m.joinUser, onFilesChanged: m.onFilesChanged, onGitStatusChanged: m.onGitStatusChanged, onReconnected: m.onReconnected,
}));

import { ensureGit, getGitState, loadGitBranches, loadGitStash, loadUnpushedLog, loadGitStatus, resetGitState } from '../git';
import { runLogoutResets } from '../logoutReset';

const adultStatus = (): GitStatus => ({
  isRepo: true, branch: 'main', upstream: 'origin/main', ahead: 2, behind: 0, detached: false,
  staged: [{ path: 'src/a.ts', status: 'M' }], unstaged: [], untracked: [], isWorktree: false,
});
const childStatus = (): GitStatus => ({
  isRepo: true, branch: 'main', upstream: 'origin/main', ahead: 0, behind: 0, detached: false,
  staged: [], unstaged: [], untracked: [], isWorktree: false,
});
const unpushedCommit: GitLogEntry = { sha: 'a'.repeat(40), shortSha: 'aaaaaaa', author: 'Взрослый', email: 'a@x', date: '2026-11-20T00:00:00Z', subject: 'правка взрослого' };
const adultBranch: GitBranchInfo = { name: 'feat/adult-work', current: false, upstream: 'origin/feat/adult-work' };
const adultStash: GitStashEntry = { index: 0, message: 'WIP взрослого', date: '2026-11-20T00:00:00Z' };

const settle = () => new Promise<void>(r => setTimeout(r, 0));
let seq = 0;
const freshProjectId = () => `proj-${++seq}`;

// Открыть проект так, как его открывает взрослый: ensureGit (статус) + загрузки панели
// «Изменения» (незапушенные, ветки, стэши) — и дождаться сетевой цепочки.
// flush — как дождаться сетевой цепочки: обычно микротаск+таймер 0, а в тесте с
// фейковыми таймерами тот же спуск таймеров идёт через advanceTimersByTimeAsync
// (обычный setTimeout под fake timers не резолвится).
async function openProjectAsAdult(projectId: string, flush: () => Promise<unknown> = settle) {
  m.status.mockResolvedValue(adultStatus());
  m.unpushed.mockResolvedValue([unpushedCommit]);
  m.branches.mockResolvedValue([adultBranch]);
  m.stashList.mockResolvedValue([adultStash]);
  m.changedBy.mockResolvedValue({ files: {} });
  ensureGit(projectId);
  await Promise.all([loadUnpushedLog(projectId), loadGitBranches(projectId), loadGitStash(projectId)]);
  await flush();
}

// Формула canPublish панели «Изменения» (GitChangesRail: ahead || unpushed) — по состоянию стора
const canPublishOf = (projectId: string) => {
  const st = getGitState(projectId);
  return (st.status?.ahead ?? 0) > 0 || st.unpushed.length > 0;
};

beforeEach(() => {
  m.status.mockReset(); m.unpushed.mockReset(); m.branches.mockReset(); m.stashList.mockReset(); m.changedBy.mockReset();
  m.joinUser.mockReset();
});

describe('lib/git — сброс кеша при смене пользователя (дефект bfaa0df1)', () => {
  it('после выхода состояние пустое: ни status, ни unpushed, ни веток, ни стэшей; canPublish ложен', async () => {
    const projectId = freshProjectId();
    await openProjectAsAdult(projectId);

    const before = getGitState(projectId);
    expect(before.status?.isRepo).toBe(true);
    expect(before.statusLoaded).toBe(true);
    expect(before.unpushed).toHaveLength(1);
    expect(before.branches).toHaveLength(1);
    expect(before.stashes).toHaveLength(1);
    expect(canPublishOf(projectId)).toBe(true); // та самая активная кнопка у взрослого

    runLogoutResets(); // выход из аккаунта — любой из двух путей App

    const after = getGitState(projectId);
    expect(after.status).toBeNull();
    expect(after.statusLoaded).toBe(false); // «неизвестно», а не «чисто» от кеша взрослого
    expect(after.unpushed).toHaveLength(0);
    expect(after.branches).toHaveLength(0); // ветки взрослого не видны ребёнку
    expect(after.stashes).toHaveLength(0);
    expect(after.log).toHaveLength(0);
    expect(after.error).toBeNull();
    expect(after.diverged).toBe(false);
    expect(after.conflictFiles).toHaveLength(0);
    expect(after.busy).toBe(false);
    expect(after.changedBy.size).toBe(0);
    expect(after.myChangedPaths).toBeUndefined();
    expect(after.dirtySessionIds).toBeUndefined();
    expect(canPublishOf(projectId)).toBe(false);
  });

  it('resetGitState чистит кеш и напрямую (та же точка, что регистрация на выход)', async () => {
    const projectId = freshProjectId();
    await openProjectAsAdult(projectId);

    resetGitState();

    expect(getGitState(projectId).status).toBeNull();
    expect(getGitState(projectId).branches).toHaveLength(0);
  });

  it('ребёнок в том же проекте: после сброса статуса нет до нового запроса, затем идёт перечитывание', async () => {
    const projectId = freshProjectId();
    await openProjectAsAdult(projectId);
    runLogoutResets();

    // До нового открытия — никакого кеша: ни статуса, ни признака «загружено»
    expect(getGitState(projectId).status).toBeNull();
    expect(getGitState(projectId).statusLoaded).toBe(false);

    // Ребёнок открывает тот же проект — запрос идёт заново и берёт СВОЙ статус
    const callsBefore = m.status.mock.calls.length;
    m.status.mockResolvedValue(childStatus());
    ensureGit(projectId);
    await settle();

    expect(m.status.mock.calls.length).toBeGreaterThan(callsBefore);
    const st = getGitState(projectId);
    expect(st.status?.ahead).toBe(0);
    expect(st.status?.staged).toHaveLength(0);
    expect(canPublishOf(projectId)).toBe(false);
  });

  it('висевший дебаунс file_changed после выхода не возвращает в стор проект прежнего владельца', async () => {
    vi.useFakeTimers();
    try {
      const projectId = freshProjectId();
      await openProjectAsAdult(projectId, () => vi.advanceTimersByTimeAsync(0));
      const onFiles = m.onFilesChanged.mock.calls[0]?.[0] as (p: { projectId: string }) => void;
      expect(onFiles).toBeTypeOf('function'); // обвязка realtime зарегистрирована при первом ensureGit
      onFiles({ projectId }); // дебаунс 1500 мс висит

      runLogoutResets();
      await vi.advanceTimersByTimeAsync(2000); // дебаунс дособрался бы, если б его не сняли

      const st = getGitState(projectId);
      expect(st.status).toBeNull();
      expect(st.statusLoaded).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  // Ревью Х-W4-69 (MAJOR): полёт запроса не должен возвращать кеш. Принудительный выход
  // (offline.ts dispatchEvent('cc-unauthorized') ДО throw) поднимает runLogoutResets раньше
  // catch того же запроса: без стража поколения patch() в loadGitStatus создал бы запись
  // заново — фантомную (statusLoaded:true при status=null) или со статусом прежнего
  // владельца, и ensureGit следующего владельца (без refresh) ничего бы не перечитал.
  it('ответ loadGitStatus, улетевший до выхода, не возвращает кеш; следующий ensureGit перечитывает', async () => {
    const projectId = freshProjectId();
    let resolveStatus: (s: GitStatus) => void = () => {};
    m.status.mockImplementation(() => new Promise<GitStatus>(r => { resolveStatus = r; }));

    const inFlight = loadGitStatus(projectId); // запрос в полёте — ответа ещё нет
    runLogoutResets();                         // выход стреляет раньше ответа того же запроса
    resolveStatus(adultStatus());              // и только потом доезжает ответ прежнего владельца
    await inFlight;

    const st = getGitState(projectId);
    expect(st.status).toBeNull();
    expect(st.statusLoaded).toBe(false); // фантомной «загружено» нет: ответ прежнего владельца отброшен

    // Следующий владелец открывает тот же проект — ensureGit без refresh обязан сделать НОВЫЙ запрос
    m.status.mockResolvedValue(childStatus());
    const callsBefore = m.status.mock.calls.length;
    ensureGit(projectId);
    await settle();

    expect(m.status.mock.calls.length).toBeGreaterThan(callsBefore);
    const st2 = getGitState(projectId);
    expect(st2.status?.ahead).toBe(0);
    expect(st2.statusLoaded).toBe(true);
    expect(canPublishOf(projectId)).toBe(false);
  });
});
