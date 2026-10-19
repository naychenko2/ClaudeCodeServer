// Кламп groupBy в SessionList (Минор 1 по R4): при выключенном флаге features-overview
// сохранённый в localStorage groupBy='features' не должен уводить список в режим фич
// (иначе — EmptyState «Фич пока нет» без пункта «Фичи» в тулбаре). Клампим к 'days',
// как 'subjects' в глобальном списке (ChatList.tsx); при включённом флаге значение
// из хранилища снова работает.
//
// Окружение node (без jsdom): renderToStaticMarkup (SSR) — effects не идут, поэтому
// sessions остаются пустыми; различие режимов ловим по строке «Фич пока нет», которая
// есть ТОЛЬКО в режиме фич (и зависит от индекса фич, а не от sessions).

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SessionList } from './SessionList';
import { setAllFlags } from '../lib/featureFlags';
import { __test as featuresIndexTest } from '../lib/featuresIndex';
import type { Project } from '../types';

// useSubjectRun — useSyncExternalStore БЕЗ getServerSnapshot: legacy-SSR-рендерер
// (renderToStaticMarkup) его роняет. В моих кейсах subjectsMode всегда false, поэтому
// значение хука не потребляется — достаточно заглушки (остальные экспорты сохраняем).
vi.mock('../lib/chatSubjects', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/chatSubjects')>();
  return { ...orig, useSubjectRun: () => null as never };
});

// signalr: подписки no-op — без createRoot (effects) соединение не открывается, а с ним
// HttpConnection роняет resolveUrl('/hubs/session') без полного window.location
vi.mock('../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/signalr')>();
  const noop = () => () => {};
  return { ...orig, onMessage: noop, onReconnected: noop, onGitStatusChanged: noop, onFilesChanged: noop, joinUser: noop };
});

// Окружение node — localStorage нет; минимальная реализация на Map (как в chatFilters.test.ts)
const store = new Map<string, string>();
globalThis.localStorage = {
  getItem: (k: string) => store.get(k) ?? null,
  setItem: (k: string, v: string) => { store.set(k, String(v)); },
  removeItem: (k: string) => { store.delete(k); },
  clear: () => store.clear(),
  key: () => null,
  get length() { return store.size; },
} as Storage;
// sessionStorage — как localStorage (git.ts:joinUserGroup читает/пишет в него)
const sstore = new Map<string, string>();
(globalThis as any).sessionStorage = {
  getItem: (k: string) => sstore.get(k) ?? null,
  setItem: (k: string, v: string) => { sstore.set(k, String(v)); },
  removeItem: (k: string) => { sstore.delete(k); },
  clear: () => sstore.clear(),
  key: () => null,
  get length() { return sstore.size; },
} as Storage;

// Минимальный проект: SessionList в рендере читает id, tagRegistry, subjectRegistry,
// subjectsSweepSince (остальные поля эффектов — в SSR не идут).
const project: Project = {
  id: 'p1', name: 'Проект', rootPath: '/p', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
  tagRegistry: [], subjectRegistry: [], subjectsSweepSince: null,
} as Project;

// Рендер списка: флаг features-overview + groupBy, сохранённый в хранилище.
// Индекс фич сбрасываем в EMPTY — в режиме фич это даёт «Фич пока нет».
function renderList(featuresOn: boolean, storedGroupBy: string): string {
  store.clear();
  setAllFlags({ 'features-overview': featuresOn });
  featuresIndexTest.reset();
  store.set('cc_chat_filters:p1', JSON.stringify({ groupBy: storedGroupBy, sortOrder: 'newest' }));
  return renderToStaticMarkup(
    createElement(SessionList, { project, activeSession: null, onSelect: () => {} }),
  );
}

beforeEach(() => {
  store.clear();
  setAllFlags({});
  featuresIndexTest.reset();
});

describe('SessionList: кламп groupBy при выключенном флаге (Минор 1)', () => {
  it('флаг выключен + groupBy=features в хранилище → режим «Дни», «Фич пока нет» нет', () => {
    const html = renderList(false, 'features');
    // Режим фичи не включился: её пустого состояния нет
    expect(html).not.toContain('Фич пока нет');
  });

  it('флаг включён + groupBy=features → режим фич («Фич пока нет» при пустом индексе)', () => {
    const html = renderList(true, 'features');
    // Режим фичи включился: пустой индекс → её пустое состояние
    expect(html).toContain('Фич пока нет');
  });

  it('флаг выключен + groupBy=days (обычное значение) → режим «Дни», без «Фич пока нет»', () => {
    const html = renderList(false, 'days');
    expect(html).not.toContain('Фич пока нет');
  });
});

