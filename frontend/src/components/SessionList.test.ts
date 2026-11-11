// Кламп groupBy в SessionList (Минор 1 по R4): недоступный режим
// сохранённый в localStorage groupBy='features' не должен уводить список в режим фич
// (иначе — EmptyState «Фич пока нет» без пункта «Фичи» в тулбаре). Клампим к 'days',
// как 'subjects' в глобальном списке (ChatList.tsx); при включённом флаге значение
// из хранилища снова работает.
//
// Окружение node (без jsdom): renderToStaticMarkup (SSR) — effects не идут, поэтому
// sessions остаются пустыми; различие режимов ловим по строке «Фич пока нет», которая
// есть ТОЛЬКО в режиме фич (и зависит от индекса фич, а не от sessions).
// После FI-1 reset() стора = «ещё не загружено» (в режиме фич — строка «Загружаем
// фичи…»); «Фич пока нет» даёт только явный пустой ГОТОВЫЙ индекс (EMPTY_READY).

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

// signalr: подписки не открывают соединение (HttpConnection роняет resolveUrl('/hubs/session')
// без полного window.location), но onMessage ЗАХВАТЫВАЕТ обработчики: кейс S2d-F2 сам
// зовёт их с событием — реакция проверяется на настоящем сторе списка
const { msgHandlers } = vi.hoisted(() => ({ msgHandlers: [] as ((msg: unknown) => void)[] }));
const toastMock = vi.hoisted(() => vi.fn());
vi.mock('../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/signalr')>();
  const noop = () => () => {};
  const capture = (fn: (msg: unknown) => void) => { msgHandlers.push(fn); return () => { const i = msgHandlers.indexOf(fn); if (i >= 0) msgHandlers.splice(i, 1); }; };
  return { ...orig, onMessage: capture, onReconnected: noop, onGitStatusChanged: noop, onFilesChanged: noop, joinUser: noop };
});
// toast: мокаем целиком — нейтральное сообщение об уходе приватного чата проверяется по вызову
vi.mock('../lib/toast', () => ({ showToast: toastMock }));

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

// Пустой ГОТОВЫЙ индекс: ответ «фич нет» — отличается от «ещё не загружено» (reset()).
const EMPTY_READY: import('../types/featureIndex').FeaturesIndex = { features: [], chats: {} };

// Рендер списка: groupBy, сохранённый в хранилище.
// Индекс фич: без index — сброшен («ещё не загружено», в режиме фич — «Загружаем
// фичи…»); с index — положен явный срез (пустой даёт «Фич пока нет»).
function renderList(
  _featuresOn: boolean,
  storedGroupBy: string,
  index?: import('../types/featureIndex').FeaturesIndex,
): string {
  store.clear();
  featuresIndexTest.reset();
  if (index) featuresIndexTest.setIndex('p1', index);
  store.set('cc_chat_filters:p1', JSON.stringify({ groupBy: storedGroupBy, sortOrder: 'newest' }));
  return renderToStaticMarkup(
    createElement(SessionList, { project, activeSession: null, onSelect: () => {} }),
  );
}

beforeEach(() => {
  store.clear();
  setAllFlags({});
  featuresIndexTest.reset();
  indexGate = null;
});

describe('SessionList: groupBy из хранилища', () => {
  it('groupBy=features → режим фич («Фич пока нет» при пустом индексе)', () => {
    // Пустой ГОТОВЫЙ индекс: после FI-1 его надо положить явно (reset() = «не готово»)
    const html = renderList(true, 'features', EMPTY_READY);
    // Режим фичи включился: пустой индекс → её пустое состояние
    expect(html).toContain('Фич пока нет');
  });

  it('groupBy=days (обычное значение) → режим «Дни», без «Фич пока нет»', () => {
    const html = renderList(true, 'days');
    expect(html).not.toContain('Фич пока нет');
  });
});

