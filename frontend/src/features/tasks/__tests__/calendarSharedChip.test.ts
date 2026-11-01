// S2b-F2: в календаре задачи чужого общего проекта (я — участник) помечены чипом «Общие»; свои и личные — без него.
import { describe, it, expect, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { Project, Task } from '../../../types';

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

const { CalendarAgenda } = await import('../CalendarAgenda');
const { todayIso } = await import('../../../lib/tasks');

const task = (over: Partial<Task> = {}): Task => ({
  id: 't1', title: 'Задача', status: 'todo', priority: 'medium', assignee: 'me', projectId: 'p-foreign', dueDate: todayIso(),
  linkedFiles: [], subtasks: [], labels: [], order: 1,
  createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', ...over,
} as Task);

const projects = new Map<string, Project>([
  ['p-foreign', { id: 'p-foreign', name: 'Чужой', myRole: 'editor' } as Project],
  ['p-own', { id: 'p-own', name: 'Мой', myRole: 'owner' } as Project],
]);

async function render(t: Task): Promise<string> {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  await act(async () => { root.render(createElement(CalendarAgenda, { tasks: [t], projectsById: projects, onOpenTask: () => {} })); });
  return host.innerHTML as string;
}

describe('Календарь: чип «Общие»', () => {
  it('задача проекта, где я участник → чип «Общие»', async () => {
    const html = await render(task());
    expect(html).toContain('data-testid="shared-task-chip"');
    expect(html).toContain('Общие');
  });
  it('задача своего проекта → чипа нет', async () => {
    expect(await render(task({ projectId: 'p-own' }))).not.toContain('shared-task-chip');
  });
  it('личная задача (без проекта) → чипа нет', async () => {
    expect(await render(task({ projectId: undefined }))).not.toContain('shared-task-chip');
  });
});
