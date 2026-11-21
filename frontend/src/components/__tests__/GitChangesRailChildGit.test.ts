// X-W4-60 (дефект cccc47ad): ребёнку 403 ×4 на git-опросах панели «Изменения» —
// GitChangesRail на маунте звал ensureGit + loadUnpushedLog + loadGitRemote + loadGitStash
// безусловно, а GitController без [ChildAllowed] (решение 77). Гейт — в mount-эффекте
// панели, по существующему Me.isChild (тот же способ, что FileExplorer/useGitChip).
// Окружение — как FileExplorerChildGit.test: fakeDom mount + мок signalr (ensureGit строит
// через него соединения) + мок четырёх api.git.* (их вызовы на маунте и есть проверяемое) +
// localStorage (VIEW_KEY/SCOPE_H_KEY читаются в инициализаторов состояния). Стор git —
// модульный без теста-сброса: каждый кейс берёт свой projectId.
// Доработка по ревью b86380c7: те же два гейта на ленивых опросах капсулы «Ветка» —
// loadGitLog (скоуп ветки) и loadGitBranches (меню веток); их моки добавлены сюда же.
// X-W4-64 (дефект cccc47ad, дельта-ревью b29ff02e): тот же класс у ряда «Обмен с remote» —
// кнопки fetch/pull стояли только под canRemote (права проекта, не детство) → 403.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { GitChangesRail } from '../GitChangesRail';
import { api } from '../../lib/api';
import type { Project, Me } from '../../types';
import { installFakeDom, mount, reactProps, uninstallFakeDom, findNodeByAttr } from '../chat/__tests__/fakeDom';
import { clearMe, setMeFromServer } from '../../lib/defaultPersona';

vi.mock('../../lib/toast', () => ({ showToast: () => {} }));
vi.mock('../../hooks/useOnline', () => ({ useOnline: () => true }));
// signalr: ensureGit (wireRealtime, joinUserGroup) строит соединение и читает localStorage —
// в node без сервера/хранилища это падает. Все подписки — no-op.
vi.mock('../../lib/signalr', () => ({
  getConnection: () => ({ start: async () => {}, on: () => {}, stop: async () => {} }),
  onMessage: () => () => {},
  onPreviewMessage: () => () => {},
  onFilesChanged: () => () => {},
  onGitStatusChanged: () => () => {},
  onLocalModelMessage: () => () => {},
  onReconnected: () => () => {},
  joinUser: async () => {},
}));

function projectWith(id: string): Project {
  return {
    id, name: 'Проект', rootPath: '/home/an/proj',
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-02T00:00:00Z',
  };
}

beforeEach(() => {
  vi.useFakeTimers();
  const store: Record<string, string> = {};
  (globalThis as any).localStorage = {
    getItem: (k: string) => (k in store ? store[k] : null),
    setItem: (k: string, v: string) => { store[k] = String(v); },
    removeItem: (k: string) => { delete store[k]; },
    clear: () => { for (const k of Object.keys(store)) delete store[k]; },
    key: () => null, get length() { return Object.keys(store).length; },
  };
  (globalThis as any).sessionStorage = (globalThis as any).localStorage;
  // Вызовы mount-эффекта и ленивых опросов «Ветки» — ровно то, что проверяем; результаты тест не читает
  vi.spyOn(api.git, 'status').mockRejectedValue(new Error('offline'));
  vi.spyOn(api.git, 'unpushed').mockRejectedValue(new Error('offline'));
  vi.spyOn(api.git, 'remote').mockRejectedValue(new Error('offline'));
  vi.spyOn(api.git, 'stashList').mockRejectedValue(new Error('offline'));
  vi.spyOn(api.git, 'log').mockRejectedValue(new Error('offline'));
  vi.spyOn(api.git, 'branches').mockRejectedValue(new Error('offline'));
  vi.spyOn(api.git, 'fetch').mockRejectedValue(new Error('offline'));
  vi.spyOn(api.git, 'pull').mockRejectedValue(new Error('offline'));
  clearMe();
});

afterEach(() => {
  vi.useRealTimers();
  uninstallFakeDom();
  vi.restoreAllMocks();
  clearMe();
});

