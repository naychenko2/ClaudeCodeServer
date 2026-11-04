// S2b-F1: группа «Участники проекта» в пикере исполнителя и отправка assigneeUserId
// из формы правки. Монтируем НАСТОЯЩИЕ ExecutorPicker и TaskEditForm поверх linkedom;
// подменены только слои загрузки (список участников, проекты, персоны) — сами
// компоненты и их решения (фильтрация списка, состав DTO) не трогает.
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import { readFileSync } from 'node:fs';
import type { Persona, Project, ProjectMembersView, Task, UpdateTaskDto } from '../../../types';

const m = vi.hoisted(() => ({
  view: null as unknown,
  projects: [] as unknown[],
  personas: [] as unknown[],
  meUserId: 'u-owner',
}));

vi.mock('../../projects/members/MessageAuthorLine', () => ({
  loadView: () => Promise.resolve(m.view),
}));
vi.mock('../../projects/useAllProjects', () => ({
  getCachedProject: () => undefined,
  useAllProjects: () => m.projects,
}));
vi.mock('../../../lib/signalr', () => ({
  onProjectMembersChanged: () => () => {}, onMessage: () => () => {}, onReconnected: () => () => {},
  joinUser: async () => {},
}));
vi.mock('../../../lib/tasks', async importOriginal => {
  const orig = await importOriginal<typeof import('../../../lib/tasks')>();
  return { ...orig, useTasks: () => [], useEnsureFullTask: () => ({ state: 'ready', retry: () => {} }) };
});
vi.mock('../../../lib/defaultPersona', () => ({
  useMe: () => ({
    loaded: true, userId: m.meUserId, username: 'andrey', defaultPersonaId: null,
    role: '', needsOnboarding: false, onboardingSessionId: null, executionEnvironment: null,
  }),
}));
vi.mock('../../../lib/api', () => ({
  api: {
    personas: { list: () => Promise.resolve(m.personas) },
    projects: { list: () => Promise.resolve(m.projects) },
    specialties: { list: () => Promise.resolve([]) },
    models: { preview: () => Promise.resolve(null as never) },
  },
}));
vi.mock('../../../lib/aiJobStore', () => ({
  useAiJob: () => ({ status: 'idle', result: null, error: null }),
  runAiJob: () => {}, resetAiJob: () => {},
}));
vi.mock('../../../lib/subsystems/registry', () => ({
  useSlotItem: () => undefined,
}));

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

const { ExecutorPicker } = await import('../ExecutorPicker');
const { TaskEditForm } = await import('../TaskEditForm');

// Общий проект с владельцем Андреем (он же «текущий человек» по умолчанию), редактор Мария, читатель Чтец
const VIEW: ProjectMembersView = {
  owner: { userId: 'u-owner', username: 'andrey', displayName: 'Андрей' },
  members: [
    { userId: 'u-m', role: 'editor', displayName: 'Мария' },
    { userId: 'u-r', role: 'reader', displayName: 'Чтец' },
  ],
  myRole: 'owner',
};
const SHARED_PROJECT = { id: 'p1', name: 'Общий', ownerId: 'u-owner', myRole: 'owner' } as Project;
const GLOBAL_PERSONA = { id: 'g-alisa', name: 'Алиса', scope: 'global' } as Persona;
const PROJECT_PERSONA = { id: 'p-kira', name: 'Кира', scope: 'project', projectId: 'p1' } as Persona;

beforeEach(() => {
  m.view = VIEW;
  m.projects = [SHARED_PROJECT];
  m.personas = [GLOBAL_PERSONA, PROJECT_PERSONA];
  m.meUserId = 'u-owner';
});

const task = (over: Partial<Task> = {}): Task => ({
  id: 't1', title: 'Собрать отчёт', status: 'todo', priority: 'medium', assignee: 'me',
  projectId: 'p1', linkedFiles: [], subtasks: [], labels: [], order: 0,
  createdAt: '2026-10-12T10:00:00Z', updatedAt: '2026-10-12T10:00:00Z', ...over,
} as Task);

