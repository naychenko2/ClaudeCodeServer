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
import { ALL_FEATURE_KINDS, type FeatureKind } from '../lib/chatFilters';
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

// n3 (волна 2, «Показывать закрытые фичи · N» в тумблере FilterBar) — тест снят в Ф-2.2:
// тумблера больше нет (Ф-2.1), его место заняла ось featureKinds. Число «Закрытые»
// проверяется тестом заголовка секции (data-closed-count) в блоке Ф-2.2 ниже.

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
  // Рамка для позиционирования поповера FilterBar (toggleOpen → getBoundingClientRect)
  getBoundingClientRect() { return { top: 0, left: 0, right: 100, bottom: 30, width: 100, height: 30, x: 0, y: 0, toJSON() { return {}; } }; }
  // Поповер FilterBar фокусирует поле поиска (useListAutoFocus) — focus/blur на узле
  focus() {}
  blur() {}
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
  // useIsTouch (breakpoints.ts) — matchMedia при открытии поповера FilterBar (useListAutoFocus)
  matchMedia: (q: string) => ({ matches: false, media: q, addEventListener: () => {}, removeEventListener: () => {}, onchange: null, dispatchEvent: () => true }),
};
_doc.defaultView = (globalThis as any).window;

let mockSessions: Session[] = [];
// useFeaturesIndex (effects) сам зовёт api.tasks.featuresIndex и setIndex-ит результат:
// без мока он падает (нет сервера) → сброс в EMPTY, и группа фичи не рисуется
let mockIndex: import('../types/featureIndex').FeaturesIndex = { features: [], chats: {} };
// Счётчик запросов индекса: «один запрос на проект» — секция FilterBar фич не
// должна плодить второй fetch (индекс приходит из SessionList, не из FilterBar)
let indexCalls = 0;
vi.mock('../lib/api', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/api')>();
  return { ...orig, api: { ...orig.api,
    sessions: { ...orig.api.sessions, list: async () => mockSessions },
    tasks: { ...orig.api.tasks, featuresIndex: async () => { indexCalls++; return mockIndex; } },
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
    kind: 'closed' as const, attention: null, rail: [], chips: ['closed' as const],
    stepsAccepted: 0, stepsTotal: 0, loops: 0, now0: null, lastActivityAt: null, summary: null,
    keySessionId: null, keyArchived: false, keyProjectId: null, chatCount: 1,
  });
  const sess = (id: string): Session => ({
    id, projectId: 'p1', mode: 'auto', status: 'finished', messageCount: 0,
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
    // Ф-2.2: «Закрытые» на оси featureKinds — секция закрытых на экране без лишнего клика
    store.set('cc_chat_filters:p1', JSON.stringify({ groupBy: 'features', sortOrder: 'newest', featureKinds: ['closed'] }));

    const container = new _FakeNode();
    const root = createRoot(container);
    await act(async () => { root.render(createElement(SessionList, { project, activeSession: null, onSelect: () => {} })); });

    // Раскрытие группы — кнопка-шеврон (data-testid="feature-group-chevron"): после Ф1.3
    // data-testid="feature-group-header" — обёртка div (без aria-expanded/onClick), а
    // aria-expanded и клик живут на шевроне. Ищем шеврон, а не обёртку.
    const findChevron = (): _FakeNode | null => {
      const walk = (n: _FakeNode): _FakeNode | null => {
        if (n._attrs['data-testid'] === 'feature-group-chevron') return n;
        for (const c of n.childNodes) { const r = walk(c); if (r) return r; }
        return null;
      };
      return walk(container);
    };
    // По умолчанию свёрнута (aria-expanded="false" на шевроне)
    let chevron = findChevron();
    expect(chevron).not.toBeNull();
    expect(chevron!._attrs['aria-expanded']).toBe('false');
    // Клик по шеврону: до фикса onToggle был no-op (aria-expanded не менялся)
    await act(async () => { container.fireClick(chevron!); });
    // После клика — раскрыта: React может пересоздать узел, ищем заново
    chevron = findChevron();
    expect(chevron).not.toBeNull();
    expect(chevron!._attrs['aria-expanded']).toBe('true');
    root.unmount();
  });
});