// M1′: «Фич пока нет» — по содержимому (inline), не height:100%.
// До фикса EmptyState тянулся на всю высоту панели, а под ним (в «Без фичи»)
// стояли обычные чаты. inline — контейнер по содержимому.
describe('SessionList: M1′ «Фич пока нет» по содержимому', () => {
  it('пустой индекс: «Фич пока нет» без height:100% (inline)', () => {
    const html = renderList(true, 'features', EMPTY_READY);
    expect(html).toContain('Фич пока нет');
    expect(html).not.toContain('height:100%');
  });
});

// === FI-2: строка загрузки вместо «Без фичи» ===
// Первый вход в проект (индекс ещё не готов): режим «Фичи» рисует одну строку
// «Загружаем фичи…» — ни «Без фичи», ни «Фич пока нет», ни ветки «Дни» ниже.
// Иначе приход индекса переносит все карточки из контейнера «no-feature» в группы
// и весь список монтируется заново (баг Андрея). Мутация: убрать featuresReady из
// условия featureGroups → вместо null — пустой массив групп (sessions в SSR пусты,
// «Без фичи» нет) → вместо строки загрузки рисуется «Фич пока нет» (пустой индекс) →
// первый кейс красный по assert’у на «Фич пока нет».
describe('SessionList: FI-2 строка загрузки, пока индекс не готов', () => {
  it('индекс не готов (reset без setIndex): «Загружаем фичи…», без «Без фичи» и «Фич пока нет»', () => {
    const html = renderList(true, 'features');
    expect(html).toContain('Загружаем фичи…');
    expect(html).not.toContain('Без фичи');
    expect(html).not.toContain('Фич пока нет');
  });

  it('пустой ГОТОВЫЙ индекс: «Загружаем фичи…» нет, «Фич пока нет» есть', () => {
    const html = renderList(true, 'features', EMPTY_READY);
    expect(html).not.toContain('Загружаем фичи…');
    expect(html).toContain('Фич пока нет');
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
// api.chats.get для кейса S2d-F2: поведение задаёт сам кейс (404 — «чата для тебя нет»,
// 200 — свежая копия с privateToUserId)
let getImpl: (id: string) => Promise<Session> = async () => { throw { status: 404 }; };
// useFeaturesIndex (effects) сам зовёт api.tasks.featuresIndex и setIndex-ит результат:
// без мока он падает (нет сервера) → сброс в EMPTY, и группа фичи не рисуется
let mockIndex: import('../types/featureIndex').FeaturesIndex = { features: [], chats: {} };
// Гейт запроса индекса: когда положен — fetch не возвращается, стор держит состояние
// «индекс не готов» (кейс FI-4 «Чатов пока нет» рядом со строкой загрузки)
let indexGate: Promise<void> | null = null;
// Счётчик запросов индекса: «один запрос на проект» — секция FilterBar фич не
// должна плодить второй fetch (индекс приходит из SessionList, не из FilterBar)
let indexCalls = 0;
vi.mock('../lib/api', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/api')>();
  return { ...orig, api: { ...orig.api,
    sessions: { ...orig.api.sessions, list: async () => mockSessions },
    tasks: { ...orig.api.tasks, featuresIndex: async () => { indexCalls++; if (indexGate) await indexGate; return mockIndex; } },
    chats: { ...orig.api.chats, get: (id: string) => getImpl(id) },
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
    featuresIndexTest.reset();
    const idx = { features: [closedFeat('c1')], chats: { 'c1-chat': [{ featureId: 'c1', kind: 'executor', taskRole: null, stepRef: null, lane: null }] } };
    featuresIndexTest.setIndex('p1', idx);
    mockIndex = idx; // эффект useFeaturesIndex не сбрасывает индекс в EMPTY
    mockSessions = [sess('c1-chat')];
    store.clear();
    store.set('cc_chat_filters:p1', JSON.stringify({ groupBy: 'features', sortOrder: 'newest' }));

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
    // FS-FL2b: секция «Закрытые · N ›» свёрнута — раскрываем её строку-переключатель
    const closedToggle = (function find(n: _FakeNode): _FakeNode | null {
      if (n._attrs['data-testid'] === 'closed-section-toggle') return n;
      for (const c of n.childNodes) { const r = find(c); if (r) return r; }
      return null;
    })(container);
    expect(closedToggle, 'строки «Закрытые · N ›» нет').not.toBeNull();
    await act(async () => { container.fireClick(closedToggle!); });
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

// === FS-FL2b: режим «Фичи» — секции вместо фильтра, «Закрытые · N ›» свёрнуты ===
// Раскладку даёт featureListSections (по kind, FS-FL2a): «Ждут тебя»/«Стоят» → «В работе» →
// «Закрытые» (closed). merged — в «В работе» (FS-AC3a, решение Андрея 11.11): после мержа
// фича открыта, пока человек сам не нажмёт «Закрыть». Фронт ничего не отбрасывает;
// сохранённый featureKinds не читается.
describe('SessionList: FS-FL2b режим фич секциями', () => {
  const feat = (id: string, kind: FeatureKind, title: string, projectId = 'p1') => ({
    featureId: id, title, projectId,
    bucket: kind === 'closed' ? 'closed' as const : kind === 'merged' ? 'merged' as const : 'running' as const,
    attention: null, rail: [], chips: [], kind,
    stepsAccepted: 0, stepsTotal: 0, loops: 0, now0: null, lastActivityAt: null, summary: null,
    keySessionId: null, keyArchived: false, keyProjectId: projectId, chatCount: 1,
  });
  // status 'waiting' (живой): карточка монтируется, как в реальном списке
  const s = (id: string, projectId = 'p1'): Session => ({
    id, projectId, mode: 'auto', status: 'waiting', messageCount: 0,
    createdAt: '2026-10-18T10:00:00Z', updatedAt: '2026-10-18T10:00:00Z',
    origin: 'manual',
  } as Session);
  const link = (featureId: string) => [{ featureId, kind: 'executor' as const, taskRole: null, stepRef: null, lane: null }];
  const f1 = feat('f1', 'waiting_you', 'FICH_W');
  const f2 = feat('f2', 'error', 'FICH_S');
  const f3 = feat('f3', 'queued', 'FICH_Q');
  const g1 = feat('g1', 'merged', 'FICH_G');
  const c1 = feat('c1', 'closed', 'FICH_C');
  const idx: import('../types/featureIndex').FeaturesIndex = {
    features: [f1, f2, f3, g1, c1],
    chats: { s1: link('f1'), s2: link('f2'), s3: link('f3'), s5: link('g1'), s4: link('c1') },
  };

  async function renderFeatures(
    filters: Record<string, unknown> = {},
    opts: { features?: ReturnType<typeof feat>[]; sessions?: Session[]; isMobile?: boolean } = {},
  ): Promise<{ container: _FakeNode; rerender: (p: Project) => Promise<void> }> {
    const idxNow = { ...idx, features: opts.features ?? idx.features };
    featuresIndexTest.reset();
    featuresIndexTest.setIndex('p1', idxNow);
    mockIndex = idxNow;
    mockSessions = opts.sessions ?? [s('s1'), s('s2'), s('s3'), s('s4'), s('s5')];
    store.clear();
    store.set('cc_chat_filters:p1', JSON.stringify({ groupBy: 'features', sortOrder: 'newest', ...filters }));
    const container = new _FakeNode();
    const root = createRoot(container);
    const render = (pr: Project) => act(async () => {
      root.render(createElement(SessionList, { project: pr, activeSession: null, onSelect: () => {}, isMobile: opts.isMobile }));
    });
    await render(project);
    return { container, rerender: render };
  }

  function findTestId(n: _FakeNode, id: string): _FakeNode | null {
    if (n._attrs['data-testid'] === id) return n;
    for (const c of n.childNodes) { const r = findTestId(c, id); if (r) return r; }
    return null;
  }
  function allTestIds(n: _FakeNode, id: string, out: _FakeNode[] = []): _FakeNode[] {
    if (n._attrs['data-testid'] === id) out.push(n);
    for (const c of n.childNodes) allTestIds(c, id, out);
    return out;
  }
  function textOf(n: _FakeNode): string {
    let t = n.textContent ?? '';
    for (const c of n.childNodes) t += textOf(c);
    return t;
  }
  // Группа фичи рисуется заголовком с атрибутом title={feature.title} — ровно один на группу
  function countTitle(n: _FakeNode, title: string): number {
    let c = n._attrs['title'] === title ? 1 : 0;
    for (const ch of n.childNodes) c += countTitle(ch, title);
    return c;
  }
  function parentOfTextStart(n: _FakeNode, prefix: string): _FakeNode | null {
    if (n.nodeType === 3 && n.textContent.startsWith(prefix)) return n.parentElement;
    for (const c of n.childNodes) { const r = parentOfTextStart(c, prefix); if (r) return r; }
    return null;
  }

  it('секции по порядку: «Ждут тебя» → «В работе»; «Закрытые · 1 ›» свёрнуты, их фич на экране нет', async () => {
    const { container } = await renderFeatures();
    const titles = allTestIds(container, 'feature-section-title').map(textOf);
    expect(titles, 'подписи секций или их порядок').toEqual(['Ждут тебя', 'В работе']);
    for (const t of ['FICH_W', 'FICH_S', 'FICH_Q']) expect(countTitle(container, t), `${t} должна быть один раз`).toBe(1);
    // FS-AC3a: merged — в «В работе», видна без раскрытия
    expect(countTitle(container, 'FICH_G'), 'merged видна в «В работе»').toBe(1);
    const toggle = findTestId(container, 'closed-section-toggle');
    expect(toggle, 'строки «Закрытые · N ›» нет').not.toBeNull();
    expect(textOf(toggle!), 'число в строке «Закрытые»').toContain('1');
    expect(toggle!._attrs['aria-expanded']).toBe('false');
    expect(countTitle(container, 'FICH_C'), 'closed видна в свёрнутой секции').toBe(0);
  });

  // Инвариант: после раскрытия каждая из 5 фич — ровно один раз; повторный клик сворачивает.
  // Мутация 1: в closed-ветке не рисовать sec.groups при closedExpanded → тест красный.
  // Мутация 2 (FS-T12, минор ревью R-AC3a): вернуть merged в секцию «Закрытые»
  // (FEATURE_LIST_SECTION_OF_KIND в chatGroups.ts) → краснеет assert про FICH_G после сворачивания.
  it('клик по «Закрытые» раскрывает: все пять фич ровно по разу; повторный клик сворачивает', async () => {
    const { container } = await renderFeatures();
    await act(async () => { container.fireClick(findTestId(container, 'closed-section-toggle')!); });
    expect(findTestId(container, 'closed-section-toggle')!._attrs['aria-expanded']).toBe('true');
    for (const t of ['FICH_W', 'FICH_S', 'FICH_Q', 'FICH_G', 'FICH_C']) {
      expect(countTitle(container, t), `${t} должна быть ровно один раз`).toBe(1);
    }
    await act(async () => { container.fireClick(findTestId(container, 'closed-section-toggle')!); });
    expect(countTitle(container, 'FICH_C'), 'после повторного клика закрытая осталась').toBe(0);
    // FS-AC3a: merged не относится к «Закрытым» — она остаётся видимой и после сворачивания
    // закрытых (FS-T12).
    expect(countTitle(container, 'FICH_G'), 'смерженная пропала после сворачивания').toBe(1);
    expect(countTitle(container, 'FICH_W'), 'открытые пропали').toBe(1);
  });

  // Сохранённая ось не читается: фильтра нет, строки «Показаны» нет, FICH_Q виден.
  // Мутация: вернуть отбор по featureKinds → FICH_Q пропадёт, тест красный.
  it('сохранённый featureKinds=[waiting_you] не читается: показаны все открытые, строки «Показаны» нет', async () => {
    const { container } = await renderFeatures({ featureKinds: ['waiting_you'] });
    for (const t of ['FICH_W', 'FICH_S', 'FICH_Q']) expect(countTitle(container, t), t).toBe(1);
    expect(parentOfTextStart(container, 'Показаны'), 'строка «Показаны» осталась').toBeNull();
    expect(parentOfTextStart(container, 'Сбросить'), 'кнопка «Сбросить» оси осталась').toBeNull();
  });

  // Инвариант: индекс из 9 фич (по одной на каждый kind), «Закрытые» раскрыты → групп ровно 9.
  // Мутация: отбор по сохранённому featureKinds (старый featureMatchesKinds) → групп меньше, красный.
  it('инвариант: 9 фич по одной на каждый kind, «Закрытые» раскрыты → заголовков групп ровно 9, дублей нет', async () => {
    const all = ALL_FEATURE_KINDS.map((k, i) => feat(`k${i}`, k, `KIND_${k}`));
    const chats: Record<string, ReturnType<typeof link>> = {};
    all.forEach((f, i) => { chats[`c${i}`] = link(f.featureId); });
    featuresIndexTest.reset();
    const idx9 = { features: all, chats };
    featuresIndexTest.setIndex('p1', idx9);
    mockIndex = idx9;
    mockSessions = all.map((_, i) => s(`c${i}`));
    store.clear();
    // сохранённая ось, которая раньше сузила бы список до одной фичи
    store.set('cc_chat_filters:p1', JSON.stringify({ groupBy: 'features', sortOrder: 'newest', featureKinds: ['waiting_you'] }));
    const container = new _FakeNode();
    const root = createRoot(container);
    await act(async () => { root.render(createElement(SessionList, { project, activeSession: null, onSelect: () => {} })); });
    await act(async () => { container.fireClick(findTestId(container, 'closed-section-toggle')!); });
    expect(allTestIds(container, 'feature-group-header'), 'число заголовков групп').toHaveLength(9);
    for (const k of ALL_FEATURE_KINDS) expect(countTitle(container, `KIND_${k}`), `KIND_${k}`).toBe(1);
  });

  it('только error → подпись секции «Стоят», не «Ждут тебя»', async () => {
    const { container } = await renderFeatures({}, { features: [f2], sessions: [s('s2')] });
    expect(allTestIds(container, 'feature-section-title').map(textOf)).toEqual(['Стоят']);
  });

  it('все закрыты: calm «Открытых фич нет», строка «Закрытые · 1» свёрнута, клик показывает фичу', async () => {
    const { container } = await renderFeatures({}, { features: [c1], sessions: [s('s4')] });
    expect(parentOfTextStart(container, 'Открытых фич нет.'), 'плашки calm нет').not.toBeNull();
    expect(countTitle(container, 'FICH_C'), 'закрытая видна до клика').toBe(0);
    await act(async () => { container.fireClick(findTestId(container, 'closed-section-toggle')!); });
    expect(countTitle(container, 'FICH_C')).toBe(1);
  });

  it('нет закрытых — строки «Закрытые» нет вовсе', async () => {
    const { container } = await renderFeatures({}, { features: [f1, f3], sessions: [s('s1'), s('s3')] });
    expect(findTestId(container, 'closed-section-toggle')).toBeNull();
  });

  it('360: строка «Закрытые» высотой не меньше 44', async () => {
    const { container } = await renderFeatures({}, { isMobile: true });
    const toggle = findTestId(container, 'closed-section-toggle');
    expect(toggle, 'строки нет').not.toBeNull();
    expect(toggle!.style.minHeight, 'minHeight строки на мобиле').toBe('44px');
  });

  // Конкурентность: смена проекта при раскрытых «Закрытых». Индекс нового проекта приходит
  // асинхронно; после него секция свёрнута, строк старого проекта в DOM нет.
  // Мутация: хранить раскрытие булевым флагом (без привязки к project.id) → секция
  // остаётся раскрытой, FICH_X2 виден, тест красный.
  it('смена проекта при раскрытых «Закрытых»: секция снова свёрнута, строк старого проекта нет', async () => {
    const { container, rerender } = await renderFeatures();
    await act(async () => { container.fireClick(findTestId(container, 'closed-section-toggle')!); });
    expect(countTitle(container, 'FICH_C'), 'до смены проекта закрытая не раскрыта').toBe(1);

    const x1 = feat('x1', 'running', 'FICH_X1', 'p2');
    const x2 = feat('x2', 'closed', 'FICH_X2', 'p2');
    const idx2: import('../types/featureIndex').FeaturesIndex = { features: [x1, x2], chats: { t1: link('x1'), t2: link('x2') } };
    featuresIndexTest.setIndex('p2', idx2);
    mockIndex = idx2;
    mockSessions = [s('t1', 'p2'), s('t2', 'p2')];
    store.set('cc_chat_filters:p2', JSON.stringify({ groupBy: 'features', sortOrder: 'newest' }));
    await rerender({ ...project, id: 'p2' } as Project);

    for (const t of ['FICH_W', 'FICH_S', 'FICH_Q', 'FICH_G', 'FICH_C']) {
      expect(countTitle(container, t), `${t} старого проекта осталась в DOM`).toBe(0);
    }
    expect(countTitle(container, 'FICH_X1'), 'открытая фича нового проекта').toBe(1);
    const toggle = findTestId(container, 'closed-section-toggle');
    expect(toggle, 'строка «Закрытые» нового проекта').not.toBeNull();
    expect(toggle!._attrs['aria-expanded'], 'секция осталась раскрытой после смены проекта').toBe('false');
    expect(countTitle(container, 'FICH_X2'), 'закрытая нового проекта видна свёрнутой').toBe(0);
  });

  it('один запрос индекса на проект', async () => {
    indexCalls = 0;
    await renderFeatures();
    expect(indexCalls).toBe(1);
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

// === S2d-F2: chat_visibility_changed — реакция настоящего стора списка ===
// Событие шлётся в project-группу после того, как сервер снял чужие соединения с группы
// чата. Различение — по ответу GET (он сильнее локальной копии): автору чат виден
// (200 с privateToUserId — копия обновляется), неавтору 404 (чат убирается, открытый
// уводится на соседа, нейтральное сообщение).
describe('SessionList: chat_visibility_changed (S2d-F2)', () => {
  const s2 = (id: string, over: Partial<Session> = {}): Session => ({
    id, projectId: 'p1', name: `Чат ${id}`, mode: 'auto', status: 'finished', messageCount: 0,
    createdAt: '2026-11-01T10:00:00Z', updatedAt: '2026-11-01T10:00:00Z', origin: 'manual', ...over,
  } as Session);

  async function mountList(active: Session | null) {
    const lists: Session[][] = [];
    const onActiveDeleted = vi.fn();
    const container = new _FakeNode();
    const root = createRoot(container);
    const startIdx = msgHandlers.length; // только обработчики ЭТОГО монтажа (чужие уже сняты unmount)
    await act(async () => {
      root.render(createElement(SessionList, {
        project, activeSession: active, onSelect: () => {}, onActiveDeleted,
        onSessionsListChanged: (l: Session[]) => lists.push(l),
      }));
    });
    return { lists, onActiveDeleted, root, startIdx };
  }

  it('неавтору (GET 404): чат убран из списка, открытый уведён на соседа, нейтральное сообщение', async () => {
    setAllFlags({}); featuresIndexTest.reset(); store.clear();
    mockSessions = [s2('c1'), s2('c2')];
    getImpl = async () => { throw { status: 404 }; };
    toastMock.mockReset();
    const { lists, onActiveDeleted, root, startIdx } = await mountList(s2('c1'));
    expect(lists.at(-1)?.map(s => s.id)).toEqual(['c1', 'c2']);
    await act(async () => {
      for (const h of msgHandlers.slice(startIdx)) h({ type: 'chat_visibility_changed', sessionId: 'c1', private: true });
      await Promise.resolve();
    });
    expect(lists.at(-1)?.map(s => s.id)).toEqual(['c2']);
    expect(onActiveDeleted).toHaveBeenCalledTimes(1);
    expect((onActiveDeleted.mock.calls[0] as [Session | null])[0]?.id).toBe('c2');
    expect(toastMock).toHaveBeenCalledTimes(1);
    const [title, text] = toastMock.mock.calls[0] as [string, string];
    expect(title).toBe('Видимость чата');
    expect(text).toBe('Чат стал приватным — вы его больше не видите');
    root.unmount();
  });

  it('автору (GET 200 с privateToUserId): чат остаётся, его копия обновлена; сосед и тоста нет', async () => {
    setAllFlags({}); featuresIndexTest.reset(); store.clear();
    mockSessions = [s2('c1', { createdByUserId: 'me-1' })];
    getImpl = async (id: string) => ({ ...s2(id, { createdByUserId: 'me-1' }), privateToUserId: 'me-1' });
    toastMock.mockReset();
    const { lists, onActiveDeleted, root, startIdx } = await mountList(null);
    await act(async () => {
      for (const h of msgHandlers.slice(startIdx)) h({ type: 'chat_visibility_changed', sessionId: 'c1', private: true });
      await Promise.resolve();
    });
    const kept = lists.at(-1)?.find(s => s.id === 'c1');
    expect(kept?.privateToUserId).toBe('me-1');
    expect(onActiveDeleted).not.toHaveBeenCalled();
    expect(toastMock).not.toHaveBeenCalled();
    root.unmount();
  });

  it('сбой сети (не 404) — список не тронут: на реконнекте он перечитается сам', async () => {
    setAllFlags({}); featuresIndexTest.reset(); store.clear();
    mockSessions = [s2('c1'), s2('c2')];
    getImpl = async () => { throw { status: 0 }; };
    toastMock.mockReset();
    const { lists, onActiveDeleted, root, startIdx } = await mountList(s2('c1'));
    await act(async () => {
      for (const h of msgHandlers.slice(startIdx)) h({ type: 'chat_visibility_changed', sessionId: 'c1', private: true });
      await Promise.resolve();
    });
    expect(lists.at(-1)?.map(s => s.id)).toEqual(['c1', 'c2']);
    expect(onActiveDeleted).not.toHaveBeenCalled();
    expect(toastMock).not.toHaveBeenCalled();
    root.unmount();
  });
});

// === FI-4 (хвосты ревью FI-2): отсев чатов чужого проекта + «Чатов пока нет» рядом со строкой загрузки ===
describe('SessionList: FI-4 отсев чатов чужого проекта', () => {
  // Форма индекса — как в Ф-2.2: kind — единственный признак состояния; «живой» чат
  // (status 'waiting') видим и в свёрнутой группе — карточку видно без клика шеврона.
  const feat = (id: string, title: string) => ({
    featureId: id, title, projectId: 'p1', bucket: 'running' as const,
    attention: null, rail: [], chips: [], kind: 'waiting_you' as const,
    stepsAccepted: 0, stepsTotal: 0, loops: 0, now0: null, lastActivityAt: null, summary: null,
    keySessionId: null, keyArchived: false, keyProjectId: 'p1', chatCount: 1,
  });
  const sess = (id: string, projectId: string, name: string): Session => ({
    id, projectId, name, mode: 'auto', status: 'waiting', messageCount: 0,
    createdAt: '2026-10-18T10:00:00Z', updatedAt: '2026-10-18T10:00:00Z', origin: 'manual',
  } as Session);
  const link = (featureId: string) => ({ featureId, kind: 'executor' as const, taskRole: null, stepRef: null, lane: null });
  const idx: import('../types/featureIndex').FeaturesIndex = {
    features: [feat('f1', 'ФИЧА_A')],
    chats: { 'p1-linked': [link('f1')] },
  };

  // Узел с данным title-атрибутом: заголовок группы (FeatureGroupHeader.tsx:268,
  // title={feature.title}) и название чата (ChatCard.tsx:1137, span title={displayName})
  // — по нему и видим, что группа/карточка на экране (текстовые узлы в fake-DOM ненадёжны)
  const hasTitle = (n: _FakeNode, title: string): boolean => {
    if (n._attrs['title'] === title) return true;
    for (const c of n.childNodes) if (hasTitle(c, title)) return true;
    return false;
  };

  // Состояние смены проекта (SessionList.tsx:845-852): в sessions ещё лежат чаты
  // прежнего проекта (их подмешивает кэш idb), в индексе нового — их нет. Без фильтра
  // по project.id в projectSessions (SessionList.tsx:849-851) чат чужого проекта
  // всплыл бы в «Без фичи» до прихода своего списка. Мутация «убрать фильтр» → красный.
  it('индекс p1 готов, в sessions чат p2: его нет ни в «Без фичи», ни в группах', async () => {
    featuresIndexTest.reset();
    featuresIndexTest.setIndex('p1', idx);
    mockIndex = idx; // эффект useFeaturesIndex не сбрасывает индекс в EMPTY
    mockSessions = [
      sess('p1-linked', 'p1', 'ЧАТ_P1_СВЯЗАН'), // связан с f1 → группа A
      sess('p1-free', 'p1', 'ЧАТ_P1_БЕЗФИ'), // своего проекта, без связи → «Без фичи»
      sess('p2-foreign', 'p2', 'ЧАТ_P2_ЧУЖОЙ'), // чат другого проекта → нигде
    ];
    store.clear();
    store.set('cc_chat_filters:p1', JSON.stringify({ groupBy: 'features', sortOrder: 'newest' }));
    const container = new _FakeNode();
    const root = createRoot(container);
    await act(async () => { root.render(createElement(SessionList, { project, activeSession: null, onSelect: vi.fn() })); });
    // Оба чата своего проекта на экране: связанный — в группе, без связи — в «Без фичи»
    expect(hasTitle(container, 'ФИЧА_A'), 'группа A есть').toBe(true);
    expect(hasTitle(container, 'ЧАТ_P1_СВЯЗАН'), 'связанный чат в группе').toBe(true);
    expect(hasTitle(container, 'ЧАТ_P1_БЕЗФИ'), 'чат без связи в «Без фичи»').toBe(true);
    // Чат p2 — ни в «Без фичи», ни в какой-либо группе: его карточки нет вовсе
    expect(hasTitle(container, 'ЧАТ_P2_ЧУЖОЙ'), 'чата чужого проекта нет').toBe(false);
    root.unmount();
  });
});

// FI-4, минор 4: при пустом списке и НЕГОТОВОМ индексе состояния два — «Чатов пока нет»
// (empty-block до цепочки групп) и «Загружаем фичи…» (в цепочке) — и второе
// противоречит первому. Правка: пустой empty-state подавляется, пока индекс не приехал.
// Мутация: убрать из его условия !(featuresMode && !featuresReady) → «Чатов пока нет»
// возвращается → кейс красный.
describe('SessionList: FI-4 «Чатов пока нет» не рядом со строкой загрузки', () => {
  // Текст всего дерева: React кладёт одиночный текстовый ребёнок в textContent самого
  // элемента, нескольких детей — в text-ноды; берём и то, и другое (как textOf выше)
  const allText = (n: _FakeNode): string => {
    let out = n.textContent || '';
    for (const c of n.childNodes) out += allText(c);
    return out;
  };

  it('пустой проект, индекс не готов: только «Загружаем фичи…», без «Чатов пока нет»', async () => {
    featuresIndexTest.reset();
    indexGate = new Promise<void>(() => {}); // запрос индекса не возвращается: состояние «не готов»
    mockSessions = []; // список приехал пустой: loaded=true, чатов нет
    store.clear();
    store.set('cc_chat_filters:p1', JSON.stringify({ groupBy: 'features', sortOrder: 'newest' }));
    const container = new _FakeNode();
    const root = createRoot(container);
    await act(async () => { root.render(createElement(SessionList, { project, activeSession: null, onSelect: vi.fn() })); });
    indexGate = null;
    const text = allText(container);
    expect(text, 'строка загрузки есть').toContain('Загружаем фичи…');
    expect(text, 'второго empty-state рядом нет').not.toContain('Чатов пока нет');
    root.unmount();
  });
});
