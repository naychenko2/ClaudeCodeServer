// Встройка метки общего проекта в реальные места и меню участника (S1-F5;
// карта shared-projects-v2-proposal.md п.6–7).
//
// Механизм — fake-DOM + createRoot/act с заглушками контролов (как в
// members/__tests__/MembersSection.test.ts): проверяются решения встраивания —
// бейдж рядом с именем в строке виджета «Проекты» и в строке палитры, состав
// меню «⋯» по роли — а не вёрстка ui-кита. SharedBadge и Badge не заглушены:
// их присутствие в строке и есть предмет теста (вид самой метки — SharedBadge.test.ts).
import { createElement, Fragment } from 'react';
import type { ReactElement } from 'react';
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Project } from '../../../types';

// fake-DOM поднимается ДО импорта React-кода (createRoot читает document)
const { _doc, FakeNode, reg, projectsList } = vi.hoisted(() => {
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
  // Реестр: последний рендер контрола-триггера и пунктов меню (по label)
  const reg = {
    iconBtn: null as any,
    items: {} as Record<string, any>,
  };
  // Список проектов для палитры (useAllProjects) — подменяемый, чтобы один мок
  // служил и виджету (через api), и палитре (через хук)
  const projectsList = { value: [] as Project[] };
  return { _doc, FakeNode: _FakeNode as typeof _FakeNode, reg, projectsList };
});

const api = vi.hoisted(() => ({
  list: vi.fn(), groupsList: vi.fn(),
}));
vi.mock('../../../lib/api', () => ({ api: { projects: { list: api.list }, projectGroups: { list: api.groupsList } } }));
vi.mock('../../../lib/defaultPersona', () => ({ useMe: () => ({ userId: 'me-id' }) }));
vi.mock('../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => false };
});
vi.mock('../../../lib/pinnedProjects', () => ({
  usePinnedIds: () => [], useRecentIds: () => [], isPinned: () => false, togglePin: vi.fn(),
}));
vi.mock('../../../lib/listAutoFocus', () => ({ useListAutoFocus: () => false }));
vi.mock('../../../lib/featureFlags', () => ({ FLAGS: { spheres: 'spheres' }, useFeature: () => true }));
vi.mock('../../../lib/userScrollGate', () => ({ useUserScrollGate: () => () => false }));
// Палитра рендерится порталом в body — в тесте остаётся в своём контейнере
vi.mock('react-dom', () => ({ createPortal: (node: any) => node }));
vi.mock('../useAllProjects', () => ({
  useAllProjects: () => projectsList.value,
  openProjectViaEvent: vi.fn(), openAllProjects: vi.fn(), openNewProjectFlow: vi.fn(),
}));
vi.mock('../ProjectIcon', () => ({ ProjectIcon: () => createElement('span', { 'data-icon': true }) }));
vi.mock('../../home/WidgetCard', () => ({
  WidgetCard: (p: any) => createElement('div', { 'data-widget': p.title }, p.children),
  WidgetAction: () => createElement('span'),
  WidgetEmpty: (p: any) => createElement('span', null, p.text),
  relTime: () => '5 мин назад',
}));
vi.mock('../dialogs/AddProjectDialog', () => ({ AddProjectDialog: () => null }));
vi.mock('../../../components/ui', async () => {
  const { createElement: h, Fragment: F } = await import('react');
  return {
    IconButton: (p: any) => { reg.iconBtn = p; return h('button'); },
    Menu: (p: any) => h('menu', null, h(F, null, p.children)),
    MenuItem: (p: any) => { reg.items[p.label] = p; return h('item'); },
  };
});

import { ProjectActionsMenu } from '../ProjectActionsMenu';
import { ProjectPalette } from '../ProjectPalette';
import { ProjectsWidget } from '../../home/ProjectsWidget';

type FN = InstanceType<typeof FakeNode>;

// Данные: свой общий (есть участники), чужой (владелец другой, есть имя владельца),
// приватный (ни участников, ни чужого владельца) — на нём проверяем «верстки нет»
// members — сводка той формы, что отдаёт бэк (ProjectMembersView.Summary): count + первые трое
const ownShared = { id: 'p-own', name: 'Свой', rootPath: '/a', ownerId: 'me-id', updatedAt: '2026-10-03T00:00:00Z', members: { count: 1, first: [{ userId: 'u-other', username: 'u-other' }] } } as Project;
const foreign = { id: 'p-foreign', name: 'Чужой', rootPath: '/b', ownerId: 'andrey', updatedAt: '2026-10-02T00:00:00Z', ownerDisplayName: 'Андрей' } as Project;
const plain = { id: 'p-plain', name: 'Приватный', rootPath: '/c', ownerId: 'me-id', updatedAt: '2026-10-01T00:00:00Z' } as Project;

let container: FN;

