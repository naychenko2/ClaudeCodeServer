// Вкладка «Участники» (S1-F3): кликовые сценарии секции (вид списка — MembersList.test.ts).
// Кликовые тесты — fake-DOM + createRoot/act (механизм как в ui/Tooltip.test.ts): контролы
// ui-кита подменены заглушками, которые запоминают свои props, и тест «нажимает» их
// напрямую; api — мок. Так проверяются именно решения секции: что уходит в api и что
// остаётся в поле, а не вёрстка контролов.
import { createElement, Fragment } from 'react';
import type { ReactNode } from 'react';
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { MEMBER_REFUSAL } from '../../../../types';
import type { Project, ProjectMember, ProjectMembersView } from '../../../../types';

// fake-DOM поднимается ДО импорта React-кода (createRoot читает document)
const { _doc, FakeNode, reg } = vi.hoisted(() => {
  class _FakeNode {
    nodeType = 1;
    nodeName = 'div';
    childNodes: _FakeNode[] = [];
    parentElement: _FakeNode | null = null;
    _attrs: Record<string, string> = {};
    style: any = { setProperty: () => {} };
    textContent = '';
    setAttribute(k: string, v: string) { this._attrs[k] = String(v); }
    getAttribute(k: string) { return this._attrs[k] ?? null; }
    removeAttribute(k: string) { delete this._attrs[k]; }
    get firstChild() { return this.childNodes[0] ?? null; }
    get parentNode() { return this.parentElement; }
    appendChild(c: _FakeNode) { c.parentElement = this; this.childNodes.push(c); return c; }
    insertBefore(c: _FakeNode, ref: _FakeNode | null) {
      c.parentElement = this;
      if (ref == null) this.childNodes.push(c);
      else { const i = this.childNodes.indexOf(ref); this.childNodes.splice(i < 0 ? this.childNodes.length : i, 0, c); }
      return c;
    }
    removeChild(c: _FakeNode) { const i = this.childNodes.indexOf(c); if (i >= 0) this.childNodes.splice(i, 1); c.parentElement = null; return c; }
    get ownerDocument() { return _doc; }
    get tagName() { return this.nodeName.toUpperCase(); }
    addEventListener() {}
    removeEventListener() {}
  }
  class _FakeIframe {}
  const _doc: any = {
    createElement: (t: string) => { const n = new _FakeNode(); n.nodeName = t; return n; },
    createElementNS: (_ns: string, t: string) => { const n = new _FakeNode(); n.nodeName = t; return n; },
    createTextNode: (t: string) => { const n = new _FakeNode(); n.nodeType = 3; n.textContent = t; return n; },
    createComment: (t: string) => { const n = new _FakeNode(); n.nodeType = 8; n.textContent = t; return n; },
    body: new _FakeNode(),
    documentElement: new _FakeNode(),
    getElementById: () => null,
    addEventListener: () => {},
    removeEventListener: () => {},
  };
  const _win: any = {
    document: _doc, defaultView: null,
    HTMLIFrameElement: _FakeIframe, HTMLInputElement: class {}, HTMLSelectElement: class {}, HTMLTextAreaElement: class {},
    addEventListener: () => {}, removeEventListener: () => {},
    location: { href: '' }, navigator: { userAgent: 'test' },
    setTimeout: () => 0, clearTimeout: () => {},
    innerWidth: 1024,
    matchMedia: (_q: string) => ({ matches: false, media: _q, addEventListener: () => {}, removeEventListener: () => {}, onchange: null, dispatchEvent: () => true }),
  };
  (globalThis as any).document = _doc;
  (globalThis as any).window = _win;
  (globalThis as any).IS_REACT_ACT_ENVIRONMENT = true;
  _doc.defaultView = _win;
  // Реестр props заглушек: последний рендер каждого контрола
  const reg = {
    text: null as any,
    field: null as any,
    buttons: {} as Record<string, any>,
    confirms: {} as Record<string, any>,
    items: {} as Record<string, any>,
    list: null as any,
  };
  return { _doc, FakeNode: _FakeNode as typeof _FakeNode, reg };
});

