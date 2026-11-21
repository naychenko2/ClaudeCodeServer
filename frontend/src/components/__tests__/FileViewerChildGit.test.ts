// X-W4-60 (дефект cccc47ad): ребёнку 403 на git-опросе панели «Файлы» — FileViewer звал
// ensureGit на маунте безусловно, а GitController без [ChildAllowed] (решение 77). Гейт —
// в mount-эффекте панели, по существующему Me.isChild (тот же способ, что FileExplorer/
// useGitChip). Окружение — как FileViewerRole.test + FileExplorerChildGit.test: fakeDom mount
// + мок useOnline + мок signalr (ensureGit строит через него соединения) + мок api.files.*
// (загрузка содержимого) + мок api.git.status (его вызов на маунте и есть проверяемое) +
// localStorage (sync-метки и др. его читают). Стор git — модульный без теста-сброса: каждый
// кейс берёт свой projectId, чтобы состояние предыдущего не глушило следующий вызов.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FileViewer } from '../FileViewer';
import { api } from '../../lib/api';
import type { Project, Me } from '../../types';
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
  if (!(globalThis as any).getComputedStyle) (globalThis as any).getComputedStyle = () => ({ overflowY: 'visible' });
  // Содержимое .txt — как в FileViewerRole.test: панель монтируется в обычный просмотр
  vi.spyOn(api.files, 'getContent').mockResolvedValue({
    content: 'hello world', isBinary: false, isImage: false, mimeType: 'text/plain', fileSize: 11,
  }) as never;
  vi.spyOn(api.files, 'getDiff').mockResolvedValue({ diff: '' });
  vi.spyOn(api.files, 'fileUrl').mockReturnValue('http://localhost:5000/files/x');
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

async function mountViewer(isChild: boolean, projectId: string) {
  setMeFromServer({ userId: isChild ? 'u-child' : 'u-adult', isChild } as Me);
  installFakeDom();
  if (!(globalThis as any).window.location) (globalThis as any).window.location = { origin: 'http://localhost:5000' };
  const { root } = mount(createElement(FileViewer, { project: projectWith(projectId), filePath: 'docs/readme.txt', onClose: () => {} }));
  await act(async () => { await vi.advanceTimersByTimeAsync(100); });
  return root;
}

describe('FileViewer: ребёнок не опрашивает git панели (X-W4-60)', () => {
  it('детский mount: api.git.status не позван ни разу', async () => {
    const root = await mountViewer(true, 'p-child-fv-git');
    expect(api.git.status, 'ребёнку позван git.status').not.toHaveBeenCalled();
    await act(async () => { root.unmount(); });
  });

  it('взрослый mount: api.git.status позван как прежде', async () => {
    const root = await mountViewer(false, 'p-adult-fv-git');
    expect(api.git.status, 'взрослый не спросил git.status').toHaveBeenCalledTimes(1);
    await act(async () => { root.unmount(); });
  });
});
