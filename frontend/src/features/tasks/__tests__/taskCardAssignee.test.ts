// S2b-F4: в карточке задачи человек-исполнитель вытесняет значок Claude/Я; без поля — как раньше.
import { describe, it, expect, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { Task } from '../../../types';

const m = vi.hoisted(() => ({
  view: { owner: { userId: 'u-o', username: 'a', displayName: 'Андрей' }, members: [{ userId: 'u-m', role: 'editor', displayName: 'Мария' }], myRole: 'owner' },
}));
vi.mock('../../projects/members/MessageAuthorLine', () => ({ loadView: () => Promise.resolve(m.view) }));
vi.mock('../../projects/useAllProjects', () => ({ getCachedProject: () => ({ formerMembers: [] }) }));
vi.mock('../../../lib/api', () => ({ api: { tasks: {} } }));
vi.mock('../../../lib/signalr', () => ({
  joinUser: async () => {}, onMessage: () => () => {}, onReconnected: () => () => {}, onProjectMembersChanged: () => () => {},
}));
vi.mock('../../../lib/idb', () => ({
  idbGet: async () => undefined, idbSet: async () => {}, idbClear: async () => {}, idbKeys: async () => [], idbDelete: async () => {},
  outboxAll: async () => [], outboxGet: async () => undefined, outboxPut: async () => {}, outboxDelete: async () => {},
}));
vi.mock('../../chatContext/useContextButton', () => ({
  useContextButton: () => ({ available: false, inContext: false, title: '', toggle: () => {} }),
}));

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
Object.assign(win, { matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }) });
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
vi.stubGlobal('sessionStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const { TaskCard } = await import('../TaskCard');

const task = (over: Partial<Task> = {}): Task => ({
  id: 't1', title: 'Задача', status: 'todo', priority: 'medium', assignee: 'claude', projectId: 'p1',
  linkedFiles: [], subtasks: [], labels: [], order: 1,
  createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', ...over,
} as Task);

async function render(t: Task): Promise<string> {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  await act(async () => { root.render(createElement(TaskCard, { task: t, onClick: () => {} })); });
  await act(async () => { await Promise.resolve(); });
  return host.innerHTML as string;
}

describe('TaskCard: человек-исполнитель', () => {
  it('assigneeUserId участника → чип с именем, значка Claude справа нет', async () => {
    const html = await render(task({ assigneeUserId: 'u-m' }));
    expect(html).toContain('data-testid="task-user-badge"');
    expect(html).toContain('Мария');
    expect(html).not.toContain('title="AI"');
  });
  it('исполнитель не из участников → «Без исполнителя»', async () => {
    const html = await render(task({ assigneeUserId: 'u-x' }));
    expect(html).toContain('Без исполнителя');
  });
  it('без assigneeUserId → прежний значок Claude, чипа нет', async () => {
    const html = await render(task());
    expect(html).toContain('title="AI"');
    expect(html).not.toContain('task-user-badge');
  });
});
