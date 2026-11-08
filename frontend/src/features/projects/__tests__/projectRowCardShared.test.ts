// S1-F7 (хвосты ревью S1-F5, M-39/M-40): метка общего проекта в ProjectRow/ProjectCard
// и рабочий «Покинуть проект» в меню действий.
// Кликовые тесты — fake-DOM + createRoot/act (механизм как в members/__tests__/MembersSection.test.ts):
// контролы ui-кита подменены заглушками, которые запоминают свои props, api — мок; тест
// «нажимает» их напрямую. ProjectActionsMenu и SharedBadge НЕ мокаются: вид меню
// (участник/владелец) и текст метки — ровно то, что проверяется.
import { createElement } from 'react';
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Project } from '../../../types';

// fake-DOM поднимается ДО импорта React-кода (createRoot читает document)
const { _doc, FakeNode, reg } = vi.hoisted(() => {
  class _FakeNode {
    nodeType = 1;
    nodeName = 'div';
    childNodes: _FakeNode[] = [];
    parentElement: _FakeNode | null = null;
    _attrs: Record<string, string> = {};
    // dataset — для themeMode: document.documentElement.dataset.theme на уровне модуля
    // (тот же приём, что в components/FilterBar.test.ts)
    dataset: Record<string, string> = {};
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
    trigger: null as any,
    items: {} as Record<string, any>,
    confirms: {} as Record<string, any>,
  };
  return { _doc, FakeNode: _FakeNode as typeof _FakeNode, reg };
});

const api = vi.hoisted(() => ({ leave: vi.fn() }));
vi.mock('../../../lib/api', () => ({ api: { projects: { members: api } } }));
vi.mock('../../../lib/defaultPersona', () => ({ useMe: () => ({ userId: 'u-me' }) }));
vi.mock('../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => false };
});
vi.mock('../../../components/ui', async () => {
  const { createElement: h, Fragment: F } = await import('react');
  return {
    IconButton: (p: any) => { reg.trigger = p; return h('button'); },
    Menu: (p: any) => h('menu', null, h(F, null, p.children)),
    MenuItem: (p: any) => { reg.items[p.label] = p; return h('item'); },
    ConfirmDialog: (p: any) => { reg.confirms[p.title] = p; return h('confirm', { 'data-title': p.title }); },
  };
});

import { ProjectRow } from '../ProjectRow';
import { ProjectCard } from '../ProjectCard';

type FN = InstanceType<typeof FakeNode>;

const meId = 'u-me';
// Чужой проект: myRole авторитетна — взгляд участника; имя владельца — из данных (для метки)
const foreign = {
  id: 'p-foreign', name: 'Работа', rootPath: '/r/work', updatedAt: '2026-10-20T00:00:00Z',
  ownerId: 'u-owner', ownerDisplayName: 'Мария', ownerUsername: 'maria',
  myRole: 'editor', members: { count: 2, first: [] },
} as Project;
// Мой общий проект с участниками: метка «общий», меню — владельца
const ownShared = {
  id: 'p-own', name: 'Сад', rootPath: '/r/garden', updatedAt: '2026-10-21T00:00:00Z',
  ownerId: meId, myRole: 'owner', members: { count: 1, first: [] },
} as Project;
// Обычный проект: ни метки, ни «Покинуть проект»
const plain = {
  id: 'p-plain', name: 'Дача', rootPath: '/r/cottage', updatedAt: '2026-10-22T00:00:00Z',
  ownerId: meId,
} as Project;

let container: FN;

async function mountRow(project: Project, extra: Record<string, unknown> = {}) {
  container = new FakeNode();
  const root = createRoot(container);
  await act(async () => {
    root.render(createElement(ProjectRow, {
      project, index: 0, online: true, onOpen: () => {}, onMove: () => {}, onEdit: () => {}, onDelete: () => {}, ...extra,
    }));
  });
}
async function mountCard(project: Project, extra: Record<string, unknown> = {}) {
  container = new FakeNode();
  const root = createRoot(container);
  await act(async () => {
    root.render(createElement(ProjectCard, {
      project, index: 0, online: true, onOpen: () => {}, onMove: () => {}, onEdit: () => {}, onDelete: () => {}, ...extra,
    }));
  });
}

// Открыть меню «⋯»: rect кнопки — заглушка, как в реальном клике
const openMenu = () => act(async () => {
  reg.trigger.onClick({ currentTarget: { getBoundingClientRect: () => ({ top: 0, left: 0, right: 12, bottom: 12, width: 12, height: 12, x: 0, y: 0, toJSON() { return {}; } }) } });
});