const api = vi.hoisted(() => ({
  list: vi.fn(), add: vi.fn(), changeRole: vi.fn(), remove: vi.fn(), leave: vi.fn(),
}));
vi.mock('../../../../lib/api', () => ({ api: { projects: { members: api } } }));
vi.mock('../../../../lib/defaultPersona', () => ({ useMe: () => ({ userId: 'u-owner' }) }));
vi.mock('../../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => false };
});
vi.mock('../../dialogs/AccordionSection', () => ({
  AccordionSection: (p: { children: ReactNode }) => createElement(Fragment, null, p.children),
}));
vi.mock('../../../../components/ui', async () => {
  const { createElement: h, Fragment: F } = await import('react');
  return {
    Button: (p: any) => { reg.buttons[String(p.children)] = p; return h('button'); },
    Field: (p: any) => { reg.field = p; return h(F, null, p.children); },
    TextField: (p: any) => { reg.text = p; return h('input'); },
    Select: () => h('select'),
    Notice: () => h('div'),
    ConfirmDialog: (p: any) => { reg.confirms[p.title] = p; return h('confirm', { 'data-title': p.title }); },
  };
});
vi.mock('../../../../components/ui/Menu', async () => {
  const { createElement: h, Fragment: F } = await import('react');
  return {
    Menu: (p: any) => h('menu', null, h(F, null, p.children)),
    MenuItem: (p: any) => { reg.items[p.label] = p; return h('item'); },
  };
});
vi.mock('../MembersList', () => ({
  MembersList: (p: any) => { reg.list = p; return createElement('div'); },
}));
// Подписка на событие состава проекта: мок запоминает хендлеры и отдаёт тестируемую отписку.
// joinProject/leaveProject — членство в группе проекта (без него событие не доходит):
// сам счётчик держит signalr.ts, здесь проверяется, что секция им пользуется.
const signalrMock = vi.hoisted(() => ({
  onProjectMembersChanged: vi.fn(),
  joinProject: vi.fn(async () => {}),
  leaveProject: vi.fn(async () => {}),
}));
const subs = { handlers: [] as Array<(d: { projectId: string }) => void>, offs: [] as Array<() => void> };
vi.mock('../../../../lib/signalr', () => ({
  onProjectMembersChanged: signalrMock.onProjectMembersChanged,
  joinProject: signalrMock.joinProject,
  leaveProject: signalrMock.leaveProject,
}));

import { MembersSection } from '../MembersSection';

type FN = InstanceType<typeof FakeNode>;

const owner = { userId: 'u-owner', username: 'andrey', displayName: 'Андрей' };
const maria: ProjectMember = { userId: 'u-maria', username: 'maria', displayName: 'Мария', role: 'editor' };
const petr: ProjectMember = { userId: 'u-petr', username: 'petr', displayName: 'Пётр', role: 'reader' };
const project = { id: 'p1', name: 'Test', rootPath: '/x' } as Project;
const view = (members: ProjectMember[]): ProjectMembersView => ({ owner, members }) as ProjectMembersView;

const CONFIRM_FIRST = 'Открыть проект для участников?';

// === кликовые сценарии секции ===

function hasNode(n: FN, tag: string, title?: string): boolean {
  if (n.nodeName === tag && (title === undefined || n._attrs['data-title'] === title)) return true;
  return n.childNodes.some(c => hasNode(c, tag, title));
}

let container: FN;

async function mount(mode: 'owner' | 'member', initial: ProjectMember[], props: Record<string, unknown> = {}) {
  api.list.mockResolvedValue(view(initial));
  container = new FakeNode();
  const root = createRoot(container);
  await act(async () => { root.render(createElement(MembersSection, { project, mode, ...props })); });
  return root;
}

