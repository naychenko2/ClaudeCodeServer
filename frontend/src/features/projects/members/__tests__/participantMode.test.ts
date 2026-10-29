// Режим участника общего проекта (S1-F6): стопка аватаров в крошке, плашки владельца и
// отзыва доступа, блокировка композера. Окружение vitest — node без jsdom: стопка и плашки
// монтируются в fake-DOM с заглушками контролов (проверяются решения встраивания, а не
// вёрстка ui-кита), композер и входы запуска хода вне него — поведением чистых функций
// participantGate (Composer/ChatPanel целиком в fake-DOM не поднять: сотни хуков и рефы).
import { readFileSync } from 'node:fs';
import { createElement } from 'react';
import type { ReactElement } from 'react';
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Project } from '../../../../types';

const { FakeNode, reg } = vi.hoisted(() => {
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
  // Реестр заглушек: что получили контролы
  const reg = {
    press: null as any,
    avatars: [] as string[],
    notices: [] as { tone: string; text: string; action: any }[],
    buttons: [] as any[],
  };
  return { FakeNode: _FakeNode as typeof _FakeNode, reg };
});

vi.mock('../../../../components/ui/PressableArea', async () => {
  const { createElement: h } = await import('react');
  return { PressableArea: (p: any) => { reg.press = p; return h('div', null, p.children); } };
});
vi.mock('../../../../components/ui/UserAvatar', () => ({
  UserAvatar: (p: any) => { reg.avatars.push(p.userId); return createElement('i'); },
}));
vi.mock('../../../../components/ui/Notice', async () => {
  const { createElement: h } = await import('react');
  return {
    Notice: (p: any) => {
      reg.notices.push({ tone: p.tone, text: String(p.children), action: p.action });
      return h('div', null, p.children, p.action);
    },
  };
});
vi.mock('../../../../components/ui/Button', async () => {
  const { createElement: h } = await import('react');
  return { Button: (p: any) => { reg.buttons.push(p); return h('button'); } };
});

import { MembersStack } from '../MembersStack';
import { OwnerSettingsNotice } from '../OwnerSettingsNotice';
import {
  EDITOR_TURNS_ENABLED, canCreateChats, composerExecBlocked, guardTurnAction, turnDeniedReason, participantComposerGate, participantComposerText,
} from '../participantGate';

type FN = InstanceType<typeof FakeNode>;
let container: FN;
async function mount(el: ReactElement) {
  container = new FakeNode();
  const root = createRoot(container);
  await act(async () => { root.render(el); });
}
function texts(n: FN, acc: string[] = []): string[] {
  if (n.nodeType === 3) { acc.push(n.textContent); return acc; }
  if (n.childNodes.length === 0 && n.textContent) acc.push(n.textContent);
  for (const c of n.childNodes) texts(c, acc);
  return acc;
}

const person = (id: string) => ({ userId: id, username: id });
const base = { id: 'p', name: 'П', rootPath: '/p', ownerId: 'andrey', ownerDisplayName: 'Андрей' } as Project;
const reader = { ...base, myRole: 'reader' } as Project;
const editor = { ...base, myRole: 'editor' } as Project;
const own = { ...base, ownerId: 'me', ownerDisplayName: null, myRole: 'owner' } as Project;

beforeEach(() => { reg.press = null; reg.avatars = []; reg.notices = []; reg.buttons = []; });

describe('стопка аватаров', () => {
  it('до трёх аватаров и «+N», клик открывает настройки', async () => {
    const onOpen = vi.fn();
    const project = { ...own, members: { count: 5, first: [person('a'), person('b'), person('c'), person('d')] } } as Project;
    await mount(createElement(MembersStack, { project, onOpen }));
    expect(reg.avatars).toEqual(['a', 'b', 'c']);
    expect(texts(container).join('')).toBe('+2');
    expect(reg.press.title).toBe('Участники проекта — 5');
    reg.press.onPress();
    expect(onOpen).toHaveBeenCalledTimes(1);
  });

  it('три участника — без «+N»', async () => {
    const project = { ...own, members: { count: 3, first: [person('a'), person('b'), person('c')] } } as Project;
    await mount(createElement(MembersStack, { project, onOpen: vi.fn() }));
    expect(reg.avatars).toHaveLength(3);
    expect(texts(container).join('')).toBe('');
  });

  it('нет участников — стопки нет', async () => {
    await mount(createElement(MembersStack, { project: { ...own, members: { count: 0, first: [] } } as Project, onOpen: vi.fn() }));
    expect(reg.press).toBeNull();
  });
});

