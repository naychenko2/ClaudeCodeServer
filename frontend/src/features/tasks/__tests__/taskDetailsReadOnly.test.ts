// F2-Q3 (разрез CONSULT-TASK-RIGHTS): карточка задачи только для чтения у ЧИТАТЕЛЯ общего
// проекта (не у «участника вообще» — это было S2c-F4j под ещё не открытую серверную дверь):
// «Изменить», корзина, «Выполнить с AI» — 0 узлов в DOM, сегмент статуса выключен с
// подсказкой «Менять задачи может редактор», startInEdit и AI-действия панели игнорируются;
// строки подзадач и «Назначить» читателю не кликаются (ревью 47fd9dd9). РЕДАКТОР и владелец —
// все действия на месте: редактор задачами управляет. Рендерим настоящую
// TaskDetailsPane поверх linkedom; ширину панели задаём явно (getter clientWidth):
// 500px < порога 720 — узкая раскладка (случай модалки: сегмент статуса под шапкой),
// 900px — широкая (рельс «Статус» справа). Форму правки подменяем маркером.
// Доработка F2-Q3 (ревью 11716b9b): корзина — право автора или владельца задачи (зеркало
// бэкового TaskDeleteDenied): у редактора корзина только на своей задаче (свой автор).
import { afterEach, describe, it, expect, vi } from 'vitest';
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
  chats: { get: vi.fn(async () => ({})) },
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
// Панель мерит ШИРИНУ СВОЕГО контейнера (useNarrowContainer, порог 720): ширина из m.width
Object.defineProperty(win.HTMLElement.prototype, 'clientWidth', { get: () => m.width, configurable: true });
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
// «Я — автор задачи» читается через readStoredUserId: пара токен+id из одного хранилища
// (sessionStorage первый) — тот же стаб, что в PrivateChatToggle.test
const ME = 'u-me';
const sstore = new Map<string, string>([['cc_token', 't'], ['cc_user_id', ME]]);
vi.stubGlobal('sessionStorage', {
  getItem: (k: string) => sstore.get(k) ?? null,
  setItem: (k: string, v: string) => { sstore.set(k, String(v)); },
  removeItem: (k: string) => { sstore.delete(k); },
});
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const { TaskDetailsPane } = await import('../TaskDetailsPane');
const { TASK_WRITE_DENIED } = await import('../../projects/members/participantGate');

const DENIED_TITLE = TASK_WRITE_DENIED;

const task = (over: Partial<Task> = {}): Task => ({
  id: 't1', title: 'Собрать отчёт', status: 'todo', priority: 'medium', assignee: 'claude',
  projectId: 'p1', linkedFiles: [], subtasks: [], labels: [], order: 0,
  createdAt: '2026-10-12T10:00:00Z', updatedAt: '2026-10-12T10:00:00Z', ...over,
} as Task);

// Панели следующего теста монтируются к тем же корням: снимание после каждого теста
// обязательно, иначе слушатель 'cc-ai-run' предыдущей панели отвечает вместе с новой
// (исполнитель вызывается дважды)
const mounted: ReturnType<typeof createRoot>[] = [];
afterEach(async () => {
  while (mounted.length) { const r = mounted.pop()!; await act(async () => { r.unmount(); }); }
});

async function mount(over: Partial<Task> = {}, startInEdit?: boolean) {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  mounted.push(root);
  await act(async () => { root.render(createElement(TaskDetailsPane, { task: task(over), project: null, startInEdit })); });
  await act(async () => { await Promise.resolve(); });
  return host;
}

const buttonsByText = (host: Element, text: string) =>
  Array.from(host.querySelectorAll('button')).filter(b => (b.textContent ?? '').trim() === text);

// Кнопки управления статусом: сегмент под шапкой (узкая панель) или рельс справа
// (широкая) — три кнопки с подписями статусов. Чип статуса в шапке — span, не кнопка.
const STATUS_TEXTS = ['К выполнению', 'В работе', 'Готово'];
const statusButtons = (host: Element) =>
  Array.from(host.querySelectorAll('button')).filter(b => STATUS_TEXTS.includes((b.textContent ?? '').trim()));

const dispatchAi = async (action: string) => {
  await act(async () => {
    win.dispatchEvent(new win.CustomEvent('cc-ai-run', { detail: { action } }));
    await Promise.resolve();
  });
};

const clickEl = async (el: Element) => {
  await act(async () => { el.dispatchEvent(new win.Event('click', { bubbles: true })); });
};

