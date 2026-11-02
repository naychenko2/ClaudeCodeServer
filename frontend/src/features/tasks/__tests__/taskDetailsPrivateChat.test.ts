// S2d-F1 (маска чужих приватных чатов, S2d-4b/К5): карточка задачи при закрытом чате
// (linkedSessionId=null, рядом форма linkedChat) показывает нейтральную подпись
// «Приватный чат участника» — без кнопки перехода, без шеврона и без id; свой чат —
// как прежде (кнопка «Открыть диалог»); ни чата, ни формы — секции нет вовсе.
// Рендерим настоящую TaskDetailsPane поверх linkedom (harness как в taskDetailsReadOnly).
import { describe, it, expect, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { Task } from '../../../types';

const m = vi.hoisted(() => ({ project: undefined as unknown, view: null as unknown, width: 500 }));
const apiSpy = vi.hoisted(() => ({
  tasks: {
    execute: vi.fn(async () => ({})),
    aiClassify: vi.fn(async () => ({ labels: [] as string[], priority: 'medium' })),
    aiFindDuplicate: vi.fn(async () => ({ duplicateId: null })),
    update: vi.fn(async () => ({})),
    delete: vi.fn(async () => ({})),
  },
  // Свой/общий чат: карточка личной задачи грузит его напрямую по id
  chats: { get: vi.fn(async (id: string) => ({ id, name: 'Общий чат' })) },
}));

vi.mock('../../projects/useAllProjects', () => ({ getCachedProject: () => m.project }));
vi.mock('../../projects/members/MessageAuthorLine', () => ({ loadView: () => Promise.resolve(m.view) }));
vi.mock('../../../lib/signalr', () => ({
  onProjectMembersChanged: () => () => {}, onMessage: () => () => {}, onReconnected: () => () => {},
  joinUser: async () => {},
}));
vi.mock('../../../lib/api', () => ({ api: apiSpy }));
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
Object.defineProperty(win.HTMLElement.prototype, 'clientWidth', { get: () => m.width, configurable: true });
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const { TaskDetailsPane } = await import('../TaskDetailsPane');

const PRIVATE_LABEL = 'Приватный чат участника';

const task = (over: Partial<Task> = {}): Task => ({
  id: 't1', title: 'Собрать отчёт', status: 'todo', priority: 'medium', assignee: 'claude',
  projectId: 'p1', linkedFiles: [], subtasks: [], labels: [], order: 0,
  createdAt: '2026-10-12T10:00:00Z', updatedAt: '2026-10-12T10:00:00Z', ...over,
} as Task);

// Форма-заглушка ровно той формы, что отдаёт бэк (TaskView.MaskForeignChats):
// id-поле обнулено, рядом ChatRef с title-подписью и authorUserId
const MASKED: Partial<Task> = {
  linkedSessionId: null,
  linkedChat: { sessionId: null, title: PRIVATE_LABEL, private: true, authorUserId: 'u-x' },
};

async function mount(over: Partial<Task> = {}, isMobile?: boolean) {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  await act(async () => { root.render(createElement(TaskDetailsPane, { task: task(over), project: null, isMobile })); });
  await act(async () => { await Promise.resolve(); });
  return host;
}

const hasText = (host: Element, text: string) => (host.textContent ?? '').includes(text);
const buttonsWithText = (host: Element, text: string) =>
  Array.from(host.querySelectorAll('button')).filter(b => (b.textContent ?? '').includes(text));

describe('карточка задачи: маска чужого приватного чата (S2d-F1)', () => {
  it('заглушка вместо ссылки: «Приватный чат участника» без кнопки перехода, без шеврона и без id', async () => {
    m.project = { myRole: 'owner' }; m.width = 500;
    const host = await mount(MASKED);
    expect(hasText(host, 'Связанная сессия')).toBe(true);   // секция на месте
    expect(hasText(host, PRIVATE_LABEL)).toBe(true);        // подпись как её отдаёт бэк
    expect(buttonsWithText(host, PRIVATE_LABEL)).toHaveLength(0);   // не кнопка
    expect(buttonsWithText(host, 'Открыть диалог')).toHaveLength(0);
    expect(host.querySelector('svg.lucide-chevron-right, [class*="chevron"]')).toBeNull();
    expect(hasText(host, 'u-x')).toBe(false);               // authorUserId не показываем
    expect(apiSpy.chats.get).not.toHaveBeenCalled();        // id нет — запроса по id нет
  });

  it('свой/общий чат — как прежде: кнопка «Открыть диалог», заглушки нет', async () => {
    m.project = { myRole: 'owner' }; m.width = 500;
    apiSpy.chats.get.mockClear();
    const host = await mount({ linkedSessionId: 'c-1' });
    const btn = buttonsWithText(host, 'Открыть диалог');
    expect(btn).toHaveLength(1);
    expect(hasText(host, 'Общий чат')).toBe(true);
    expect(hasText(host, PRIVATE_LABEL)).toBe(false);
  });

  it('ни чата, ни заглушки: секции «Связанная сессия» нет (как раньше)', async () => {
    m.project = { myRole: 'owner' }; m.width = 500;
    const host = await mount({});
    expect(hasText(host, 'Связанная сессия')).toBe(false);
    expect(hasText(host, PRIVATE_LABEL)).toBe(false);
  });

  it('мобильная раскладка: заглушка на месте и остаётся неактивной', async () => {
    m.project = { myRole: 'owner' }; m.width = 360;
    const host = await mount(MASKED, true);
    expect(hasText(host, PRIVATE_LABEL)).toBe(true);
    expect(buttonsWithText(host, PRIVATE_LABEL)).toHaveLength(0);
    expect(buttonsWithText(host, 'Открыть диалог')).toHaveLength(0);
  });
});