const typeName = (v: string) => act(async () => { reg.text.onChange(v); });
const clickAdd = () => act(async () => { reg.buttons['Добавить'].onClick(); });
const refusal = (code: string) => Object.assign(new Error('refused'), { status: 409, body: { code } });

beforeEach(() => {
  vi.clearAllMocks();
  reg.text = null; reg.field = null; reg.list = null;
  reg.buttons = {}; reg.confirms = {}; reg.items = {};
  subs.handlers = []; subs.offs = [];
  signalrMock.onProjectMembersChanged.mockImplementation((h: (d: { projectId: string }) => void) => {
    subs.handlers.push(h);
    const off = vi.fn();
    subs.offs.push(off);
    return off;
  });
});

describe('секция «Участники»: приглашение', () => {
  it('отказ не стирает введённое имя', async () => {
    for (const rejection of [
      refusal(MEMBER_REFUSAL.UserNotFound),
      refusal(MEMBER_REFUSAL.AlreadyMember),
      Object.assign(new Error('offline'), {}),
    ]) {
      api.add.mockReset();
      api.add.mockRejectedValue(rejection);
      await mount('owner', [maria]);
      await typeName('ghost');
      await clickAdd();
      expect(api.add).toHaveBeenCalledTimes(1);
      expect(reg.text.value, 'имя стёрто после отказа').toBe('ghost');
      expect(reg.field.error, 'под полем нет причины отказа').toBeTruthy();
    }
  });

  it('первое приглашение идёт через подтверждение, второе — без него', async () => {
    api.add.mockResolvedValue(view([maria]));
    await mount('owner', []);
    await typeName('maria');
    await clickAdd();
    expect(hasNode(container, 'confirm', CONFIRM_FIRST), 'нет подтверждения первого приглашения').toBe(true);
    expect(api.add, 'добавили до подтверждения').not.toHaveBeenCalled();

    await act(async () => { await reg.confirms[CONFIRM_FIRST].onConfirm(); });
    expect(api.add).toHaveBeenCalledWith('p1', 'maria', 'editor');
    expect(hasNode(container, 'confirm', CONFIRM_FIRST), 'подтверждение не закрылось').toBe(false);

    // участник уже есть → следующее приглашение без вопроса
    api.add.mockClear();
    await typeName('petr');
    await clickAdd();
    expect(hasNode(container, 'confirm', CONFIRM_FIRST), 'подтверждение показано повторно').toBe(false);
    expect(api.add).toHaveBeenCalledWith('p1', 'petr', 'editor');
  });

  it('отмена подтверждения ничего не добавляет', async () => {
    await mount('owner', []);
    await typeName('maria');
    await clickAdd();
    await act(async () => { reg.confirms[CONFIRM_FIRST].onCancel(); });
    expect(api.add).not.toHaveBeenCalled();
    expect(hasNode(container, 'confirm', CONFIRM_FIRST)).toBe(false);
    expect(reg.text.value).toBe('maria');
  });
});

