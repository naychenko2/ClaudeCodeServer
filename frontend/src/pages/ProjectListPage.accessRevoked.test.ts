// QA-1: страница списка проектов при отзыве доступа.
// 1) Приведённому сюда человеку (open project отозван, App увёл к списку) страница рисует
//    Notice «Доступ к проекту отозван» по пропу accessNotice — с именем владельца, когда
//    сервер его принёс (текст — participantGate, общий с плашкой воркспейса).
// 2) Отзыв другого (не открытого) проекта: строка отозванного уходит из списка молча,
//    без Notice на текущей странице.
// Окружение — node без jsdom, как в participantMode.test: свой fake-DOM, тяжёльные дети —
// recorder-заглушки (проверяются решения списка и текста, не вёрстка карточек).

import { createElement } from 'react';
import type { ReactElement } from 'react';
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AuthState, Project } from '../types';

const { FakeNode, reg, apiMock, sig } = vi.hoisted(() => {
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
    HTMLIFrameElement: class {}, HTMLInputElement: class {}, HTMLSelectElement: class {}, HTMLTextAreaElement: class {},
    addEventListener: () => {}, removeEventListener: () => {},
    location: { href: '' }, navigator: { userAgent: 'test' },
    setTimeout: () => 0, clearTimeout: () => {},
    innerWidth: 420, // мобильная ветка раскладки (одна колонка, ProjectCard)
    matchMedia: (_q: string) => ({ matches: false, media: _q, addEventListener: () => {}, removeEventListener: () => {}, onchange: null, dispatchEvent: () => true }),
  };
  (globalThis as any).document = _doc;
  (globalThis as any).window = _win;
  (globalThis as any).sessionStorage = { getItem: () => null, setItem: () => {}, removeItem: () => {} };
  (globalThis as any).IS_REACT_ACT_ENVIRONMENT = true;
  _doc.defaultView = _win;
  const mk = (id: string): Project => ({
    id, name: `Проект ${id}`, rootPath: `/p/${id}`,
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
    tagRegistry: [], subjectRegistry: [], subjectsSweepSince: null,
  } as Project);
  const apiMock: any = {
    projects: { list: vi.fn(async () => [mk('p1'), mk('p2')]) },
    projectGroups: { list: vi.fn(async () => []) },
    sessions: { list: vi.fn(async () => []) },
  };
  const sig = { handlers: [] as Array<(d: { projectId: string; ownerDisplayName?: string | null }) => void> };
  const reg = {
    cards: [] as string[], // id карточцев последнего прохода рендера
    notices: [] as { tone: string; title: string; children: unknown }[],
  };
  return { FakeNode: _FakeNode as typeof _FakeNode, reg, apiMock, sig };
});

vi.mock('../lib/api', () => ({ api: apiMock }));
vi.mock('../hooks/useOnline', () => ({ __esModule: true, useOnline: () => true }));
vi.mock('../lib/offline', () => ({ __esModule: true, OfflineError: class OfflineError extends Error {} }));
vi.mock('../lib/featureFlags', () => ({ __esModule: true, FLAGS: { spheres: 'spheres' }, useFeature: () => false }));
// Подписка на личное событие — ловим handler, чтобы «прислать» его из теста
vi.mock('../lib/signalr', () => ({
  __esModule: true,
  onProjectAccessRevoked: (h: (d: { projectId: string; ownerDisplayName?: string | null }) => void) => {
    sig.handlers.push(h);
    return () => {};
  },
}));
vi.mock('../features/projects/useAllProjects', () => ({ __esModule: true, onProjectIconBackfilled: () => () => {} }));

