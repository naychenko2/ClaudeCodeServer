// S2b-F4: человек-исполнитель и «Поставил …» в карточках задач. Чистая логика
// (resolveTaskAssignee/resolveCreatorName) и рендер через react-dom/client поверх linkedom.
import { describe, it, expect, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { ProjectMembersView } from '../../../types';

const m = vi.hoisted(() => ({
  view: null as unknown,
  project: undefined as unknown,
  loads: vi.fn(),
}));

vi.mock('../../projects/members/MessageAuthorLine', () => ({
  loadView: (...a: unknown[]) => { m.loads(...a); return Promise.resolve(m.view); },
}));
vi.mock('../../projects/useAllProjects', () => ({ getCachedProject: () => m.project }));
vi.mock('../../../lib/signalr', () => ({ onProjectMembersChanged: () => () => {} }));

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
Object.assign(win, {
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const {
  TaskUserBadge, CreatedByLine, FormerAssigneeLine, resolveTaskAssignee, resolveCreatorName,
  formatCreatedDate, UNASSIGNED_LABEL,
} = await import('../TaskUserBadge');

const VIEW: ProjectMembersView = {
  owner: { userId: 'u-owner', username: 'andrey', displayName: 'Андрей' },
  members: [{ userId: 'u-m', role: 'editor', displayName: 'Мария' }],
  myRole: 'owner',
};
const PROJECT = { formerMembers: [{ userId: 'u-old', displayName: 'Олег' }] };

async function render(el: ReturnType<typeof createElement>): Promise<string> {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  await act(async () => { root.render(el); });
  await act(async () => { await Promise.resolve(); });
  return host.innerHTML as string;
}

describe('resolveTaskAssignee', () => {
  it('участник и владелец → member с именем', () => {
    expect(resolveTaskAssignee('u-m', VIEW, PROJECT)).toEqual({ state: 'member', userId: 'u-m', name: 'Мария' });
    expect(resolveTaskAssignee('u-owner', VIEW, PROJECT)).toMatchObject({ state: 'member', name: 'Андрей' });
  });
  it('бывший участник → none с formerName; неизвестный → none без имени', () => {
    expect(resolveTaskAssignee('u-old', VIEW, PROJECT)).toEqual({ state: 'none', formerName: 'Олег' });
    expect(resolveTaskAssignee('u-x', VIEW, PROJECT)).toEqual({ state: 'none' });
  });
  it('список не загружен или нет id → null (не мигаем «Без исполнителя»)', () => {
    expect(resolveTaskAssignee('u-m', null, PROJECT)).toBeNull();
    expect(resolveTaskAssignee(null, VIEW, PROJECT)).toBeNull();
  });
});

describe('resolveCreatorName / formatCreatedDate', () => {
  it('имя владельца, участника, бывшего; запасное «Участник»', () => {
    expect(resolveCreatorName('u-owner', VIEW, PROJECT)).toBe('Андрей');
    expect(resolveCreatorName('u-old', VIEW, PROJECT)).toBe('Олег');
    expect(resolveCreatorName('u-x', VIEW, PROJECT)).toBe('Участник');
    expect(resolveCreatorName(null, VIEW, PROJECT)).toBeNull();
    expect(resolveCreatorName('u-m', null, PROJECT)).toBeNull(); // список не пришёл — без «Участник»
    // три состояния: имя из кэша сразу; null без списка и без имени; «Участник» — список пришёл, человека нет
    expect(resolveCreatorName('u-old', null, PROJECT)).toBe('Олег');
    expect(resolveCreatorName('u-x', null, PROJECT)).toBeNull();
    expect(resolveCreatorName('u-x', VIEW, PROJECT)).toBe('Участник');
  });
  it('дата без точки: «12 окт»', () => {
    expect(formatCreatedDate('2026-10-12T10:00:00Z')).toBe('12 окт');
    expect(formatCreatedDate('мусор')).toBeNull();
  });
});

describe('рендер', () => {
  it('участник → чип с аватаром и именем', async () => {
    m.view = VIEW; m.project = PROJECT;
    const html = await render(createElement(TaskUserBadge, { projectId: 'p1', assigneeUserId: 'u-m' }));
    expect(html).toContain('data-testid="task-user-badge"');
    expect(html).toContain('Мария');
    expect(html).not.toContain(UNASSIGNED_LABEL);
  });

  it('не из участников → «Без исполнителя», подсказка с бывшим именем', async () => {
    m.view = VIEW; m.project = PROJECT;
    const html = await render(createElement(TaskUserBadge, { projectId: 'p1', assigneeUserId: 'u-old' }));
    expect(html).toContain(UNASSIGNED_LABEL);
    expect(html).toContain('Была на Олег');
    expect(html).not.toContain('task-user-badge');
  });

  it('строка «была на Олег · Назначить»: кнопка только с onAssign', async () => {
    m.view = VIEW; m.project = PROJECT;
    const noBtn = await render(createElement(FormerAssigneeLine, { projectId: 'p1', assigneeUserId: 'u-old' }));
    expect(noBtn).toContain('была на Олег');
    expect(noBtn).not.toContain('Назначить');
    const withBtn = await render(createElement(FormerAssigneeLine, { projectId: 'p1', assigneeUserId: 'u-old', onAssign: () => {} }));
    expect(withBtn).toContain('Назначить');
    // MAJOR: инлайн all/box-shadow/outline перебивает :focus-visible из класса — кольца фокуса не будет
    const btnStyle = (withBtn.match(/<button[^>]*>/)?.[0]) ?? '';
    expect(btnStyle).toContain('cc-task-assign-link');
    for (const prop of ['all:', 'box-shadow', 'outline']) expect(btnStyle, `инлайн ${prop}`).not.toContain(prop);
    const unknown = await render(createElement(FormerAssigneeLine, { projectId: 'p1', assigneeUserId: 'u-x' }));
    expect(unknown).toBe(''); // неизвестный → строки нет
  });

  it('createdByUserId → «Поставил Андрей · 12 окт»; нет поля → строки нет', async () => {
    m.view = VIEW; m.project = PROJECT;
    const html = await render(createElement(CreatedByLine, { projectId: 'p1', createdByUserId: 'u-owner', createdAt: '2026-10-12T10:00:00Z' }));
    expect(html).toContain('Поставил Андрей · 12 окт');
    const none = await render(createElement(CreatedByLine, { projectId: 'p1', createdAt: '2026-10-12T10:00:00Z' }));
    expect(none).toBe('');
  });

  it('участники не загружены: «Поставил» не мигает, исполнитель показывает запасной вид', async () => {
    m.view = null; m.project = PROJECT;
    const by = await render(createElement(CreatedByLine, { projectId: 'p1', createdByUserId: 'u-m' }));
    expect(by).toBe('');
    const fb = await render(createElement(TaskUserBadge, { projectId: 'p1', assigneeUserId: 'u-m', fallback: createElement('i', null, 'ЗАПАС') }));
    expect(fb).toContain('ЗАПАС');
  });

  it('личный проект / задача без assigneeUserId → пусто и список не грузится', async () => {
    m.loads.mockClear();
    const html = await render(createElement(TaskUserBadge, { projectId: 'p1' }));
    expect(html).toBe('');
    expect(m.loads).not.toHaveBeenCalled();
  });
});
