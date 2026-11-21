// X-W4-37: ребёнок не опрашивает git панели. GitController без [ChildAllowed] (решение 77:
// git у проекта-предмета выключен), а FileExplorer звал ensureGit на маунте безусловно —
// ребёнку был 403 при каждом открытии детской панели «Файлы». Гейт — в mount-эффекте
// панели, по существующему Me.isChild. Окружение — как FileExplorerRole.test: fakeDom mount
// + spyOn(api.files,'list') + мок useOnline + мок signalr (ensureGit строит через него
// соединения) + мок api.git.status (его вызов на маунте и есть проверяемое) + localStorage
// (loadSortMode и др. его читают). Стор git — модульный без теста-сброса: каждый кейс
// берёт свой projectId, чтобы состояние предыдущего не глушило следующий вызов.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FileExplorer } from '../FileExplorer';
import { api } from '../../lib/api';
import type { Project, FileEntry, Me } from '../../types';
import { installFakeDom, mount, uninstallFakeDom } from '../chat/__tests__/fakeDom';
import { clearMe, setMeFromServer } from '../../lib/defaultPersona';

vi.mock('../../lib/toast', () => ({ showToast: () => {} }));
// В node navigator.onLine не определён → «офлайн»; мокаем онлайн, чтобы панель работала как в браузере
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

const FILE: FileEntry = { name: 'a.txt', path: 'a.txt', isDirectory: false, modified: '2026-01-01T00:00:00Z', isModified: false };

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
  vi.spyOn(api.files, 'list').mockImplementation(async () => [FILE]);
  // git.status — ровно тот вызов, который проверяем; его результат тест не читает
  vi.spyOn(api.git, 'status').mockRejectedValue(new Error('offline'));
  // стор заметок при маунте панели тоже поднимает свои load-вызовы — no-op
  vi.spyOn(api.notes, 'list').mockResolvedValue([] as never);
  vi.spyOn(api.notes, 'folders').mockResolvedValue([] as never);
  clearMe();
});

afterEach(() => {
  vi.useRealTimers();
  uninstallFakeDom();
  vi.restoreAllMocks();
  clearMe();
});

async function mountExplorer(isChild: boolean, projectId: string) {
  setMeFromServer({ userId: isChild ? 'u-child' : 'u-adult', isChild } as Me);
  installFakeDom();
  if (!(globalThis as any).window.location) (globalThis as any).window.location = { origin: 'http://localhost:5000' };
  const { root } = mount(createElement(FileExplorer, { project: projectWith(projectId), onOpenFile: () => {} }));
  await act(async () => { await vi.advanceTimersByTimeAsync(100); });
  return root;
}

describe('FileExplorer: ребёнок не опрашивает git панели (X-W4-37)', () => {
  it('детский mount: api.git.status не позван ни разу', async () => {
    const root = await mountExplorer(true, 'p-child-git');
    expect(api.git.status, 'ребёнку позван git.status').not.toHaveBeenCalled();
    await act(async () => { root.unmount(); });
  });

  it('взрослый mount: api.git.status позван как прежде', async () => {
    const root = await mountExplorer(false, 'p-adult-git');
    expect(api.git.status, 'взрослый не спросил git.status').toHaveBeenCalledTimes(1);
    await act(async () => { root.unmount(); });
  });
});
