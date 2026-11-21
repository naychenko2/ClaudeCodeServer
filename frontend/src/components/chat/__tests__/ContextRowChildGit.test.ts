// Доработка X-W4-37: ребёнок не опрашивает git из строки контекста чата. ChatPanel рисует
// ContextRow безусловно, тот зовёт useGitChip, а тот — ensureGit/loadUnpushedLog: ребёнку
// был 403 на /git/status и /git/unpushed при каждом открытии чата (обход после гашения
// панелей в d8596877f). Гейт — по Me.isChild в useGitChip (решение 77: git у ребёнка
// выключен); у взрослого запросы идут как прежде. Окружение — как FileExplorerChildGit:
// fakeDom mount + мок signalr (ensureGit строит через него соединения) + мок api.git.*
// (их вызов и есть проверяемое) + мок chatContextApi (RowCore на маунте грузит стор
// контекста; результат тест не читает) + localStorage (readStoredUserId). Стор git —
// модульный без сброса: каждый кейс берёт свой projectId. Цепочка ChatPanel → ContextRow
// guard'ится по исходнику (образец ChatPanel.companionGate): порвут звено — тест красный.

import { act, createElement } from 'react';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { ContextRow } from '../ContextRow';
import { api } from '../../../lib/api';
import { chatContextApi } from '../../../lib/chatContext/api';
import { EMPTY_CONTEXT } from '../../../lib/chatContext/types';
import type { Me, Project, Session } from '../../../types';
import { installFakeDom, mount, uninstallFakeDom } from './fakeDom';
import { clearMe, setMeFromServer } from '../../../lib/defaultPersona';

vi.mock('../../../lib/toast', () => ({ showToast: () => {} }));
// signalr: ensureGit (wireRealtime, joinUserGroup) и стор контекста (onReconnected) строят
// через него соединения — в node без сервера это падает. Все подписки — no-op.
vi.mock('../../../lib/signalr', () => ({
  getConnection: () => ({ start: async () => {}, on: () => {}, stop: async () => {} }),
  onMessage: () => () => {},
  onPreviewMessage: () => () => {},
  onFilesChanged: () => () => {},
  onGitStatusChanged: () => () => {},
  onLocalModelMessage: () => () => {},
  onReconnected: () => () => {},
  joinUser: async () => {},
}));

const CHAT_PANEL_SRC = readFileSync(fileURLToPath(new URL('../../ChatPanel.tsx', import.meta.url)), 'utf8');
const CONTEXT_ROW_SRC = readFileSync(fileURLToPath(new URL('../ContextRow.tsx', import.meta.url)), 'utf8');

function projectWith(id: string): Project {
  return {
    id, name: 'Проект', rootPath: '/home/an/proj',
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-02T00:00:00Z',
  };
}

beforeEach(() => {
  vi.useFakeTimers();
  installFakeDom();
  const store: Record<string, string> = {};
  (globalThis as any).localStorage = {
    getItem: (k: string) => (k in store ? store[k] : null),
    setItem: (k: string, v: string) => { store[k] = String(v); },
    removeItem: (k: string) => { delete store[k]; },
    clear: () => { for (const k of Object.keys(store)) delete store[k]; },
    key: () => null, get length() { return Object.keys(store).length; },
  };
  (globalThis as any).sessionStorage = (globalThis as any).localStorage;
  if (!(globalThis as any).window.location) (globalThis as any).window.location = { origin: 'http://localhost:5000' };
  // стор контекста при маунте строки грузит свой GET — результат тесту не нужен
  vi.spyOn(chatContextApi, 'get').mockResolvedValue(EMPTY_CONTEXT);
  // git.status / git.unpushed — ровно те вызовы, которые проверяем; их результаты тест не читает
  vi.spyOn(api.git, 'status').mockRejectedValue(new Error('offline'));
  vi.spyOn(api.git, 'unpushed').mockRejectedValue(new Error('offline'));
  clearMe();
});

afterEach(() => {
  vi.useRealTimers();
  uninstallFakeDom();
  vi.restoreAllMocks();
  clearMe();
});

async function mountRow(isChild: boolean, projectId: string) {
  setMeFromServer({ userId: isChild ? 'u-child' : 'u-adult', isChild } as Me);
  const { root } = mount(createElement(ContextRow, {
    session: { id: 's-' + projectId } as Session,
    project: projectWith(projectId),
    isMobile: false,
    onCommitOwn: () => {}, onCommitAll: () => {},
  }));
  await act(async () => { await Promise.resolve(); }); // промисы маунт-эффектов доехали
  return root;
}

describe('ContextRow: ребёнок не опрашивает git из чата (доработка X-W4-37)', () => {
  it('детский маунт строки: /git/status и /git/unpushed не позваны', async () => {
    const root = await mountRow(true, 'p-child-chat');
    expect(api.git.status, 'ребёнку позван git.status').not.toHaveBeenCalled();
    expect(api.git.unpushed, 'ребёнку позван git.unpushed').not.toHaveBeenCalled();
    await act(async () => { root.unmount(); });
  });

  it('взрослый маунт строки: статус и незапушенные позваны как прежде', async () => {
    const root = await mountRow(false, 'p-adult-chat');
    expect(api.git.status, 'взрослый не спросил git.status').toHaveBeenCalledTimes(1);
    expect(api.git.unpushed, 'взрослый не спросил git.unpushed').toHaveBeenCalledTimes(1);
    await act(async () => { root.unmount(); });
  });

  // Звенья цепочки чата (guard по исходнику, образец companionGate): без них переименование
  // или снятие звена оставило бы тест зелёным при мёртвой связи ChatPanel → useGitChip
  it('цепочка цела: ChatPanel рисует ContextRow, ContextRow зовёт useGitChip', () => {
    expect(CHAT_PANEL_SRC, 'ChatPanel не рисует <ContextRow').toContain('<ContextRow ');
    expect(CONTEXT_ROW_SRC, 'ContextRow не зовёт useGitChip').toContain('useGitChip(');
  });
});