// === Ф-2.2: режим «Фичи» по оси featureKinds (отбор, «Показаны», пустые состояния) ===
// Отбор — только featureMatchesKinds по полю kind (Ф-1.3/Б-1.2): bucket в пути фильтра
// не читается. Мультивыбор оси (OR); крестик строки «Показаны» убирает значение с оси
// ('remove') или возвращает снятое ('restore', вариант «кроме»); «Закрытые» — секцией,
// когда выбраны на оси; пустые состояния: «Фич пока нет» / calm «Открытых фич нет» +
// «Показать закрытые · N» / «Под фильтр ничего не попало». Один запрос индекса на
// проект. Индекс и sessions приходят из SessionList (useFeaturesIndex) — FilterBar
// только показывает; интерактив — createRoot + act (effects идут), текст из fake-DOM.
describe('SessionList: Ф-2.2 режим фич по оси featureKinds', () => {
  // kind — единственный признак состояния (Б-1.2: row.kind == brief.kind). bucket
  // в фикстуре согласован с ним (тот же закон бэка), но тесты читают только kind.
  const feat = (id: string, kind: FeatureKind, title: string) => ({
    featureId: id, title, projectId: 'p1',
    bucket: kind === 'closed' ? 'closed' as const : kind === 'merged' ? 'merged' as const : 'running' as const,
    attention: null, rail: [], chips: [], kind,
    stepsAccepted: 0, stepsTotal: 0, loops: 0, now0: null, lastActivityAt: null, summary: null,
    keySessionId: null, keyArchived: false, keyProjectId: 'p1', chatCount: 1,
  });
  // projectId: 'p1' — как в реальном списке проекта: карточка читает индекс того же
  // проекта, что и SessionList (useFeaturesIndex), а не области '' (вне проектов).
  // p.1: status 'waiting' (живой) — карточка монтируется и тест реально ловит второй
  // запрос индекса (было 'finished' → видимый список пуст, ChatCard не монтировался,
  // и тест проходил тривиально, не доказывая фикс).
  const s = (id: string): Session => ({
    id, projectId: 'p1', mode: 'auto', status: 'waiting', messageCount: 0,
    createdAt: '2026-10-18T10:00:00Z', updatedAt: '2026-10-18T10:00:00Z',
    origin: 'manual',
  } as Session);
  // Индекс: 3 открытые (waiting_you / error / queued) + 1 СМЕРЖЕННАЯ С ОТКРЫТЫМ ЭПИКОМ
  // (kind='merged', эпик не Done → bucket 'merged') + 1 закрытая; каждая со своим чатом
  const f1 = feat('f1', 'waiting_you', 'FICH_W');
  const f2 = feat('f2', 'error', 'FICH_S');
  const f3 = feat('f3', 'queued', 'FICH_Q');
  const g1 = feat('g1', 'merged', 'FICH_G');
  const c1 = feat('c1', 'closed', 'FICH_C');
  const idx: import('../types/featureIndex').FeaturesIndex = {
    features: [f1, f2, f3, g1, c1],
    chats: {
      's1': [{ featureId: 'f1', kind: 'executor', taskRole: null, stepRef: null, lane: null }],
      's2': [{ featureId: 'f2', kind: 'executor', taskRole: null, stepRef: null, lane: null }],
      's3': [{ featureId: 'f3', kind: 'executor', taskRole: null, stepRef: null, lane: null }],
      's5': [{ featureId: 'g1', kind: 'executor', taskRole: null, stepRef: null, lane: null }],
      's4': [{ featureId: 'c1', kind: 'executor', taskRole: null, stepRef: null, lane: null }],
    },
  };

  // Рендер режима «Фичи» с заданными фильтрами (опционально — свой индекс/sessions:
  // кейсы пустых состояний живут на своём индексе). Возвращает контейнер fake-DOM
  async function renderFeatures(
    filters: Record<string, unknown>,
    opts: { features?: ReturnType<typeof feat>[]; sessions?: Session[] } = {},
  ): Promise<_FakeNode> {
    const idxNow = { ...idx, features: opts.features ?? idx.features };
    setAllFlags({ 'features-overview': true });
    featuresIndexTest.reset();
    indexCalls = 0;
    featuresIndexTest.setIndex('p1', idxNow);
    mockIndex = idxNow; // эффект useFeaturesIndex не сбрасывает индекс в EMPTY
    mockSessions = opts.sessions ?? [s('s1'), s('s2'), s('s3'), s('s4'), s('s5')];
    store.clear();
    store.set('cc_chat_filters:p1', JSON.stringify({ groupBy: 'features', sortOrder: 'newest', ...filters }));
    const container = new _FakeNode();
    const root = createRoot(container);
    await act(async () => { root.render(createElement(SessionList, { project, activeSession: null, onSelect: () => {} })); });
    return container;
  }

  // Узел по aria-label (крестик снимаемого чипа)
  function findByAriaLabel(n: _FakeNode, label: string): _FakeNode | null {
    if (n._attrs['aria-label'] === label) return n;
    for (const c of n.childNodes) { const r = findByAriaLabel(c, label); if (r) return r; }
    return null;
  }

  // Заголовок группы фичи: в fake-DOM текстовый узел {feature.title} не создаётся,
  // но span несёт атрибут title={feature.title} — по нему и проверяем, что группа на экране
  function hasFeatureTitle(n: _FakeNode, title: string): boolean {
    if (n._attrs['title'] === title) return true;
    for (const c of n.childNodes) if (hasFeatureTitle(c, title)) return true;
    return false;
  }

  // Родительский элемент текстового узла (кнопка, несущая эту подпись): для клика
  // по «Сбросить» — текст живёт в text-узле, клик — на его <button>-родителе
  function parentOfText(n: _FakeNode, text: string): _FakeNode | null {
    if (n.nodeType === 3 && n.textContent === text) return n.parentElement;
    for (const c of n.childNodes) { const r = parentOfText(c, text); if (r) return r; }
    return null;
  }

  // То же по ПРЕФИКСУ: «Показать закрытые · {N}» — ДВА текстовых узла («Показать
  // закрытые · » и число), точного совпадения whole-text нет никогда
  function parentOfTextStart(n: _FakeNode, prefix: string): _FakeNode | null {
    if (n.nodeType === 3 && n.textContent.startsWith(prefix)) return n.parentElement;
    for (const c of n.childNodes) { const r = parentOfTextStart(c, prefix); if (r) return r; }
    return null;
  }

  // Узел, НЕСУЩИЙ этот текст в своём textContent: одиночный текстовый ребёнок React
  // кладёт в textContent самого элемента (setTextContent, nodeType=1 — заголовок
  // EmptyState), при нескольких детях — в text-ноду. Здесь ровно один ребёнок.
  function nodeWithText(n: _FakeNode, text: string): _FakeNode | null {
    if (n.textContent === text) return n;
    for (const c of n.childNodes) { const r = nodeWithText(c, text); if (r) return r; }
    return null;
  }

  // Узел по data-testid (надёжно в fake-DOM: атрибуты всегда создаются, в отличие от
  // текстовых узлов)
  function findTestId(n: _FakeNode, id: string): _FakeNode | null {
    if (n._attrs['data-testid'] === id) return n;
    for (const c of n.childNodes) { const r = findTestId(c, id); if (r) return r; }
    return null;
  }

  // c1: смерженная с открытым эпиком (kind='merged', эпик не Done) — открытая фича:
  // при умолчании она в обычных открытых группах, секции закрытых нет вовсе.
  // Мутация: в shownOpenIds вернуть `f.bucket !== 'closed'` вместо kind — FICH_G
  // (bucket='merged'?) тут не краснеет; краснеет на «явный merged» (ниже) и на c2.
  it('c1: merged с открытым эпиком при умолчании — в открытых группах, не в секции закрытых', async () => {
    const all = await renderFeatures({});
    expect(hasFeatureTitle(all, 'FICH_G'), 'смерженной с открытым эпиком нет в открытых группах').toBe(true);
    // Секции закрытых при умолчании нет, закрытой фичи на экране нет
    expect(findTestId(all, 'closed-section-header'), 'секция закрытых при умолчании').toBeNull();
    expect(hasFeatureTitle(all, 'FICH_C'), 'закрытая фича при умолчании').toBe(false);
    // Явный выбор «Смержены» оставляет только её (other open kinds сняты)
    const only = await renderFeatures({ featureKinds: ['merged'] });
    expect(hasFeatureTitle(only, 'FICH_G')).toBe(true);
    expect(hasFeatureTitle(only, 'FICH_W'), 'открытая фича под выбором «Смержены»').toBe(false);
  });

  // Мультивыбор оси — OR по kind (замена теста «два чипа дают OR» Ф1.2).
  it('мультивыбор оси даёт OR: waiting_you+error → их фичи, queued не показана', async () => {
    const container = await renderFeatures({ featureKinds: ['waiting_you', 'error'] });
    expect(hasFeatureTitle(container, 'FICH_W')).toBe(true);
    expect(hasFeatureTitle(container, 'FICH_S')).toBe(true);
    expect(hasFeatureTitle(container, 'FICH_Q'), 'фича несогласованного kind показана').toBe(false);
  });

  // c2: «только закрытые» — открытых групп нет, секция закрытых одна; число =
  // рисующиеся закрытые (chatCount > 0), тот же счёт, что у чипа «Закрытые» в FilterBar.
  it('c2: ось «только закрытые» — открытых групп нет, секция закрытых одна с числом', async () => {
    const off = await renderFeatures({});
    expect(findTestId(off, 'closed-section-header'), 'заголовок секции без выбора «Закрытых»').toBeNull();
    const on = await renderFeatures({ featureKinds: ['closed'] });
    expect(hasFeatureTitle(on, 'FICH_W'), 'открытая фича под «только закрытые»').toBe(false);
    expect(hasFeatureTitle(on, 'FICH_G'), 'merged под «только закрытые»').toBe(false);
    expect(hasFeatureTitle(on, 'FICH_C'), 'закрытой фичи нет').toBe(true);
    const header = findTestId(on, 'closed-section-header');
    expect(header, 'заголовка секции «Закрытые» нет').not.toBeNull();
    expect(header!._attrs['data-closed-count'], 'число «Закрытые · N» не по рисующимся').toBe('1');
  });

  // c2: все фичи закрыты (фичи ЕСТЬ, открытых нет) → calm-плашка со ссылкой,
  // которая добавляет «Закрытые» на ось (полный цикл: клик → секция на экране).
  it('c2: все закрыты (умолчание) — calm «Открытых фич нет» + «Показать закрытые · 1», клик поднимает секцию', async () => {
    const container = await renderFeatures({}, { features: [c1], sessions: [s('s4')] });
    expect(parentOfText(container, 'Открытых фич нет.'), 'плашки calm нет').not.toBeNull();
    // Закрытые ещё не выбраны: их ни в секции, ни на экране
    expect(findTestId(container, 'closed-section-header'), 'секция закрытых до клика').toBeNull();
    const link = parentOfTextStart(container, 'Показать закрытые ·');
    expect(link, 'ссылки «Показать закрытые · N» в calm нет').not.toBeNull();
    await act(async () => { container.fireClick(link!); });
    // Клик добавил 'closed' на ось → закрытые встали секцией
    expect(findTestId(container, 'closed-section-header'), 'секции закрытых после клика').not.toBeNull();
    expect(hasFeatureTitle(container, 'FICH_C'), 'закрытой фичи после клика нет').toBe(true);
  });

  // c3: выбранный kind без совпадений (в индексе есть открытые других состояний) →
  // EmptyState «Под фильтр ничего не попало»; «Сбросить фильтр» → [] (умолчание).
  it('c3: noMatch — «Под фильтр ничего не попало», «Сбросить фильтр» возвращает умолчание', async () => {
    const container = await renderFeatures({ featureKinds: ['idle'] });
    expect(hasFeatureTitle(container, 'FICH_W'), 'фича несогласованного kind показана').toBe(false);
    expect(nodeWithText(container, 'Под фильтр ничего не попало'), 'EmptyState noMatch').not.toBeNull();
    const reset = parentOfText(container, 'Сбросить фильтр');
    expect(reset, 'кнопки «Сбросить фильтр» нет').not.toBeNull();
    await act(async () => { container.fireClick(reset!); });
    // Сброс → []: открытые снова на экране, EmptyState снят
    expect(nodeWithText(container, 'Под фильтр ничего не попало'), 'EmptyState после сброса').toBeNull();
    expect(hasFeatureTitle(container, 'FICH_W'), 'после сброса открытой фичи нет').toBe(true);
  });

  // Строка «Показаны» (п.4 записки), вариант «Показаны: …»: крестик убирает значение
  // с оси, остальной выбор не трогает.
  it('«Показаны:» — крестик убирает «Ждут вас» с оси: FICH_W нет, FICH_S остаётся', async () => {
    const container = await renderFeatures({ featureKinds: ['waiting_you', 'error'] });
    const cross = findByAriaLabel(container, 'Убрать фильтр «Ждут вас»');
    expect(cross, 'крестика чипа «Ждут вас» в строке «Показаны» нет').not.toBeNull();
    await act(async () => { container.fireClick(cross!); });
    expect(hasFeatureTitle(container, 'FICH_W'), 'снятый kind не снял свою фичу').toBe(false);
    expect(hasFeatureTitle(container, 'FICH_S'), 'остальной выбор сломан').toBe(true);
  });

  // Вариант «Все открытые, кроме:» (снят один из восьми): крестик ВОЗВРАЩАЕТ снятое;
  // возврат делает выбор равным умолчанию → канон [] (R-Ф2.1 M2), строка исчезает.
  it('«Все открытые, кроме:» — крестик возвращает «Остановленных»: возврат = умолчание []', async () => {
    const seven = ALL_FEATURE_KINDS.filter(k => k !== 'closed' && k !== 'error');
    const container = await renderFeatures({ featureKinds: seven });
    expect(hasFeatureTitle(container, 'FICH_W'), 'неснятый kind пропал').toBe(true);
    expect(hasFeatureTitle(container, 'FICH_S'), 'снятый «Остановлены» не снят').toBe(false);
    const cross = findByAriaLabel(container, 'Вернуть «Остановлены»');
    expect(cross, 'чипа-возврата «Вернуть «Остановлены»» нет').not.toBeNull();
    await act(async () => { container.fireClick(cross!); });
    expect(hasFeatureTitle(container, 'FICH_S'), 'возврат не вернул фичу').toBe(true);
    expect(parentOfText(container, 'Сбросить'), 'строка «Показаны» осталась после возврата к умолчанию').toBeNull();
  });

  it('один запрос индекса на проект (FilterBar не плодит fetch)', async () => {
    // Все потребители (SessionList + карточки) читают индекс того же проекта 'p1'
    // (projectId в фикстуре), поэтому первичная загрузка — ровно один fetch (0→1).
    // Если бы карточка читала область '' (вне проектов) — был бы второй fetch.
    // p.1: фикстура — ЖИВОЙ статус (waiting): карточка монтируется и её
    // useFeaturesIndex реально исполняется. Мутация «ChatCard читает scope null»
    // (вне проектов) красит: indexCalls=2. С 'finished' карточка бы не смонтировалась
    // и тест проходил тривиально, не доказывая фикс.
    await renderFeatures({ featureKinds: ['waiting_you'] });
    expect(indexCalls).toBe(1);
  });

  // «Сбросить» в строке «Показаны» снимает выбор оси одним кликом и не плодит запросов.
  // Мутация: в обработчике `featureKinds: []` → `['error']` — строка остаётся → тест красный.
  it('«Сбросить» снимает выбор оси и не плодит запросов', async () => {
    const container = await renderFeatures({ featureKinds: ['waiting_you', 'error'] });
    const resetBtn = parentOfText(container, 'Сбросить');
    expect(resetBtn, 'кнопки «Сбросить» в строке «Показаны» нет').not.toBeNull();
    await act(async () => { container.fireClick(resetBtn!); });
    // После сброса: выбор равен умолчанию → строки «Показаны» (и «Сбросить») нет
    expect(parentOfText(container, 'Сбросить'), '«Сбросить» остался').toBeNull();
    expect(parentOfText(container, 'Показаны:'), 'подписи «Показаны:» остались').toBeNull();
    // Новых запросов индекса нет: сброс фильтров не меняет проект → fetch не плодится
    expect(indexCalls).toBe(1);
  });

  // m1: «Сбросить» в строке «Показаны» — minHeight 40 на 360 (было 24).
  // SSR (renderToStaticMarkup): стили в style-атрибуте — надёжно. removableChips рендерится
  // при выбранном «Закрытом» на оси независимо от индекса (в SSR effects не идут).
  // Мутация: убрать minHeight:40 из style кнопки → на мобиле height/minHeight по xs (24), тест красный.
  it('m1: «Сбросить» minHeight 40 на 360 (десктоп — 24)', () => {
    setAllFlags({ 'features-overview': true });
    featuresIndexTest.reset();
    // featureKinds:['closed'] → shownFiltersModel не null → строка «Показаны» с «Сбросить»
    store.clear();
    store.set('cc_chat_filters:p1', JSON.stringify({ groupBy: 'features', sortOrder: 'newest', featureKinds: ['closed'] }));
    // Мобила: у кнопки «Сбросить» minHeight 40
    const mobile = renderToStaticMarkup(createElement(SessionList, { project, activeSession: null, onSelect: () => {}, isMobile: true }));
    const mTag = buttonTagWithText(mobile, 'Сбросить');
    expect(mTag).not.toBeNull();
    expect(mTag).toContain('min-height:40px');
    // Десктоп: height/minHeight 24 (xs), minHeight 40 нет
    const desktop = renderToStaticMarkup(createElement(SessionList, { project, activeSession: null, onSelect: () => {} }));
    const dTag = buttonTagWithText(desktop, 'Сбросить');
    expect(dTag).not.toBeNull();
    expect(dTag).not.toContain('min-height:40px');
  });
});

