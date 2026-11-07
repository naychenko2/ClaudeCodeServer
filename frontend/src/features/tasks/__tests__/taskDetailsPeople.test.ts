// S2b-F4: строки «Поставил …» и «была на … · Назначить» внутри панели деталей задачи.
// Рендерим настоящую TaskDetailsPane через react-dom/client поверх linkedom; форму правки
// (в ней живёт ExecutorPicker — единственный способ сменить исполнителя) подменяем маркером.
import { describe, it, expect, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { ProjectMembersView, Task } from '../../../types';

const m = vi.hoisted(() => ({ view: null as unknown, project: undefined as unknown }));

vi.mock('../../projects/members/MessageAuthorLine', () => ({
  loadView: () => Promise.resolve(m.view),
}));
vi.mock('../../projects/useAllProjects', () => ({ getCachedProject: () => m.project }));
vi.mock('../../../lib/signalr', () => ({
  onProjectMembersChanged: () => () => {}, onMessage: () => () => {}, onReconnected: () => () => {},
  joinUser: async () => {},
}));
vi.mock('../../../lib/tasks', async importOriginal => {
  const orig = await importOriginal<typeof import('../../../lib/tasks')>();
  return { ...orig, useTasks: () => [], useEnsureFullTask: () => ({ state: 'ready', retry: () => {} }) };
});
vi.mock('../../chatContext/useContextButton', () => ({
  useContextButton: () => ({ available: false, inContext: false, title: '', toggle: () => {} }),
}));
vi.mock('../TaskEditForm', () => ({
  TaskEditForm: () => createElement('div', { 'data-testid': 'edit-form' }, 'ФОРМА ПРАВКИ'),
}));
vi.mock('../../../lib/sessionsList', () => ({ listProjectSessions: async () => [] }));

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
Object.assign(win, {
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
  requestAnimationFrame: (cb: () => void) => setTimeout(cb, 0),
  cancelAnimationFrame: (id: ReturnType<typeof setTimeout>) => clearTimeout(id),
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const { TaskDetailsPane } = await import('../TaskDetailsPane');

const VIEW: ProjectMembersView = {
  owner: { userId: 'u-owner', username: 'andrey', displayName: 'Андрей' },
  members: [{ userId: 'u-m', role: 'editor', displayName: 'Мария' }],
  myRole: 'owner',
};

const task = (over: Partial<Task> = {}): Task => ({
  id: 't1', title: 'Собрать отчёт', status: 'todo', priority: 'medium', assignee: 'me',
  projectId: 'p1', linkedFiles: [], subtasks: [], labels: [], order: 0,
  createdAt: '2026-10-12T10:00:00Z', updatedAt: '2026-10-12T10:00:00Z', ...over,
} as Task);

async function mount(t: Task) {
  m.view = VIEW; m.project = { formerMembers: [{ userId: 'u-old', displayName: 'Олег' }] };
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  await act(async () => { root.render(createElement(TaskDetailsPane, { task: t, project: null })); });
  await act(async () => { await Promise.resolve(); });
  return host;
}

describe('панель деталей: люди задачи', () => {
  it('createdByUserId → «Поставил <имя>»', async () => {
    const host = await mount(task({ createdByUserId: 'u-m' }));
    expect(host.innerHTML).toContain('Поставил Мария');
  });

  it('бывший участник → «была на Олег», «Назначить» открывает выбор исполнителя (форму правки)', async () => {
    const host = await mount(task({ assigneeUserId: 'u-old' }));
    expect(host.innerHTML).toContain('была на Олег');
    expect(host.innerHTML).not.toContain('ФОРМА ПРАВКИ');
    const btn = Array.from(host.querySelectorAll('button')).find(b => b.textContent === 'Назначить');
    expect(btn).toBeTruthy();
    await act(async () => { btn!.dispatchEvent(new win.Event('click', { bubbles: true })); });
    expect(host.innerHTML).toContain('ФОРМА ПРАВКИ');
  });
});