// M1′: «Фич пока нет» — по содержимому (inline), не height:100%.
// До фикса EmptyState тянулся на всю высоту панели, а под ним (в «Без фичи»)
// стояли обычные чаты. inline — контейнер по содержимому.
describe('SessionList: M1′ «Фич пока нет» по содержимому', () => {
  it('пустой индекс: «Фич пока нет» без height:100% (inline)', () => {
    const html = renderList(true, 'features');
    expect(html).toContain('Фич пока нет');
    expect(html).not.toContain('height:100%');
  });
});

// n3: число «Закрытые» — из нарисованных групп (единый источник для чипа и секции),
// не «все закрытые фичи индекса». В SSR sessions пусты → нарисованных групп 0,
// даже если в индексе есть закрытые фичи: чип и секция совпадают (0), а не 0 vs N.
describe('SessionList: n3 число «Закрытые» из нарисованных групп', () => {
  const closedFeat = (id: string) => ({
    featureId: id, title: `Закрыта ${id}`, projectId: 'p1', bucket: 'closed' as const,
    attention: null, rail: [], chips: ['closed' as const],
    stepsAccepted: 0, stepsTotal: 0, loops: 0, now0: null, lastActivityAt: null, summary: null,
    keySessionId: null, keyArchived: false, keyProjectId: null, chatCount: 0,
  });

  it('2 закрытые фичи в индексе, sessions пусты → чип «Закрытые» = 0 (нарисованные группы)', () => {
    setAllFlags({ 'features-overview': true });
    featuresIndexTest.reset();
    featuresIndexTest.setIndex('p1', { features: [closedFeat('c1'), closedFeat('c2')], chats: {} });
    store.clear();
    store.set('cc_chat_filters:p1', JSON.stringify({ groupBy: 'features', sortOrder: 'newest' }));
    const html = renderToStaticMarkup(createElement(SessionList, { project, activeSession: null, onSelect: () => {} }));
    // Первый «Закрытые» в разметке — чип (строка чипов идёт первой); его число = 0
    const i = html.indexOf('>Закрытые</span>');
    expect(i).toBeGreaterThan(-1);
    const m = html.slice(i).match(/<span[^>]*>(\d+)<\/span>/);
    expect(m?.[1]).toBe('0');
  });
});

// === M4: закрытые фичи раскрываются (по featureId через expandedFeatureGroups) ===
// До фикса закрытая фича была статичной строкой (expanded={false}, onToggle={() => {}}).
// Теперь — как открытые группы: раскрытие по featureId через общий expandedFeatureGroups,
// дерево buildGroupTree. SSR (renderToStaticMarkup) effects не гоняет → sessions пусты и
// группа закрытой фичи не рисуется; для раскрытия нужен createRoot (effects идут):
// минимальный DOM-шим + mock api.sessions.list.
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import type { Session } from '../types';

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
  appendChild(c: _FakeNode) { c.parentElement = this; this.childNodes.push(c); return c; }
  insertBefore(c: _FakeNode, ref: _FakeNode | null) {
    c.parentElement = this;
    if (ref == null) this.childNodes.push(c);
    else { const i = this.childNodes.indexOf(ref); this.childNodes.splice(i < 0 ? this.childNodes.length : i, 0, c); }
    return c;
  }
  removeChild(c: _FakeNode) { const i = this.childNodes.indexOf(c); if (i >= 0) this.childNodes.splice(i, 1); c.parentElement = null; return c; }
  _listeners: Record<string, ((e: any) => void)[]> = {};
  get ownerDocument() { return _doc; }
  get tagName() { return this.nodeName.toUpperCase(); }
  addEventListener = (type: string, h: (e: any) => void) => { (this._listeners[type] ??= []).push(h); };
  removeEventListener = (type: string, h: (e: any) => void) => { const a = this._listeners[type]; if (a) { const i = a.indexOf(h); if (i >= 0) a.splice(i, 1); } };
  // React 19 вешает слушатели на корневой контейнер: «клик» = вызов их с событием,
  // чей target — кликнутый узел (синтетическая диспетчеризация идёт по target)
  fireClick(target: _FakeNode) {
    const ev: any = { type: 'click', target, currentTarget: this, bubbles: true, cancelable: true,
      preventDefault() {}, stopPropagation() {}, stopImmediatePropagation() {}, nativeEvent: null };
    ev.nativeEvent = ev;
    for (const h of this._listeners['click'] ?? []) h(ev);
  }
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
(globalThis as any).document = _doc;
(globalThis as any).window = {
  document: _doc, defaultView: null,
  HTMLIFrameElement: _FakeIframe, HTMLInputElement: class {}, HTMLSelectElement: class {}, HTMLTextAreaElement: class {},
  addEventListener: () => {}, removeEventListener: () => {}, location: { href: '' }, navigator: { userAgent: 'test' },
  // useHoverWarm (useSession.ts:461) в cleanup зовёт window.clearTimeout — в node его нет
  setTimeout: () => 0, clearTimeout: () => {},
};
_doc.defaultView = (globalThis as any).window;