// ui-кит: Notice — recorder (проверяется решение страницы: тон и текст из participantGate),
// остальное — заглушки; PageCanvas — прозрачная обёртка, дети рендерятся
const { makeNull, uiMock } = vi.hoisted(() => {
  const { createElement: h } = require('react');
  const makeNull = (name: string) => ({ __esModule: true, [name]: () => null });
  const uiMock: any = {
    __esModule: true,
    Button: () => null,
    EmptyState: () => null,
    IslandScaffold: (p: any) => h('div', null, p.left, p.right, p.center),
    Notice: (p: any) => {
      reg.notices.push({ tone: p.tone ?? 'warning', title: String(p.title ?? ''), children: p.children });
      return h('div', { 'data-testid': p['data-testid'] }, p.title, p.children);
    },
  };
  return { makeNull, uiMock };
});
vi.mock('../components/ui', () => uiMock);
vi.mock('../components/ui/PageCanvas', async () => {
  const { createElement: h } = await import('react');
  return { __esModule: true, PageCanvas: (p: any) => h('div', null, p.children) };
});
vi.mock('../components/Toolbar', () => ({ __esModule: true, PillSwitch: () => null }));
vi.mock('../components/HubHeader', () => ({ __esModule: true, HubHeader: () => null }));
// Карточки/строки — recorder id: по ним видно, что осталось в списке после события
const { cardMock } = vi.hoisted(() => {
  const rec = (name: string) => ({
    __esModule: true,
    [name]: (p: any) => { reg.cards.push(p.project.id); return createElement('div', { 'data-card': p.project.id }); },
  });
  return { cardMock: { row: rec('ProjectRow'), card: rec('ProjectCard') } };
});
vi.mock('../features/projects/ProjectCard', () => cardMock.card);
vi.mock('../features/projects/ProjectRow', () => cardMock.row);
vi.mock('../features/projects/GroupHeader', () => makeNull('GroupHeader'));
vi.mock('../features/projects/ProjectSidebar', () => makeNull('ProjectSidebar'));
vi.mock('../features/projects/dialogs/AddProjectDialog', () => makeNull('AddProjectDialog'));
vi.mock('../features/projects/dialogs/EditDialog', () => makeNull('EditDialog'));
vi.mock('../features/projects/dialogs/DeleteDialog', () => makeNull('DeleteDialog'));
vi.mock('../features/projects/dialogs/MoveToGroupDialog', () => makeNull('MoveToGroupDialog'));
vi.mock('../features/projects/dialogs/GroupManagerDialog', () => makeNull('GroupManagerDialog'));
vi.mock('../features/spheres/SphereHeader', () => makeNull('SphereHeader'));
vi.mock('../features/spheres/SphereDialog', () => makeNull('SphereDialog'));
vi.mock('../features/spheres/SphereDeleteDialog', () => makeNull('SphereDeleteDialog'));
vi.mock('../features/spheres/RemoveFromSphereDialog', () => makeNull('RemoveFromSphereDialog'));
vi.mock('../features/spheres/SpherePage', () => makeNull('SpherePage'));
// Панельные зоны в мобильной ветке не рендерятся, но модули тянутся импортом страницы
vi.mock('./workspace/PanelZone', () => ({ __esModule: true, PanelZone: () => null }));
vi.mock('./workspace/panelStackState', () => ({ __esModule: true, projectsPanels: {} }));
vi.mock('./workspace/panelCatalog', () => ({ __esModule: true, PROJECTS_KEYS: [] }));

import { ProjectListPage } from './ProjectListPage';
import { ACCESS_REVOKED_TITLE } from '../features/projects/members/participantGate';

type FN = InstanceType<typeof FakeNode>;
const auth = { serverUrl: 'http://x', token: 't', username: 'u' } as AuthState;