async function mount(host: HTMLElement, el: React.ReactElement) {
  const root = createRoot(host as unknown as HTMLElement);
  await act(async () => { root.render(el); });
  // прогрузка списков (projects/personas/members) — две микротask-фазы, как в taskDetailsPeople
  await act(async () => { await Promise.resolve(); });
  await act(async () => { await Promise.resolve(); });
}

function newHost(): HTMLElement {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  return host;
}

async function click(el: Element | null): Promise<void> {
  expect(el).toBeTruthy();
  await act(async () => { el!.dispatchEvent(new win.Event('click', { bubbles: true })); });
}

const byTestId = (host: HTMLElement, id: string) => host.querySelector(`[data-testid="${id}"]`);
const byText = (host: HTMLElement, text: string) =>
  Array.from(host.querySelectorAll('button')).find(b => (b.textContent ?? '').includes(text));

// === Пикер: группа и выбор ===

describe('ExecutorPicker: группа «Участники проекта»', () => {
  it('общий проект: «Участники проекта» между «Основными» и персонами, «Глобальных» нет', async () => {
    const host = newHost();
    await mount(host, createElement(ExecutorPicker, {
      assignee: 'me', personaId: null, projectId: 'p1',
      members: [{ userId: 'u-m', displayName: 'Мария' }], assigneeUserId: '',
      onChange: () => {},
    }));
    await click(host.querySelector('button'));
    const html = host.innerHTML;
    expect(html).toContain('Основные');
    expect(html).toContain('Участники проекта');
    expect(html).toContain('Мария');
    expect(html).toContain('Команда проекта');
    expect(html).not.toContain('Глобальные');
    expect(html.indexOf('Основные')).toBeLessThan(html.indexOf('Участники проекта'));
    expect(html.indexOf('Участники проекта')).toBeLessThan(html.indexOf('Команда проекта'));
  });

  it('выбор: участник → me+assigneeUserId; «Я»/AI/персона → assigneeUserId=""', async () => {
    const picks: { assignee: string; personaId: string | null; assigneeUserId?: string }[] = [];
    const host = newHost();
    await mount(host, createElement(ExecutorPicker, {
      assignee: 'me', personaId: null, projectId: 'p1',
      members: [{ userId: 'u-m', displayName: 'Мария' }], assigneeUserId: '',
      onChange: (v: { assignee: string; personaId: string | null; assigneeUserId?: string }) => { picks.push(v); },
    }));
    await click(host.querySelector('button'));
    await click(byTestId(host, 'member-u-m'));
    expect(picks.at(-1)).toEqual({ assignee: 'me', personaId: null, assigneeUserId: 'u-m' });

    await click(host.querySelector('button'));
    await click(byTestId(host, 'opt-me'));
    expect(picks.at(-1)).toEqual({ assignee: 'me', personaId: null, assigneeUserId: '' });

    await click(host.querySelector('button'));
    await click(byTestId(host, 'opt-ai'));
    expect(picks.at(-1)).toEqual({ assignee: 'claude', personaId: null, assigneeUserId: '' });

    await click(host.querySelector('button'));
    await click(byText(host, 'Кира'));
    expect(picks.at(-1)).toEqual({ assignee: 'claude', personaId: 'p-kira', assigneeUserId: '' });
  });

  it('нет участников — группы нет; «Глобальных» в общем проекте всё равно нет', async () => {
    const host = newHost();
    await mount(host, createElement(ExecutorPicker, {
      assignee: 'me', personaId: null, projectId: 'p1', members: [], assigneeUserId: '',
      onChange: () => {},
    }));
    await click(host.querySelector('button'));
    const html = host.innerHTML;
    expect(html).not.toContain('Участники проекта');
    expect(html).not.toContain('Глобальные');
  });

  it('личный проект (members не передан): группы нет, снимок прежний — с «Глобальными»', async () => {
    const host = newHost();
    await mount(host, createElement(ExecutorPicker, {
      assignee: 'me', personaId: null, projectId: null, onChange: () => {},
    }));
    await click(host.querySelector('button'));
    const html = host.innerHTML;
    expect(html).not.toContain('Участники проекта');
    expect(html).toContain('Глобальные');
    expect(html).toContain('Алиса');
  });

  it('NewTaskDialog не передаёт участников: при создании поля нет', () => {
    const src = readFileSync(new URL('../NewTaskDialog.tsx', import.meta.url), 'utf8');
    const call = src.match(/<ExecutorPicker[\s\S]*?\/>/)?.[0] ?? '';
    expect(call).not.toContain('members=');
    expect(call).not.toContain('assigneeUserId=');
  });
});