async function mountRail(isChild: boolean, projectId: string) {
  setMeFromServer({ userId: isChild ? 'u-child' : 'u-adult', isChild } as Me);
  installFakeDom();
  if (!(globalThis as any).window.location) (globalThis as any).window.location = { origin: 'http://localhost:5000' };
  const { root, container } = mount(createElement(GitChangesRail, {
    project: projectWith(projectId),
    onOpenDiff: () => {}, onOpenFile: () => {}, onOpenCommit: () => {},
  }));
  await act(async () => { await vi.advanceTimersByTimeAsync(100); });
  return { root, container };
}

describe('GitChangesRail: ребёнок не опрашивает git панели (X-W4-60)', () => {
  it('детский mount: ни один из четырёх git-запросов не позван', async () => {
    const { root } = await mountRail(true, 'p-child-rail-git');
    expect(api.git.status, 'ребёнку позван git.status').not.toHaveBeenCalled();
    expect(api.git.unpushed, 'ребёнку позван git.unpushed').not.toHaveBeenCalled();
    expect(api.git.remote, 'ребёнку позван git.remote').not.toHaveBeenCalled();
    expect(api.git.stashList, 'ребёнку позван git.stashList').not.toHaveBeenCalled();
    await act(async () => { root.unmount(); });
  });

  it('взрослый mount: все четыре запроса уходят как прежде', async () => {
    const { root } = await mountRail(false, 'p-adult-rail-git');
    expect(api.git.status, 'взрослый не спросил git.status').toHaveBeenCalledTimes(1);
    expect(api.git.unpushed, 'взрослый не спросил git.unpushed').toHaveBeenCalledTimes(1);
    expect(api.git.remote, 'взрослый не спросил git.remote').toHaveBeenCalledTimes(1);
    expect(api.git.stashList, 'взрослый не спросил git.stashList').toHaveBeenCalledTimes(1);
    await act(async () => { root.unmount(); });
  });
});

// Капсула «Ветка» (доработка по ревью b86380c7): тело = selectScope('branch') → ленивый
// loadGitLog; стрелка = openBranchMenu → loadGitBranches. Ребёнку капсула видна (ранний
// выход «Проект без git» требует statusLoaded, которого у ребёнка нет) — но ни один из
// двух ленивых опросов не позван. Стрелка ищется по title, тело — первый элемент-ребёнок
// той же рамки; оба поиска проверяются expect-ом: если капсулы нет, тест краснеет сам.
describe('GitChangesRail: ребёнок не спрашивает git по капсуле «Ветка» (X-W4-60)', () => {
  it('детский клик по телу и стрелке: ни git.log, ни git.branches не позваны', async () => {
    const { root, container } = await mountRail(true, 'p-child-rail-branch');
    const arrow = findNodeByAttr(container, 'title', 'Выбрать ветку');
    const body = arrow?.parentElement?.children[0] ?? null;
    expect(arrow, 'ребёнку не нашёлся контрол «Выбрать ветку»').not.toBeNull();
    expect(body, 'ребёнку не нашёлось тело капсулы «Ветка»').not.toBeNull();
    act(() => { (reactProps(body as never) as { onClick: () => void }).onClick(); });
    act(() => { (reactProps(arrow as never) as { onClick: (e: unknown) => void }).onClick({ currentTarget: arrow, stopPropagation: () => {} }); });
    await act(async () => { await vi.advanceTimersByTimeAsync(100); });
    expect(api.git.log, 'ребёнку позван git.log').not.toHaveBeenCalled();
    expect(api.git.branches, 'ребёнку позван git.branches').not.toHaveBeenCalled();
    await act(async () => { root.unmount(); });
  });

  it('взрослый клик по телу и стрелке: ленивые опросы уходят как прежде', async () => {
    // Капсула взрослому нужна живая: при rejected status панель уходит в «Проект без git»
    vi.mocked(api.git.status).mockResolvedValue({
      isRepo: true, branch: 'main', upstream: null, ahead: 0, behind: 0, detached: false,
      staged: [], unstaged: [], untracked: [], isWorktree: false,
    });
    const { root, container } = await mountRail(false, 'p-adult-rail-branch');
    const arrow = findNodeByAttr(container, 'title', 'Выбрать ветку');
    const body = arrow?.parentElement?.children[0] ?? null;
    expect(arrow, 'взрослому не нашёлся контрол «Выбрать ветку»').not.toBeNull();
    expect(body, 'взрослому не нашёлось тело капсулы «Ветка»').not.toBeNull();
    act(() => { (reactProps(body as never) as { onClick: () => void }).onClick(); });
    act(() => { (reactProps(arrow as never) as { onClick: (e: unknown) => void }).onClick({ currentTarget: arrow, stopPropagation: () => {} }); });
    await act(async () => { await vi.advanceTimersByTimeAsync(100); });
    expect(api.git.log, 'взрослый не спросил git.log').toHaveBeenCalledTimes(1);
    expect(api.git.branches, 'взрослый не спросил git.branches').toHaveBeenCalledTimes(1);
    await act(async () => { root.unmount(); });
  });
});