describe('композер участника', () => {
  it('читатель и редактор заблокированы, владелец и обычный проект — нет', () => {
    expect(participantComposerGate(reader).blocked).toBe(true);
    expect(participantComposerGate(editor).blocked).toBe(true);
    expect(participantComposerGate(own).blocked).toBe(false);
    expect(participantComposerGate(base).blocked).toBe(false);
    expect(participantComposerGate(null).blocked).toBe(false);
  });

  it('выключатель ходов редактора выключен (до S2a-10)', () => {
    expect(EDITOR_TURNS_ENABLED).toBe(false);
  });

  it('текст читателя называет владельца и роль редактора', () => {
    const gate = participantComposerGate(reader);
    if (!gate.blocked) throw new Error('ожидалась блокировка');
    expect(participantComposerText(gate)).toBe('Вы читатель — писать в чаты нельзя. Попросите Андрей сделать вас редактором.');
  });

  it('входы создания чата скрыты только у читателя', () => {
    expect(canCreateChats(reader)).toBe(false);
    expect(canCreateChats(editor)).toBe(true);
    expect(canCreateChats(own)).toBe(true);
  });

  // Composer целиком в fake-DOM не поднять (сотни хуков, рефы textarea): решение о блокировке
  // вынесено в чистую composerExecBlocked, из неё берутся и disabled отправки, и сброс режимов
  it('composerExecBlocked: участник блокирует отправку даже при доступном исполнении', () => {
    const ok = (p: Project) => composerExecBlocked(p, true, participantComposerGate(p));
    expect(ok(reader)).toBe(true);
    expect(ok(editor)).toBe(true);
    expect(ok(own)).toBe(false);
    expect(ok(base)).toBe(false);
    expect(composerExecBlocked(own, false, participantComposerGate(own))).toBe(true);
    expect(composerExecBlocked(null, false, participantComposerGate(null))).toBe(false);
  });
});

describe('плашки владельца', () => {
  it('участник видит «ходы идут по его настройкам и за его счёт»', async () => {
    await mount(createElement(OwnerSettingsNotice, { project: reader, onGoHome: vi.fn() }));
    expect(reg.notices).toHaveLength(1);
    expect(reg.notices[0].tone).toBe('info');
    expect(reg.notices[0].text).toContain('Андрей');
    expect(reg.notices[0].text).toContain('за его счёт');
  });

  it('отозванный доступ — warning и «На Домой»', async () => {
    const onGoHome = vi.fn();
    await mount(createElement(OwnerSettingsNotice, { project: editor, revoked: true, onGoHome }));
    expect(reg.notices[0].tone).toBe('warning');
    expect(reg.notices[0].text).toContain('закрыл вам доступ');
    reg.buttons[0].onClick();
    expect(onGoHome).toHaveBeenCalledTimes(1);
  });

  it('владельцу и обычному проекту — ничего', async () => {
    await mount(createElement(OwnerSettingsNotice, { project: own, onGoHome: vi.fn() }));
    await mount(createElement(OwnerSettingsNotice, { project: base, revoked: true, onGoHome: vi.fn() }));
    expect(reg.notices).toHaveLength(0);
  });
});

describe('входы запуска хода вне композера (гейт в точке отправки)', () => {
  // Те же действия, что ChatPanel заворачивает: send (чипы, «Повторить», «Зафиксировать»,
  // «Возобновить», механики команды, сброс режимов), ответ на разрешение/вопрос/план
  const entries = ['send', 'allowPermission', 'allowAlways', 'answerQuestion', 'respondPlan',
    'denyPermission', 'respondTeamPlan', 'respondTeamEscalation'] as const;

  it.each(entries)('%s: у читателя и редактора не доходит до сервера, показывает причину', (name) => {
    for (const p of [reader, editor]) {
      const action = vi.fn(() => 'ok');
      const denied = vi.fn();
      const guarded = guardTurnAction(() => p, action, denied);
      expect(guarded('x')).toBeUndefined();
      expect(action, `${name}/${p.myRole}`).not.toHaveBeenCalled();
      expect(denied).toHaveBeenCalledTimes(1);
      expect(denied.mock.calls[0][0]).toBe(turnDeniedReason(p));
    }
  });

  it.each(entries)('%s: ChatPanel заворачивает вход в гейт', (name) => {
    const src = readFileSync(new URL('../../../../components/ChatPanel.tsx', import.meta.url), 'utf8');
    const cap = name[0].toUpperCase() + name.slice(1);
    expect(src).toContain(`${name}: guardTurnAction(get, raw${cap}, deny)`);
  });

  it.each(entries)('%s: владелец и обычный проект проходят', (name) => {
    for (const p of [own, base, null]) {
      const action = vi.fn((a: string) => a + '!');
      const denied = vi.fn();
      const guarded = guardTurnAction(() => p, action, denied);
      expect(guarded(name)).toBe(name + '!');
      expect(denied).not.toHaveBeenCalled();
    }
  });

  it('роль читается в момент вызова: смена роли после создания обёртки учитывается', () => {
    let current: Project = own;
    const action = vi.fn();
    const guarded = guardTurnAction(() => current, action, vi.fn());
    guarded();
    current = reader;
    guarded();
    expect(action).toHaveBeenCalledTimes(1);
  });

  it('тексты отказа различают читателя и редактора', () => {
    expect(turnDeniedReason(reader)).toBe('Недоступно: вы читатель проекта');
    expect(turnDeniedReason(editor)).toBe('Недоступно: ходы редактора пока выключены');
    expect(turnDeniedReason(own)).toBeNull();
  });
});