async function mount(el: ReactElement) {
  container = new FakeNode();
  const root = createRoot(container);
  await act(async () => { root.render(el); });
}

// Весь видимый текст смонтированного дерева. Отдельные текстовые узлы — только там,
// где у элемента есть и элементы-дети: для элемента с одним текстом React применяет
// shouldSetTextContent и пишет текст прямо в textContent элемента (без TextNode) —
// такие листья читаем отсюда.
function texts(n: FN, acc: string[] = []): string[] {
  if (n.nodeType === 3) { acc.push(n.textContent); return acc; }
  if (n.childNodes.length === 0 && n.textContent) acc.push(n.textContent);
  for (const c of n.childNodes) texts(c, acc);
  return acc;
}
const count = (all: string[], t: string) => all.filter(x => x === t).length;

// Открыть меню «⋯»: триггер помнит свой onClick — нажимаем его напрямую
async function openMenu() {
  await act(async () => {
    reg.iconBtn.onClick({ currentTarget: { getBoundingClientRect: () => ({ x: 0, y: 0, width: 0, height: 0, top: 0, left: 0, right: 0, bottom: 0 }) } });
  });
}

beforeEach(() => {
  vi.clearAllMocks();
  reg.iconBtn = null;
  reg.items = {};
  projectsList.value = [ownShared, foreign, plain];
  api.list.mockResolvedValue(projectsList.value);
  api.groupsList.mockResolvedValue([]);
});

describe('виджет «Проекты» на «Домой»: метка общего проекта', () => {
  it('свой общий — «общий», чужой — «проект Андрея», приватный — без метки', async () => {
    await mount(createElement(ProjectsWidget, { onOpenProject: () => {} }));
    const all = texts(container);
    expect(count(all, 'Свой')).toBe(1);
    expect(count(all, 'общий')).toBe(1);
    expect(count(all, 'проект Андрей')).toBe(1);
    expect(count(all, 'Приватный')).toBe(1);
    // ни лишней плашки, ни заглушки «чужой проект»
    expect(count(all, 'общий') + count(all, 'проект Андрей') + count(all, 'чужой проект')).toBe(2);
  });
});

describe('палитра проектов: метка общего проекта', () => {
  it('метки в строках общего и чужого, приватный — без метки', async () => {
    await mount(createElement(ProjectPalette, { onClose: () => {} }));
    const all = texts(container);
    expect(count(all, 'общий')).toBe(1);
    expect(count(all, 'проект Андрей')).toBe(1);
    expect(count(all, 'чужой проект')).toBe(0);
  });
});

describe('меню «⋯» проекта: взгляд участника (карта п.7)', () => {
  const menuProps = {
    onMove: () => {}, onEdit: () => {}, onDelete: () => {}, onRemoveFromSphere: () => {}, onLeave: () => {},
  };

  it('участник (myRole editor): «Закрепить · Открыть настройки · Покинуть проект», скрыты сфера/удаление', async () => {
    const p = { ...foreign, myRole: 'editor' as const, groupId: 'g1' };
    await mount(createElement(ProjectActionsMenu, { project: p, ...menuProps }));
    await openMenu();
    expect(Object.keys(reg.items)).toEqual(['Закрепить', 'Открыть настройки', 'Покинуть проект']);
    expect(reg.items['Покинуть проект'].danger).toBe(true);
    expect('Удалить' in reg.items).toBe(false);
    expect('Перенести в сферу…' in reg.items).toBe(false);
    expect('Убрать из сферы' in reg.items).toBe(false);
    expect('Редактировать' in reg.items).toBe(false);
  });

  it('участник без myRole (старый бэк): тот же состав по ownerId', async () => {
    await mount(createElement(ProjectActionsMenu, { project: foreign, ...menuProps }));
    await openMenu();
    expect(Object.keys(reg.items)).toEqual(['Закрепить', 'Открыть настройки', 'Покинуть проект']);
  });

  it('вызывающий не задал onLeave — «Покинуть проект» нет, но и «Удалить» нет', async () => {
    const { onLeave: _drop, ...rest } = menuProps;
    await mount(createElement(ProjectActionsMenu, { project: foreign, ...rest }));
    await openMenu();
    expect(Object.keys(reg.items)).toEqual(['Закрепить', 'Открыть настройки']);
  });

  it('владелец: обычное меню, «Покинуть проект» и «Открыть настройки» нет', async () => {
    const p = { ...ownShared, groupId: 'g1' };
    await mount(createElement(ProjectActionsMenu, { project: p, ...menuProps }));
    await openMenu();
    expect(Object.keys(reg.items)).toEqual(['Закрепить', 'Перенести в сферу…', 'Убрать из сферы', 'Редактировать', 'Удалить']);
    expect('Покинуть проект' in reg.items).toBe(false);
    expect('Открыть настройки' in reg.items).toBe(false);
  });
});