async function mount(el: ReactElement): Promise<FN> {
  const container = new FakeNode();
  const root = createRoot(container);
  await act(async () => { root.render(el); });
  return container;
}
// Сколько узлов с этим data-testid реально в дереве (reg.notices накапливает каждый
// проход рендера — считать плашку надо по DOM, а не по журналу)
function countByAttr(n: FN, attr: string, value: string): number {
  let c = n.getAttribute(attr) === value ? 1 : 0;
  for (const ch of n.childNodes) c += countByAttr(ch, attr, value);
  return c;
}
// Дождать загрузки списка: цепочка api.* — микротask'и; один макротask их гарантированно
// дренирует до конца (setProjects/setActiveSessions отработают)
async function flush() { await act(async () => { await new Promise(r => setTimeout(r, 0)); }); }

beforeEach(() => {
  reg.cards.length = 0;
  reg.notices.length = 0;
  sig.handlers.length = 0;
});

describe('ProjectListPage: отзыв доступа (QA-1)', () => {
  it('приведённому с открытого проекта — Notice «Доступ к проекту отозван» с именем владельца', async () => {
    const container = await mount(createElement(ProjectListPage, {
      onOpen: vi.fn(), onLogout: vi.fn(), auth, onHubTab: vi.fn(),
      accessNotice: { projectId: 'p1', ownerDisplayName: 'admin' },
    }));
    await flush();
    // Список на месте
    expect(reg.cards).toEqual(expect.arrayContaining(['p1', 'p2']));
    // Плашка в дереве ровно одна (перерисовки списка не плодят второй баннер)
    expect(countByAttr(container, 'data-testid', 'access-revoked-banner'), 'плашек не одна/нет').toBe(1);
    // warning, базовый текст + имя владельца
    expect(reg.notices.length, 'Notice не отрисован').toBeGreaterThan(0);
    expect(reg.notices[0].tone).toBe('warning');
    expect(reg.notices[0].title).toBe(ACCESS_REVOKED_TITLE);
    expect(reg.notices[0].title).toContain('Доступ к проекту отозван');
    expect(String(reg.notices[0].children)).toContain('admin');
  });

  it('без имени владельца — только базовый текст', async () => {
    const container = await mount(createElement(ProjectListPage, {
      onOpen: vi.fn(), onLogout: vi.fn(), auth, onHubTab: vi.fn(),
      accessNotice: { projectId: 'p1', ownerDisplayName: null },
    }));
    await flush();
    expect(countByAttr(container, 'data-testid', 'access-revoked-banner'), 'плашек не одна/нет').toBe(1);
    expect(reg.notices.length).toBeGreaterThan(0);
    expect(reg.notices[0].title).toBe(ACCESS_REVOKED_TITLE);
    expect(reg.notices[0].children ?? null).toBeNull();
  });

  it('отзыв другого (не открытого) проекта: строка уходит из списка, Notice нет', async () => {
    await mount(createElement(ProjectListPage, { onOpen: vi.fn(), onLogout: vi.fn(), auth, onHubTab: vi.fn() }));
    await flush();
    expect(reg.cards).toEqual(expect.arrayContaining(['p1', 'p2']));

    reg.cards.length = 0; // интересен последний проход рендера после события
    await act(async () => { sig.handlers.forEach(h => h({ projectId: 'p2', ownerDisplayName: 'admin' })); });

    expect(reg.cards, 'p1 должен остаться').toContain('p1');
    expect(reg.cards, 'отозванный проект остался в списке').not.toContain('p2');
    expect(reg.notices, 'Notice про чужой проект').toHaveLength(0);
  });

  // Чужой id: состояние списка не меняется → React не перерисовывает ни одной карточки
  // (мутация «убрать гейт prev.some» красит этот тест: filter вернул бы новый массив)
  it('несуществующий id список не трогает', async () => {
    await mount(createElement(ProjectListPage, { onOpen: vi.fn(), onLogout: vi.fn(), auth, onHubTab: vi.fn() }));
    await flush();
    reg.cards.length = 0;
    await act(async () => { sig.handlers.forEach(h => h({ projectId: 'нет-такого' })); });
    expect(reg.cards, 'список перерисовался без изменения состава').toHaveLength(0);
    expect(reg.notices).toHaveLength(0);
  });
});