describe('секция «Участники»: действия над участником', () => {
  it('смена роли шлёт changeRole с userId строкой', async () => {
    api.changeRole.mockResolvedValue(view([{ ...maria, role: 'reader' }]));
    await mount('owner', [maria]);
    await act(async () => { reg.list.onRoleChange(maria, 'reader'); });
    expect(api.changeRole).toHaveBeenCalledWith('p1', 'u-maria', 'reader');
    expect(typeof api.changeRole.mock.calls[0][1]).toBe('string');
    expect(api.remove).not.toHaveBeenCalled();
  });

  it('удаление: меню → подтверждение → remove с userId строкой', async () => {
    api.remove.mockResolvedValue(view([]));
    await mount('owner', [maria]);
    await act(async () => { reg.list.onMenu(maria, {} as DOMRect); });
    await act(async () => { reg.items['Убрать из проекта'].onClick(); });
    const title = 'Убрать Мария из проекта?';
    expect(hasNode(container, 'confirm', title), 'нет подтверждения удаления').toBe(true);
    expect(api.remove, 'удалили до подтверждения').not.toHaveBeenCalled();
    await act(async () => { await reg.confirms[title].onConfirm(); });
    expect(api.remove).toHaveBeenCalledWith('p1', 'u-maria');
    expect(typeof api.remove.mock.calls[0][1]).toBe('string');
    expect(api.changeRole).not.toHaveBeenCalled();
  });

  it('участник: «Покинуть проект» → подтверждение → leave и onLeft', async () => {
    api.leave.mockResolvedValue(undefined);
    const onLeft = vi.fn();
    await mount('member', [maria], { onLeft });
    await act(async () => { reg.buttons['Покинуть проект'].onClick(); });
    expect(hasNode(container, 'confirm', 'Покинуть проект?'), 'нет подтверждения выхода').toBe(true);
    expect(api.leave).not.toHaveBeenCalled();
    await act(async () => { await reg.confirms['Покинуть проект?'].onConfirm(); });
    expect(api.leave).toHaveBeenCalledWith('p1');
    expect(onLeft).toHaveBeenCalledTimes(1);
  });
});

describe('секция «Участники»: вид по роли', () => {
  it('владелец: форма «Имя учётки» с кнопкой «Добавить», без «Покинуть»', async () => {
    await mount('owner', []);
    expect(reg.field.label).toBe('Имя учётки');
    expect(reg.buttons['Добавить']).toBeTruthy();
    expect(reg.buttons['Покинуть проект']).toBeUndefined();
  });

  it('участник: «Покинуть проект» есть, формы приглашения нет', async () => {
    await mount('member', []);
    expect(reg.buttons['Покинуть проект']).toBeTruthy();
    expect(reg.field).toBeNull();
  });
});

describe('секция «Участники»: живое обновление состава (S1-F7)', () => {
  it('событие по своему проекту перечитывает список и передаёт его вызывающему', async () => {
    const onMembersChanged = vi.fn();
    await mount('owner', [maria], { onMembersChanged });
    expect(signalrMock.onProjectMembersChanged, 'секция не подписана на событие').toHaveBeenCalledTimes(1);

    api.list.mockResolvedValue(view([maria, petr]));
    await act(async () => { subs.handlers[0]({ projectId: 'p1' }); });

    expect(api.list).toHaveBeenCalledTimes(2);
    expect(reg.list.members.map((m: ProjectMember) => m.userId)).toEqual(['u-maria', 'u-petr']);
    // сводка в карточке проекта живёт через этот колбэк — он обязан получить новый список
    expect(onMembersChanged).toHaveBeenCalledTimes(1);
    expect(onMembersChanged.mock.calls[0][0].map((m: ProjectMember) => m.userId)).toEqual(['u-maria', 'u-petr']);
  });

  it('событие чужого проекта ничего не перечитывает', async () => {
    await mount('owner', [maria]);
    await act(async () => { subs.handlers[0]({ projectId: 'other' }); });
    expect(api.list).toHaveBeenCalledTimes(1);
    expect(reg.list.members.map((m: ProjectMember) => m.userId)).toEqual(['u-maria']);
  });

  it('отписка при размонтировании', async () => {
    const root = await mount('member', [maria]);
    expect(subs.offs[0]).not.toHaveBeenCalled();
    await act(async () => { root.unmount(); });
    expect(subs.offs[0]).toHaveBeenCalledTimes(1);
  });

  it('на время своей жизни секция входит в группу проекта и выходит при закрытии', async () => {
    // без членства в группе project_{id} событие состава до секции не доходит
    // (страница «Проекты» в группу сама не входит); выход считает signalr.ts
    const root = await mount('owner', [maria]);
    expect(signalrMock.joinProject, 'секция не вошла в группу проекта').toHaveBeenCalledWith('p1');
    await act(async () => { root.unmount(); });
    expect(signalrMock.leaveProject, 'закрытие секции не вышло из группы').toHaveBeenCalledWith('p1');
  });
});
