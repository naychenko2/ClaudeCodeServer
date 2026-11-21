// X-W4-57 (хвост дельта-ревью X-W4-37 ce88a24c): на телефоне боковая панель проекта в DOM
// и у ребёнка (WorkspacePage.tsx:2772, вкладка leftTab='sessions' по умолчанию — :633-639),
// а SessionList звал ensureGit на маунте безусловно — ребёнку 403 на /git/status при каждом
// открытии списка (GitController без [ChildAllowed], решение 77). Гейт — в mount-эффекте
// списка, по существующему Me.isChild (тот же приём, что FileExplorerChildGit.test / useGitChip).
// Окружение — как FileExplorerChildGit.test: fakeDom mount + мок useOnline + мок signalr
// (ensureGit строит через него соединения) + мок api.git.status (его вызов на маунте и есть
// проверяемое) + остальные сетевые ручки, которые список поднимает на маунте (список чатов,
// реестр тем, индекс фич, снимок фоновой работы), + localStorage (ключи кэша и фильтров).
// Стор git — модульный без теста-сброса: каждый кейс берёт свой projectId, чтобы состояние
// предыдущего не глушило следующий вызов.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { SessionList } from '../SessionList';
import { api } from '../../lib/api';
import { __test as sessionsListTest } from '../../lib/sessionsList';
import { __test as featuresIndexTest } from '../../lib/featuresIndex';
import type { Project, Me } from '../../types';
import { installFakeDom, mount, uninstallFakeDom } from '../chat/__tests__/fakeDom';
import { clearMe, setMeFromServer } from '../../lib/defaultPersona';

vi.mock('../../lib/toast', () => ({ showToast: () => {} }));
// В node navigator.onLine не определён → «офлайн»; мокаем онлайн, чтобы список работал как в браузере
vi.mock('../../hooks/useOnline', () => ({ useOnline: () => true }));
// signalr: ensureGit (wireRealtime, joinUserGroup) и подписки списка строят соединения —
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
  // git.status — ровно тот вызов, который проверяем; его результат тест не читает
  vi.spyOn(api.git, 'status').mockRejectedValue(new Error('offline'));
  // git.unpushed — вторая ручка опроса (loadUnpushedLog): список её не зовёт вовсе,
  // проверяем и её: у ребёнка не позвана ни одна
  vi.spyOn(api.git, 'unpushed').mockRejectedValue(new Error('offline'));
  // Прочие сетевые ручки, которые список поднимает на маунте: список чатов (пустой —
  // дальше empty-state, карточки не рисуются), реестр тем, индекс фич, снимок фоновой работы
  vi.spyOn(api.sessions, 'list').mockResolvedValue([]);
  vi.spyOn(api.projects.subjects, 'list').mockResolvedValue([]);
  vi.spyOn(api.tasks, 'featuresIndex').mockResolvedValue({ features: [], chats: {} } as never);
  vi.spyOn(api.chats, 'agentsPresence').mockResolvedValue({ agents: [], commands: [] } as never);
  // Модульные сторы — чистые: опросы и кэш одного кейса не должны глушить следующий
  sessionsListTest.reset();
  featuresIndexTest.reset();
  clearMe();
});

afterEach(() => {
  vi.useRealTimers();
  uninstallFakeDom();
  vi.restoreAllMocks();
  clearMe();
});

async function mountList(isChild: boolean, projectId: string) {
  setMeFromServer({ userId: isChild ? 'u-child' : 'u-adult', isChild } as Me);
  installFakeDom();
  if (!(globalThis as any).window.location) (globalThis as any).window.location = { origin: 'http://localhost:5000' };
  const { root } = mount(createElement(SessionList, {
    project: projectWith(projectId), activeSession: null, onSelect: () => {},
  }));
  await act(async () => { await vi.advanceTimersByTimeAsync(100); });
  return root;
}

describe('SessionList: ребёнок не опрашивает git списка (X-W4-57)', () => {
  it('детский mount: api.git.status и api.git.unpushed не позваны ни разу', async () => {
    const root = await mountList(true, 'p-child-git');
    expect(api.git.status, 'ребёнку позван git.status').not.toHaveBeenCalled();
    expect(api.git.unpushed, 'ребёнку позван git.unpushed').not.toHaveBeenCalled();
    await act(async () => { root.unmount(); });
  });

  it('взрослый mount: api.git.status позван как прежде', async () => {
    const root = await mountList(false, 'p-adult-git');
    expect(api.git.status, 'взрослый не спросил git.status').toHaveBeenCalledTimes(1);
    await act(async () => { root.unmount(); });
  });
});
