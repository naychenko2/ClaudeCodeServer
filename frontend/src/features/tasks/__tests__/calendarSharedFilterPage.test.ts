// S2b-F8: настоящий CalendarPage — видимость, порядок и взаимоисключение чипа «Общие».
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { Project, Task } from '../../../types';

const m = vi.hoisted(() => ({
  tasks: [] as unknown[],
  projects: [] as unknown[],
  groups: [] as unknown[],
}));
vi.mock('../../../lib/api', () => ({
  api: {
    projects: { list: () => Promise.resolve(m.projects) },
    projectGroups: { list: () => Promise.resolve(m.groups) },
    tasks: { listAll: () => Promise.resolve(m.tasks) },
  },
}));
vi.mock('../../../lib/tasks', async (orig) => ({
  ...(await orig<typeof import('../../../lib/tasks')>()),
  useTasks: () => m.tasks,
  ensureTasksLoaded: () => Promise.resolve(),
}));
vi.mock('../../../components/HubHeader', () => ({ HubHeader: () => null }));
vi.mock('../TaskDetailsModal', () => ({ TaskDetailsModal: () => null }));
vi.mock('../NewTaskDialog', () => ({ NewTaskDialog: () => null }));
vi.mock('../CalendarMonth', () => ({ CalendarMonth: () => null }));
vi.mock('../CalendarWeek', () => ({ CalendarWeek: () => null }));
vi.mock('../board/TaskBoard', () => ({ TaskBoard: () => null }));
// Агенда-заглушка выводит id пришедших задач — по ним видно, что отфильтровал календарь
vi.mock('../CalendarAgenda', async () => {
  const { createElement: h } = await import('react');
  return { CalendarAgenda: ({ tasks }: { tasks: Task[] }) => h('div', { 'data-testid': 'agenda' }, tasks.map(t => t.id).join(',')) };
});

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
Object.assign(win, {
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
  location: { hash: '#/calendar' },
  history: { pushState: () => {}, replaceState: () => {}, state: null },
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.stubGlobal('localStorage', { getItem: (k: string) => (k === 'cc_cal_view' ? 'agenda' : null), setItem: () => {}, removeItem: () => {} });
vi.stubGlobal('sessionStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const { CalendarPage } = await import('../CalendarPage');

const task = (id: string, projectId?: string) => ({ id, title: id, projectId } as Task);
const foreign = { id: 'p-foreign', name: 'Чужой', myRole: 'editor', groupId: 'g1' } as Project;
const own = { id: 'p-own', name: 'Мой', myRole: 'owner', groupId: 'g1' } as Project;
const group = { id: 'g1', name: 'Группа', color: 'gray' };

let host: HTMLElement;
async function mount(): Promise<void> {
  host = doc.createElement('div') as unknown as HTMLElement;
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host);
  await act(async () => {
    root.render(createElement(CalendarPage, { auth: {} as never, onLogout: () => {}, onHubTab: () => {}, onOpenTask: () => {} }));
  });
}
const chips = () => Array.from(host.querySelectorAll('button')).filter(b => ['Все', 'Группа', 'Без группы', 'Общие'].includes((b.textContent ?? '').trim()));
const chip = (label: string) => chips().find(b => (b.textContent ?? '').trim() === label);
const agenda = () => host.querySelector('[data-testid="agenda"]')!.textContent;
const isActive = (label: string) => /font-weight:\s*700/.test(chip(label)!.getAttribute('style') ?? '');
async function click(label: string) { await act(async () => { chip(label)!.click(); }); }

beforeEach(() => { doc.getElementById('root')!.innerHTML = ''; });

describe('CalendarPage: чип «Общие»', () => {
  it('виден при своих задачах в чужих общих проектах и стоит ПОСЛЕ чипов групп', async () => {
    m.tasks = [task('a', 'p-foreign'), task('b', 'p-own')]; m.projects = [foreign, own]; m.groups = [group];
    await mount();
    const labels = chips().map(b => (b.textContent ?? '').trim());
    expect(labels).toContain('Общие');
    expect(labels.indexOf('Общие')).toBeGreaterThan(labels.indexOf('Группа'));
    expect(labels[labels.length - 1]).toBe('Общие');
  });

  it('не виден, когда чужих общих проектов нет', async () => {
    m.tasks = [task('b', 'p-own')]; m.projects = [own]; m.groups = [group];
    await mount();
    expect(chip('Общие')).toBeUndefined();
  });

  it('не виден, когда общий проект есть, а своих задач в нём нет', async () => {
    m.tasks = [task('b', 'p-own')]; m.projects = [foreign, own]; m.groups = [group];
    await mount();
    expect(chip('Общие')).toBeUndefined();
  });

  it('клик по «Общие» показывает только общие задачи', async () => {
    m.tasks = [task('a', 'p-foreign'), task('b', 'p-own'), task('c')]; m.projects = [foreign, own]; m.groups = [group];
    await mount();
    expect(agenda()).toBe('a,b,c');
    await click('Общие');
    expect(agenda()).toBe('a');
  });

  it('«Общие» и группа взаимоисключают друг друга', async () => {
    m.tasks = [task('a', 'p-foreign'), task('b', 'p-own'), task('c')]; m.projects = [foreign, own]; m.groups = [group];
    await mount();
    await click('Группа');
    expect(isActive('Группа')).toBe(true);
    await click('Общие');
    expect(isActive('Общие')).toBe(true);
    expect(isActive('Группа')).toBe(false);
    await click('Группа');
    expect(isActive('Группа')).toBe(true);
    expect(isActive('Общие')).toBe(false);
    expect(agenda()).toBe('a,b');
  });
});