// Ряд «Обмен с remote» (X-W4-64): рисуется в блоке mode==='list' под canRemote — прав
// проекта, не детства; до него ребёнок доходит (ранний выход «Проект без git» требует
// statusLoaded). Гейт — в обработчиках кнопок, как у openBranchMenu: контролы остаются
// на месте ряда, нажатие ничего не зовёт. Кнопки ищутся по title; оба поиска — под
// expect-ом: если ряда нет, тест краснеет сам, а не «проходит» на пустоте.
describe('GitChangesRail: ребёнок не дёргает «Обмен с remote» (X-W4-64)', () => {
  it('детский клик по fetch и pull: ни git.fetch, ни git.pull не позваны', async () => {
    const { root, container } = await mountRail(true, 'p-child-rail-remote');
    const fetchBtn = findNodeByAttr(container, 'title', 'Проверить обновления (fetch)');
    const pullBtn = findNodeByAttr(container, 'title', 'Забрать и слить (pull)');
    expect(fetchBtn, 'ребёнку не нашёлся контрол «Проверить обновления (fetch)»').not.toBeNull();
    expect(pullBtn, 'ребёнку не нашёлся контрол «Забрать и слить (pull)»').not.toBeNull();
    act(() => { (reactProps(fetchBtn as never) as { onClick: () => void }).onClick(); });
    act(() => { (reactProps(pullBtn as never) as { onClick: () => void }).onClick(); });
    await act(async () => { await vi.advanceTimersByTimeAsync(100); });
    expect(api.git.fetch, 'ребёнку позван git.fetch').not.toHaveBeenCalled();
    expect(api.git.pull, 'ребёнку позван git.pull').not.toHaveBeenCalled();
    await act(async () => { root.unmount(); });
  });

  it('взрослый клик по fetch и pull: оба запроса уходят как прежде', async () => {
    // Ряд взрослому нужен живой: при rejected status панель уходит в «Проект без git»
    vi.mocked(api.git.status).mockResolvedValue({
      isRepo: true, branch: 'main', upstream: null, ahead: 0, behind: 0, detached: false,
      staged: [], unstaged: [], untracked: [], isWorktree: false,
    });
    const { root, container } = await mountRail(false, 'p-adult-rail-remote');
    const fetchBtn = findNodeByAttr(container, 'title', 'Проверить обновления (fetch)');
    const pullBtn = findNodeByAttr(container, 'title', 'Забрать и слить (pull)');
    expect(fetchBtn, 'взрослому не нашёлся контрол «Проверить обновления (fetch)»').not.toBeNull();
    expect(pullBtn, 'взрослому не нашёлся контрол «Забрать и слить (pull)»').not.toBeNull();
    act(() => { (reactProps(fetchBtn as never) as { onClick: () => void }).onClick(); });
    act(() => { (reactProps(pullBtn as never) as { onClick: () => void }).onClick(); });
    await act(async () => { await vi.advanceTimersByTimeAsync(100); });
    expect(api.git.fetch, 'взрослый не спросил git.fetch').toHaveBeenCalledTimes(1);
    expect(api.git.pull, 'взрослый не спросил git.pull').toHaveBeenCalledTimes(1);
    await act(async () => { root.unmount(); });
  });
});
