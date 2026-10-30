// S2a-F6: тумблер «И для участников» в MCP-аккордеоне проекта.
// Механизм — fake-DOM + createRoot/act (как в members/__tests__/MembersSection.test.ts):
// контролы ui-кита подменены заглушками, которые запоминают свои props, api — мок.
// Проверяются решения секции: у кого тумблер есть и по какому состоянию, что уходит в
// PUT {id}/shared и что остаётся при отказе — а не вёрстка контролов.
import { createElement, Fragment } from 'react';
import type { ReactNode } from 'react';
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { McpServer, Project } from '../../../types';

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
    removeAttribute(k: string) { delete this._attrs[k] };
    get firstChild() { return this.childNodes[0] ?? null; }
    get parentNode() { return this.parentElement; }
    appendChild(c: _FakeNode) { c.parentElement = this; this.childNodes.push(c); return c; }
    insertBefore(c: _FakeNode, ref: _FakeNode | null) {
      c.parentElement = this;
      if (ref == null) this.childNodes.push(c);
      else { const i = this.childNodes.indexOf(ref); if (i >= 0) { this.childNodes.splice(i, 0, c); } else { this.childNodes.push(c); } }
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
  // Реестр заглушек: последние props каждого тумблера, по его aria-label
  const reg = { toggles: {} as Record<string, any> };
  return { _doc, FakeNode: _FakeNode as typeof _FakeNode, reg };
});

const apiMock = vi.hoisted(() => ({
  list: vi.fn(), setShared: vi.fn(), update: vi.fn(),
}));
vi.mock('../../../lib/api', () => ({
  api: { mcp: { list: apiMock.list, setShared: apiMock.setShared }, projects: { update: apiMock.update } },
}));
vi.mock('../../../lib/defaultPersona', () => ({ useMe: () => ({ userId: 'me-id' }) }));
vi.mock('../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => false };
});
vi.mock('../../projects/dialogs/AccordionSection', () => ({
  AccordionSection: (p: { children: ReactNode }) => createElement(Fragment, null, p.children),
}));
vi.mock('../../../components/ui', async () => {
  const { createElement: h } = await import('react');
  return {
    Toggle: (p: any) => { reg.toggles[String(p.ariaLabel)] = p; return h('div', { 'data-checked': String(p.checked) }); },
    ConfirmDialog: () => h('div'),
  };
});

import { McpProjectSection } from '../McpProjectSection';

type FN = InstanceType<typeof FakeNode>;

// Данные: два сервера включены в проект (один уже помечен «для участников», другой нет),
// третий в mcpServersOn не входит. Проекты: свой общий (myRole owner + участники),
// личный (без участников), участник (myRole editor), старый бэк без myRole.
const srvOn = { id: 's1', key: 'srv1', label: 'github', enabled: true, sharedWithMembers: false } as McpServer;
const srvMarked = { id: 's2', key: 'srv2', label: 'tracker', enabled: true, sharedWithMembers: true } as McpServer;
const srvOff = { id: 's3', key: 'srv3', label: 'off-key', enabled: true, sharedWithMembers: false } as McpServer;

const ownShared = {
  id: 'p', name: 'П', rootPath: '/p', ownerId: 'me-id', updatedAt: '2026-10-30T00:00:00Z',
  myRole: 'owner', members: { count: 1, first: [] }, mcpServersOn: ['srv1', 'srv2'],
} as Project;
const plain = { ...ownShared, myRole: undefined, members: null } as Project;
const participant = { ...ownShared, myRole: 'editor' } as Project;
// Старый бэк без myRole: тот же результат по ownerId + составу (тот же приём, что в kindOf)
const legacyOwner = { ...ownShared, myRole: undefined } as Project;

let container: FN;

async function mount(project: Project) {
  container = new FakeNode();
  const root = createRoot(container);
  await act(async () => { root.render(createElement(McpProjectSection, { project })); });
}

// Весь видимый текст смонтированного дерева (тот же приём, что в sharedProjectView.test.ts:
// элемент с одним текстом React пишет прямо в textContent)
function texts(n: FN, acc: string[] = []): string[] {
  if (n.nodeType === 3) { acc.push(n.textContent); return acc; }
  if (n.childNodes.length === 0 && n.textContent) acc.push(n.textContent);
  for (const c of n.childNodes) texts(c, acc);
  return acc;
}

async function flip(label: string, to: boolean) {
  await act(async () => { reg.toggles[label].onChange(to); });
}

beforeEach(() => {
  vi.clearAllMocks();
  reg.toggles = {};
  apiMock.list.mockResolvedValue([srvOn, srvMarked, srvOff]);
  apiMock.update.mockResolvedValue(ownShared);
});

describe('S2a-F6: тумблер «И для участников»', () => {
  it('владелец общего проекта: тумблер только у серверов из mcpServersOn; состояние — из записи', async () => {
    await mount(ownShared);
    // включённый без пометки — есть и выключен; включённый с пометкой — есть и включён
    expect(reg.toggles['github для участников']).toBeTruthy();
    expect(reg.toggles['github для участников'].checked).toBe(false);
    expect(reg.toggles['tracker для участников']).toBeTruthy();
    expect(reg.toggles['tracker для участников'].checked).toBe(true);
    // не входящий в mcpServersOn — без тумблера пометки (у него только основной)
    expect(reg.toggles['off-key для участников']).toBeUndefined();
    expect(reg.toggles['off-key в проекте']).toBeTruthy();
    // короткая подсказка рядом
    expect(texts(container)).toContain('Без пометки сервер не доступен в ходах общего проекта.');
  });

  it('личный проект и участник — тумблера нет вовсе', async () => {
    await mount(plain);
    expect(reg.toggles['github для участников']).toBeUndefined();
    expect(reg.toggles['github в проекте']).toBeTruthy(); // основной тумблер остаётся
    reg.toggles = {};
    await mount(participant);
    expect(reg.toggles['github для участников']).toBeUndefined();
  });

  it('старый бэк без myRole: владелец по ownerId и составу — тумблер есть', async () => {
    await mount(legacyOwner);
    expect(reg.toggles['github для участников']).toBeTruthy();
  });

  it('переключение шлёт PUT {id}/shared с { sharedWithMembers:true }; ответ бэка становится истиной', async () => {
    apiMock.setShared.mockResolvedValue({ ...srvOn, sharedWithMembers: true });
    await mount(ownShared);
    await flip('github для участников', true);
    expect(apiMock.setShared).toHaveBeenCalledWith('s1', true);
    expect(reg.toggles['github для участников'].checked).toBe(true);
  });

  it('отказ (403/сеть): тумблер возвращается в прежнее состояние, рядом тихая подпись', async () => {
    apiMock.setShared.mockRejectedValue({ status: 403, body: { error: 'Пометку ставит только владелец' } });
    await mount(ownShared);
    await flip('github для участников', true);
    expect(reg.toggles['github для участников'].checked).toBe(false);
    expect(texts(container)).toContain('Не удалось сохранить');
  });
});