// === Форма правки: состав DTO ===

async function mountForm(t: Task) {
  const host = newHost();
  let saved: UpdateTaskDto | undefined;
  await mount(host, createElement(TaskEditForm, {
    task: t,
    onSave: async (dto: UpdateTaskDto) => { saved = dto; },
    onCancel: () => {},
    onDelete: () => {},
  }));
  return { host, saved: () => saved };
}

// Триггер пикера: подпись зависит от выбранного исполнителя («на вас»/«на нём»)
const openPicker = (host: HTMLElement) => click(byText(host, 'Задача на '));
const saveForm = (host: HTMLElement) => click(byText(host, 'Готово'));

describe('TaskEditForm: assigneeUserId в DTO', () => {
  it('общий проект: выбор участника шлёт assignee:"me" + assigneeUserId:<id>', async () => {
    const { host, saved } = await mountForm(task());
    await openPicker(host);
    expect(host.innerHTML).toContain('Участники проекта');
    await click(byTestId(host, 'member-u-m'));
    await saveForm(host);
    expect(saved()?.assignee).toBe('me');
    expect(saved()?.assigneeUserId).toBe('u-m');
  });

  it('общий проект: выбор «Я» шлёт assigneeUserId=""', async () => {
    const { host, saved } = await mountForm(task({ assigneeUserId: 'u-m' }));
    await openPicker(host);
    await click(byTestId(host, 'opt-me'));
    await saveForm(host);
    expect(saved()?.assignee).toBe('me');
    expect(saved()?.assigneeUserId).toBe('');
  });

  it('общий проект: выбор AI шлёт assigneeUserId="" (снимает человека)', async () => {
    const { host, saved } = await mountForm(task({ assigneeUserId: 'u-m' }));
    await openPicker(host);
    await click(byTestId(host, 'opt-ai'));
    await saveForm(host);
    expect(saved()?.assignee).toBe('claude');
    expect(saved()?.assigneeUserId).toBe('');
  });

  it('владелец, который не текущий человек, — участник; выбор шлёт его id', async () => {
    m.meUserId = 'u-editor';
    const { host, saved } = await mountForm(task());
    await openPicker(host);
    expect(host.innerHTML).toContain('Участники проекта');
    expect(host.innerHTML).toContain('Андрей');
    expect(byTestId(host, 'member-u-owner')).toBeTruthy();
    expect(byTestId(host, 'member-u-r')).toBeNull(); // читатель не назначается
    await click(byTestId(host, 'member-u-owner'));
    await saveForm(host);
    expect(saved()?.assigneeUserId).toBe('u-owner');
  });

  it('личный проект: группы нет, в DTO поля assigneeUserId нет (прежнее поведение)', async () => {
    const { host, saved } = await mountForm(task({ projectId: undefined }));
    await openPicker(host);
    expect(host.innerHTML).not.toContain('Участники проекта');
    await saveForm(host);
    expect(saved()).toBeTruthy();
    expect('assigneeUserId' in (saved() as object)).toBe(false);
  });
});