let mockSessions: Session[] = [];
// useFeaturesIndex (effects) сам зовёт api.tasks.featuresIndex и setIndex-ит результат:
// без мока он падает (нет сервера) → сброс в EMPTY, и группа фичи не рисуется
let mockIndex: import('../types/featureIndex').FeaturesIndex = { features: [], chats: {} };
vi.mock('../lib/api', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/api')>();
  return { ...orig, api: { ...orig.api,
    sessions: { ...orig.api.sessions, list: async () => mockSessions },
    tasks: { ...orig.api.tasks, featuresIndex: async () => mockIndex },
  } };
});

// Сериализованное дерево fake-DOM → строка (для проверок aria-expanded/содержимого)
function _serialize(n: _FakeNode): string {
  let out = n.nodeType === 3 ? n.textContent : `<${n.nodeName} aria-expanded="${n._attrs['aria-expanded'] ?? ''}" data-feature-state="${n._attrs['data-feature-state'] ?? ''}">`;
  for (const c of n.childNodes) out += _serialize(c);
  if (n.nodeType === 1) out += `</${n.nodeName}>`;
  return out;
}

describe('SessionList: M4 закрытые фичи раскрываются', () => {
  const closedFeat = (id: string) => ({
    featureId: id, title: `Закрыта ${id}`, projectId: 'p1', bucket: 'closed' as const,
    attention: null, rail: [], chips: ['closed' as const],
    stepsAccepted: 0, stepsTotal: 0, loops: 0, now0: null, lastActivityAt: null, summary: null,
    keySessionId: null, keyArchived: false, keyProjectId: null, chatCount: 1,
  });
  const sess = (id: string): Session => ({
    id, mode: 'auto', status: 'finished', messageCount: 0,
    createdAt: '2026-10-18T10:00:00Z', updatedAt: '2026-10-18T10:00:00Z',
    origin: 'manual',
  } as Session);

  it('закрытая фича: по умолчанию свёрнута, после toggle — раскрыта (aria-expanded)', async () => {
    setAllFlags({ 'features-overview': true });
    featuresIndexTest.reset();
    const idx = { features: [closedFeat('c1')], chats: { 'c1-chat': [{ featureId: 'c1', kind: 'executor', taskRole: null, stepRef: null, lane: null }] } };
    featuresIndexTest.setIndex('p1', idx);
    mockIndex = idx; // эффект useFeaturesIndex не сбрасывает индекс в EMPTY
    mockSessions = [sess('c1-chat')];
    store.clear();
    // featureFilter:'closed' — секция «Закрытые» раскрывается сама (closedExpanded = chip==='closed'),
    // без клика по кнопке секции: закрытая группа фичи сразу на экране
    store.set('cc_chat_filters:p1', JSON.stringify({ groupBy: 'features', sortOrder: 'newest', featureFilter: 'closed' }));

    const container = new _FakeNode();
    const root = createRoot(container);
    await act(async () => { root.render(createElement(SessionList, { project, activeSession: null, onSelect: () => {} })); });

    // Заголовок группы закрытой фичи (data-testid), а не кнопка секции «Закрытые»:
    // у обоих aria-expanded, но раскрываем именно группу
    const findHeader = (): _FakeNode | null => {
      const walk = (n: _FakeNode): _FakeNode | null => {
        if (n._attrs['data-testid'] === 'feature-group-header') return n;
        for (const c of n.childNodes) { const r = walk(c); if (r) return r; }
        return null;
      };
      return walk(container);
    };
    // По умолчанию свёрнута (aria-expanded="false" на самом заголовке группы)
    let header = findHeader();
    expect(header).not.toBeNull();
    expect(header!._attrs['aria-expanded']).toBe('false');
    // Клик по заголовку: до фикса onToggle был no-op (aria-expanded не менялся)
    await act(async () => { container.fireClick(header!); });
    // После клика — раскрыта: React может пересоздать узел, ищем заново
    header = findHeader();
    expect(header).not.toBeNull();
    expect(header!._attrs['aria-expanded']).toBe('true');
    root.unmount();
  });
});