function hasNode(n: FN, tag: string, title?: string): boolean {
  if (n.nodeName === tag && (title === undefined || n._attrs['data-title'] === title)) return true;
  return n.childNodes.some(c => hasNode(c, tag, title));
}
// Текст ищется и на самом элементе: React 19 текст-only детей кладёт прямо в
// textContent элемента (setTextContent в host config), текстовых узлов не создаёт.
function textIn(n: FN, s: string): boolean {
  if (n.textContent.includes(s)) return true;
  return n.childNodes.some(c => textIn(c, s));
}

beforeEach(() => {
  vi.clearAllMocks();
  reg.trigger = null; reg.items = {}; reg.confirms = {};
});

describe('S1-F7: метка общего проекта в строке и карточке (карта п.6)', () => {
  it('чужой проект: метка «проект Мария» в ProjectRow и в ProjectCard', async () => {
    await mountRow(foreign);
    expect(textIn(container, 'проект Мария'), 'метки нет в строке').toBe(true);
    await mountCard(foreign);
    expect(textIn(container, 'проект Мария'), 'метки нет в карточке').toBe(true);
  });

  it('мой общий проект с участниками: метка «общий»', async () => {
    await mountRow(ownShared);
    expect(textIn(container, 'общий'), 'метки «общий» нет').toBe(true);
  });

  it('обычный проект: метки нет, вёрстка прежняя', async () => {
    await mountRow(plain);
    expect(textIn(container, 'общий'), 'у обычного проекта появилась метка').toBe(false);
    expect(textIn(container, 'проект'), 'у обычного проекта появилась метка владельца').toBe(false);
    await openMenu();
    expect(reg.items['Удалить'], 'меню владельца изменилось').toBeTruthy();
    expect(reg.items['Редактировать'], 'меню владельца изменилось').toBeTruthy();
  });
});

describe('S1-F7: «Покинуть проект» в меню действий (карта п.7)', () => {
  it('ProjectRow: участник → подтверждение → leave с id проекта → список обновлён', async () => {
    api.leave.mockResolvedValue(undefined);
    const onLeft = vi.fn();
    await mountRow(foreign, { onLeft });
    await openMenu();
    expect(reg.items['Покинуть проект'], 'пункта для участника нет').toBeTruthy();
    await act(async () => { reg.items['Покинуть проект'].onClick(); });
    expect(hasNode(container, 'confirm', 'Покинуть проект?'), 'нет подтверждения выхода').toBe(true);
    expect(api.leave, 'ушли до подтверждения').not.toHaveBeenCalled();
    await act(async () => { await reg.confirms['Покинуть проект?'].onConfirm(); });
    expect(api.leave, 'leave не вызван с id проекта').toHaveBeenCalledWith('p-foreign');
    expect(onLeft, 'список не обновлён (onLeft не позван)').toHaveBeenCalledWith(foreign);
    expect(hasNode(container, 'confirm', 'Покинуть проект?'), 'подтверждение не закрылось').toBe(false);
  });

  it('ProjectCard: та же цепочка', async () => {
    api.leave.mockResolvedValue(undefined);
    const onLeft = vi.fn();
    await mountCard(foreign, { onLeft });
    await openMenu();
    expect(reg.items['Покинуть проект'], 'пункта для участника нет').toBeTruthy();
    await act(async () => { reg.items['Покинуть проект'].onClick(); });
    expect(hasNode(container, 'confirm', 'Покинуть проект?'), 'нет подтверждения выхода').toBe(true);
    expect(api.leave, 'ушли до подтверждения').not.toHaveBeenCalled();
    await act(async () => { await reg.confirms['Покинуть проект?'].onConfirm(); });
    expect(api.leave, 'leave не вызван с id проекта').toHaveBeenCalledWith('p-foreign');
    expect(onLeft, 'список не обновлён (onLeft не позван)').toHaveBeenCalledWith(foreign);
  });

  it('владелец: пункта «Покинуть проект» нет — ни в обычном, ни в своём общем проекте', async () => {
    for (const p of [plain, ownShared]) {
      const onLeft = vi.fn();
      await mountRow(p, { onLeft });
      await openMenu();
      expect(reg.items['Покинуть проект'], `пункт появился у владельца (${p.id})`).toBeUndefined();
      expect(api.leave, `leave вызван у владельца (${p.id})`).not.toHaveBeenCalled();
    }
  });
});