// === Общий ключевой чат фич — в каждой своей группе (решение Андрея 24.10, вариант 1) ===
// Ф3.3 (ссылка «+N чат — в группе «X»») снята: мультифичевый чат теперь рисуется в
// группе каждой своей фичы напрямую, с пометкой «общий с <другая фича>» в строке.
// Здесь: обе группы содержат общий чат ровно по одному разу, метка «общий с …» видна
// в каждой из них, ссылки «+N чат» больше нет. Мутация (в отчёте): groupOf по первой
// фиче (старое правило) — группа одна, метка не находится → тест красный.
describe('SessionList: общий чат фич — в обеих группах с меткой «общий с»', () => {
  const feat = (id: string, title: string) => ({
    featureId: id, title, projectId: 'p1', bucket: 'running' as const,
    attention: null, rail: [], chips: ['open' as const],
    stepsAccepted: 0, stepsTotal: 0, loops: 0, now0: null, lastActivityAt: null, summary: null,
    keySessionId: null, keyArchived: false, keyProjectId: 'p1', chatCount: 2,
  });
  // p.1: статус 'waiting' (живой) — карточка монтируется и метку «общий с» видно
  // даже в свёрнутой группе (живые чаты выводятся наружу).
  const sess = (id: string): Session => ({
    id, projectId: 'p1', mode: 'auto', status: 'waiting', messageCount: 0,
    createdAt: '2026-10-18T10:00:00Z', updatedAt: '2026-10-18T10:00:00Z',
    origin: 'manual',
  } as Session);
  const link = (featureId: string) => ({ featureId, kind: 'executor' as const, taskRole: null, stepRef: null, lane: null });
  // А и B — открытые фичи; chat-shared связан с обеими → общий чат в обеих группах
  const idx = {
    features: [feat('fa', 'Фича A'), feat('fb', 'Фича B')],
    chats: {
      'chat-a': [link('fa')],
      'chat-b': [link('fb')],
      'chat-shared': [link('fa'), link('fb')],
    },
  };

  const findAllAttr = (c: _FakeNode, attr: string, value: string): _FakeNode[] => {
    const out: _FakeNode[] = [];
    const walk = (n: _FakeNode) => {
      if (n._attrs[attr] === value) out.push(n);
      for (const ch of n.childNodes) walk(ch);
    };
    walk(c);
    return out;
  };
  // Заголовок группы фичи: span несёт атрибут title={feature.title} (текстовые узлы
  // в fake-DOM ненадёжны) — по нему и проверяем, что группа на экране
  const hasFeatureTitle = (n: _FakeNode, title: string): boolean => {
    if (n._attrs['title'] === title) return true;
    for (const c of n.childNodes) if (hasFeatureTitle(c, title)) return true;
    return false;
  };
  // Текст узла: React может ставить textContent прямо на элемент (без дочерних
  // текстовых узлов) — берём и текст самого узла, и текст детей.
  const textOf = (n: _FakeNode): string => {
    let out = n.textContent || '';
    for (const c of n.childNodes) out += textOf(c);
    return out;
  };

  // Рендер режима «Фичи» и раскрытие обеих групп (общий чат «живой» — выведен наружу)
  const renderAndExpandAll = async () => {
    setAllFlags({ 'features-overview': true });
    featuresIndexTest.reset();
    featuresIndexTest.setIndex('p1', idx);
    mockIndex = idx;
    mockSessions = [sess('chat-a'), sess('chat-b'), sess('chat-shared')];
    store.clear();
    store.set('cc_chat_filters:p1', JSON.stringify({ groupBy: 'features', sortOrder: 'newest' }));
    const container = new _FakeNode();
    const root = createRoot(container);
    await act(async () => { root.render(createElement(SessionList, { project, activeSession: null, onSelect: vi.fn() })); });
    for (const chevron of findAllAttr(container, 'data-testid', 'feature-group-chevron')) {
      await act(async () => { container.fireClick(chevron); });
    }
    return { container, root };
  };

  it('общий чат — в каждой группе ровно один раз; метка «общий с …» в обеих', async () => {
    const { container, root } = await renderAndExpandAll();
    // Обе группы на экране
    expect(hasFeatureTitle(container, 'Фича A'), 'группа A есть').toBe(true);
    expect(hasFeatureTitle(container, 'Фича B'), 'группа B есть').toBe(true);
    // Метка «общий с» — ровно одна в каждой группе: в A — «общий с Фича B», в B — «общий с Фича A»
    const marks = findAllAttr(container, 'data-testid', 'chat-feature-role').map(m => textOf(m));
    expect(marks.filter(t => t === 'общий с Фича B').length, 'метка в группе A').toBe(1);
    expect(marks.filter(t => t === 'общий с Фича A').length, 'метка в группе B').toBe(1);
    // Ссылки F3.3 «+N чат — в группе …» больше нет
    expect(findAllAttr(container, 'data-testid', 'other-group-link-fb').length, 'ссылки «+N чат» нет').toBe(0);
    root.unmount();
  });

  it('однотематический чат — метки «общий с» не имеет', async () => {
    const { container, root } = await renderAndExpandAll();
    // chat-a связан только с A: его строка не несёт метки (общих меток ровно две — на shared)
    const marks = findAllAttr(container, 'data-testid', 'chat-feature-role').map(m => textOf(m));
    expect(marks.length).toBe(2);
    root.unmount();
  });
});

// Открывающий тег <button ...> с заданным текстом-содержимым (SSR: style в открывающем
// теге, текст — содержимое). Возвращает строку тега или null.
function buttonTagWithText(html: string, text: string): string | null {
  const end = html.indexOf(`>${text}</button>`);
  if (end < 0) return null;
  const start = html.lastIndexOf('<button', end);
  if (start < 0) return null;
  return html.slice(start, end);
}