// Строка подзадачи — div-обёртка вокруг span с её названием
const SUBTASK_TITLE = 'Подшаг один';
const subtaskRow = (host: Element) => {
  const span = Array.from(host.querySelectorAll('span')).find(s => (s.textContent ?? '').trim() === SUBTASK_TITLE);
  return span?.parentElement ?? null;
};

// Список участников: бывший исполнитель u-old — только в formerMembers проекта
const VIEW = {
  owner: { userId: 'u-owner', username: 'andrey', displayName: 'Андрей' },
  members: [{ userId: 'u-m', role: 'editor', displayName: 'Мария' }],
  myRole: 'owner',
};
const FORMER = [{ userId: 'u-old', displayName: 'Олег' }];

describe('панель деталей: читатель только для чтения', () => {
  it('читатель: нет «Изменить», корзины, «Выполнить с AI»; статус выключен с подсказкой редактора', async () => {
    m.project = { myRole: 'reader' }; m.width = 500;
    const host = await mount();
    expect(buttonsByText(host, 'Изменить')).toHaveLength(0);
    expect(buttonsByText(host, 'Выполнить с AI')).toHaveLength(0);
    expect(host.querySelector('[title="Удалить задачу"]')).toBeNull();
    const seg = statusButtons(host);
    expect(seg).toHaveLength(3);
    for (const b of seg) {
      expect(b.getAttribute('disabled')).not.toBeNull();
      expect(b.getAttribute('title')).toBe(DENIED_TITLE);
    }
  });

  it('читатель, широкая панель: рельс «Статус» тоже выключен, сегмента нет', async () => {
    m.project = { myRole: 'reader' }; m.width = 900;
    const host = await mount();
    expect(buttonsByText(host, 'Изменить')).toHaveLength(0);
    expect(host.querySelector('[title="Удалить задачу"]')).toBeNull();
    const rail = statusButtons(host);
    expect(rail).toHaveLength(3);
    for (const b of rail) {
      expect(b.getAttribute('disabled')).not.toBeNull();
      expect(b.getAttribute('title')).toBe(DENIED_TITLE);
    }
  });

  it('читатель: startInEdit игнорируется, формы правки нет', async () => {
    m.project = { myRole: 'reader' }; m.width = 500;
    const host = await mount({}, true);
    expect(host.querySelector('[data-testid="edit-form"]')).toBeNull();
    expect(host.innerHTML).not.toContain('ФОРМА ПРАВКИ');
  });

  it('читатель: панель не отзывается на AI-действия палитры', async () => {
    m.project = { myRole: 'reader' }; m.width = 500;
    const host = await mount();
    apiSpy.tasks.execute.mockClear(); apiSpy.tasks.aiClassify.mockClear(); apiSpy.tasks.aiFindDuplicate.mockClear();
    for (const action of ['task.execute', 'task.description', 'task.subtasks', 'task.classify', 'task.dedup']) {
      await dispatchAi(action);
    }
    expect(apiSpy.tasks.execute).not.toHaveBeenCalled();
    expect(apiSpy.tasks.aiClassify).not.toHaveBeenCalled();
    expect(apiSpy.tasks.aiFindDuplicate).not.toHaveBeenCalled();
    expect(host.querySelector('[data-testid="edit-form"]')).toBeNull();
  });

  // Ревью 47fd9dd9 (п.1): строки подзадач видны, но у читателя клик не идёт в updateTask
  it('читатель: клик по строке подзадачи не зовёт api.tasks.update', async () => {
    m.project = { myRole: 'reader' }; m.width = 500;
    const host = await mount({ subtasks: [{ id: 's1', title: SUBTASK_TITLE, isDone: false }] });
    const row = subtaskRow(host);
    expect(row).not.toBeNull(); // чекбокс и строка остаются видимыми
    apiSpy.tasks.update.mockClear();
    await clickEl(row!);
    expect(apiSpy.tasks.update).not.toHaveBeenCalled();
  });

  // Ревью 47fd9dd9 (п.2): читателю onAssign не отдаём — «Назначить» нет в DOM, форма правки не откроется
  it('читатель: «была на…» без кнопки «Назначить», формы правки нет', async () => {
    m.project = { myRole: 'reader', formerMembers: FORMER }; m.view = VIEW; m.width = 500;
    const host = await mount({ assigneeUserId: 'u-old' });
    expect(host.querySelector('[data-testid="former-assignee"]')).not.toBeNull(); // строка видна
    expect(buttonsByText(host, 'Назначить')).toHaveLength(0);
    expect(host.querySelector('[data-testid="edit-form"]')).toBeNull();
  });
});

describe('панель деталей: редактор общего проекта правит задачи (как владелец)', () => {
  it.each(['editor', 'owner'] as const)('%s: действия на месте, статус кликается без подсказки, AI-действия работают', async (role) => {
    m.project = { myRole: role }; m.width = 500;
    // Задача своя (createdByUserId=я): корзина редактора на своей задаче есть (доработка F2-Q3)
    const host = await mount({ createdByUserId: ME });
    expect(buttonsByText(host, 'Изменить')).toHaveLength(1);
    expect(buttonsByText(host, 'Выполнить с AI')).toHaveLength(1);
    expect(host.querySelector('[title="Удалить задачу"]')).not.toBeNull();
    const seg = statusButtons(host);
    expect(seg).toHaveLength(3);
    for (const b of seg) {
      expect(b.getAttribute('disabled')).toBeNull();
      expect(b.getAttribute('title')).toBeNull();
    }
    apiSpy.tasks.execute.mockClear();
    await dispatchAi('task.execute');
    expect(apiSpy.tasks.execute).toHaveBeenCalledTimes(1);
  });

  it('editor: подзадача кликается (update один раз), «Назначить» на месте и открывает форму правки', async () => {
    m.project = { myRole: 'editor', formerMembers: FORMER }; m.view = VIEW; m.width = 500;
    const host = await mount({ subtasks: [{ id: 's1', title: SUBTASK_TITLE, isDone: false }], assigneeUserId: 'u-old' });
    const row = subtaskRow(host);
    expect(row).not.toBeNull();
    apiSpy.tasks.update.mockClear();
    await clickEl(row!);
    expect(apiSpy.tasks.update).toHaveBeenCalledTimes(1);
    const btn = buttonsByText(host, 'Назначить');
    expect(btn).toHaveLength(1);
    await clickEl(btn[0]);
    expect(host.querySelector('[data-testid="edit-form"]')).not.toBeNull();
  });
});

// Доработка F2-Q3 (ревью 11716b9b): корзина — автору или владельцу (зеркало бэкового
// TaskDeleteDenied, TasksController.cs:243-248). Читатель покрыт тестами выше (там корзины
// нет при любом авторе); здесь — остальные инварианты: чужой автор, свой автор, без автора.
describe('панель деталей: корзина — автору или владельцу', () => {
  it('редактор: чужой автор — корзины нет (бэк ответил бы 403 not_task_author)', async () => {
    m.project = { myRole: 'editor' }; m.width = 500;
    const host = await mount({ createdByUserId: 'u-other' });
    expect(host.querySelector('[title="Удалить задачу"]')).toBeNull();
  });

  it('редактор: свой автор — корзина есть', async () => {
    m.project = { myRole: 'editor' }; m.width = 500;
    const host = await mount({ createdByUserId: ME });
    expect(host.querySelector('[title="Удалить задачу"]')).not.toBeNull();
  });

  it('редактор: задача без автора — корзины нет (fail-closed, как на бэке)', async () => {
    m.project = { myRole: 'editor' }; m.width = 500;
    const host = await mount();
    expect(host.querySelector('[title="Удалить задачу"]')).toBeNull();
  });

  it('владелец: чужая задача — корзина есть (на любой)', async () => {
    m.project = { myRole: 'owner' }; m.width = 500;
    const host = await mount({ createdByUserId: 'u-other' });
    expect(host.querySelector('[title="Удалить задачу"]')).not.toBeNull();
  });
});

describe('панель деталей: владелец — без изменений', () => {
  it('владелец: подзадача кликается (update один раз), «Назначить» на месте и открывает форму правки', async () => {
    m.project = { myRole: 'owner', formerMembers: FORMER }; m.view = VIEW; m.width = 500;
    const host = await mount({ subtasks: [{ id: 's1', title: SUBTASK_TITLE, isDone: false }], assigneeUserId: 'u-old' });
    const row = subtaskRow(host);
    expect(row).not.toBeNull();
    apiSpy.tasks.update.mockClear();
    await clickEl(row!);
    expect(apiSpy.tasks.update).toHaveBeenCalledTimes(1);
    const btn = buttonsByText(host, 'Назначить');
    expect(btn).toHaveLength(1);
    await clickEl(btn[0]);
    expect(host.querySelector('[data-testid="edit-form"]')).not.toBeNull();
  });
});
