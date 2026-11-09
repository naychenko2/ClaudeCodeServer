// Скелетон истории (MediaHistory, ревью d05911c0): пульс ccs-lm-skeleton ставится
// всем строкам, но при prefers-reduced-motion анимации нет (паттерн как в TextTab/MediaTab).
//
// Окружение node, без jsdom: клиентский рендер через createRoot на минимальном фейк-DOM
// (паттерн из audioEditor/thread/gpuCard.test.ts) — именно он гоняет
// usePrefersReducedMotion по клиентской ветке (getSnapshot → window.matchMedia).
// useMediaHistory замокан: фиксированный снапшот с loading=true (скелетоны), без фетчей.

import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { MediaHistory } from './MediaHistory';

vi.mock('./useMediaHistory', () => ({
  useMediaHistory: () => ({
    filters: { kind: 'all', period: '7d', outcome: null, model: null, login: null },
    records: [],
    cursor: null,
    loading: true,
    loadingMore: false,
    error: null,
    counters: { kinds: {}, outcomes: {} },
    setFilters: () => {},
    resetFilters: () => {},
    loadMore: () => {},
    refresh: () => {},
    retry: () => {},
  }),
}));

vi.mock('../../lib/defaultPersona', () => ({
  useMe: () => ({
    loaded: true, userId: 'u1', username: 'me', defaultPersonaId: null,
    role: '', needsOnboarding: false, onboardingSessionId: null, executionEnvironment: null,
  }),
}));

// ── Минимальный фейк-DOM для createRoot (node, без jsdom) — паттерн из gpuCard.test.ts ──
// React в клиентском рендере вызывает только то, что нужно для монтирования и делегирования
// событий: nodeType, связи родитель/потомок, add/removeEventListener, style, атрибуты, текст.

class FNode {
  nodeType = 3 as 1 | 3;
  nodeName = '#text';
  nodeValue = '';
  // textContent как в реальном DOM: запись заменяет всех потомков одним text node
  private _tc = '';
  get textContent() { return this._tc; }
  set textContent(v: string) {
    this._tc = v;
    for (const c of [...this.childNodes]) this.removeChild(c);
    if (v) this.appendChild(this.ownerDocument.createTextNode(v));
  }
  parentElement: FNode | null = null;
  parentNode: FNode | null = null;
  childNodes: FNode[] = [];
  firstChild: FNode | null = null;
  lastChild: FNode | null = null;
  nextSibling: FNode | null = null;
  previousSibling: FNode | null = null;
  ownerDocument: FDoc;
  style: Record<string, string> = {};
  attributes: Record<string, string> = {};
  listeners: Record<string, Set<unknown>> = {};

  constructor(doc: FDoc) { this.ownerDocument = doc; }

  appendChild(child: FNode): FNode { this._link(child, this.childNodes.length); return child; }
  insertBefore(child: FNode, ref: FNode | null): FNode {
    const i = ref ? this.childNodes.indexOf(ref) : this.childNodes.length;
    this._link(child, Math.max(0, i));
    return child;
  }
  removeChild(child: FNode): FNode {
    const i = this.childNodes.indexOf(child);
    if (i >= 0) this.childNodes.splice(i, 1);
    this._unlink(child);
    return child;
  }
  replaceChild(next: FNode, prev: FNode): FNode {
    this.insertBefore(next, prev);
    this.removeChild(prev);
    return prev;
  }
  contains(n: FNode): boolean {
    for (let c: FNode | null = n; c; c = c.parentNode) if (c === this) return true;
    return false;
  }
  addEventListener(t: string, f: unknown) { (this.listeners[t] ??= new Set()).add(f); }
  removeEventListener(t: string, f: unknown) { this.listeners[t]?.delete(f); }
  setAttribute(n: string, v: unknown) { this.attributes[n] = String(v); }
  setAttributeNS() {}
  removeAttribute(n: string) { delete this.attributes[n]; }
  getAttribute(n: string) { return this.attributes[n] ?? null; }
  focus() {}
  blur() {}
  querySelector(): null { return null; }
  getBoundingClientRect() { return { top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0, x: 0, y: 0 }; }

  _link(child: FNode, at: number) {
    if (child.parentNode) child.parentNode.removeChild(child);
    child.parentNode = this;
    child.parentElement = this;
    this.childNodes.splice(at, 0, child);
    child.previousSibling = this.childNodes[at - 1] ?? null;
    child.nextSibling = this.childNodes[at + 1] ?? null;
    if (child.previousSibling) child.previousSibling.nextSibling = child;
    if (child.nextSibling) child.nextSibling.previousSibling = child;
    this._fixEdges();
  }
  _unlink(child: FNode) {
    if (child.previousSibling) child.previousSibling.nextSibling = child.nextSibling;
    if (child.nextSibling) child.nextSibling.previousSibling = child.previousSibling;
    child.parentNode = null;
    child.parentElement = null;
    this._fixEdges();
  }
  _fixEdges() {
    this.firstChild = this.childNodes[0] ?? null;
    this.lastChild = this.childNodes[this.childNodes.length - 1] ?? null;
  }
}

class FDoc {
  nodeType = 9;
  activeElement: FNode | null = null;
  documentElement: FNode | null = null;
  body: FNode;
  constructor() { this.body = this.createElement('body'); }
  createElement(tag: string) {
    const el = new FNode(this) as FNode;
    el.nodeType = 1;
    el.nodeName = tag.toUpperCase();
    (el as FNode & { tagName?: string }).tagName = tag.toUpperCase();
    return el;
  }
  // SVG-иконки (lucide): React создаёт их через createElementNS
  createElementNS(ns: string, tag: string) {
    const el = this.createElement(tag);
    (el as FNode & { namespaceURI?: string | null }).namespaceURI = ns;
    return el;
  }
  createTextNode(text: string) {
    const t = new FNode(this) as FNode & { _tc?: string };
    t.nodeType = 3;
    t.nodeValue = text;
    t._tc = text;
    return t;
  }
  createComment(text: string) { return this.createTextNode(text); }
  addEventListener() {}
  removeEventListener() {}
}

const fdoc = new FDoc();

// Фейк matchMedia: reduced переключается вручную, dispatch будит живых слушателей
// (подписка usePrefersReducedMotion)
interface FakeMql {
  matches: boolean;
  listeners: Set<() => void>;
  addEventListener: (t: string, cb: () => void) => void;
  removeEventListener: (t: string, cb: () => void) => void;
  dispatch: () => void;
}
const mql: FakeMql = {
  matches: false,
  listeners: new Set(),
  addEventListener: (_t, cb) => { mql.listeners.add(cb); },
  removeEventListener: (_t, cb) => { mql.listeners.delete(cb); },
  dispatch: () => { for (const l of [...mql.listeners]) l(); },
};

const g = globalThis as {
  window?: unknown;
  document?: unknown;
  IS_REACT_ACT_ENVIRONMENT?: boolean;
  requestAnimationFrame?: unknown;
  cancelAnimationFrame?: unknown;
};

beforeEach(() => {
  mql.matches = false;
  g.window = {
    matchMedia: () => mql,
    dispatchEvent: () => true,
    // getActiveElementDeep: `element instanceof window.HTMLIFrameElement`
    HTMLIFrameElement: class {},
  };
  g.document = fdoc;
  g.IS_REACT_ACT_ENVIRONMENT = true;
  g.requestAnimationFrame = (cb: () => void) => setTimeout(cb, 16);
  g.cancelAnimationFrame = (id: number) => clearTimeout(id);
});

afterAll(() => {
  delete g.window;
  delete g.document;
  delete g.IS_REACT_ACT_ENVIRONMENT;
  delete g.requestAnimationFrame;
  delete g.cancelAnimationFrame;
});

// ── Вспомогательное: рендер скелетона + сборка анимаций дерева ──

const PULSE = 'ccs-lm-skeleton 1.4s ease-in-out infinite';

function mount(): { root: Root; container: FNode } {
  const container = fdoc.createElement('div');
  const root = createRoot(container);
  act(() => { root.render(createElement(MediaHistory, { media: null, isMobile: false })); });
  return { root, container };
}

// Все непустые style.animation элементов дерева (в порядке обхода)
function animations(n: FNode, out: string[] = []): string[] {
  if (n.nodeType === 1) {
    const a = n.style.animation;
    if (a) out.push(a);
  }
  for (const c of n.childNodes) animations(c, out);
  return out;
}

describe('скелетон истории: reduced-motion', () => {
  it('без reduced-motion: все 12 элементов трёх скелетонов пульсируют', () => {
    const { root, container } = mount();
    // 3 скелетона × 4 элемента (миниатюра + 3 строки)
    expect(animations(container)).toEqual(Array(12).fill(PULSE));
    act(() => { root.unmount(); });
  });

  it('при reduced-motion: анимации нет ни на одном элементе', () => {
    mql.matches = true;
    const { root, container } = mount();
    expect(animations(container)).toEqual([]);
    act(() => { root.unmount(); });
  });

  it('смена настройки на лету: слушатель matchMedia будит, анимация снимается', () => {
    const { root, container } = mount();
    expect(animations(container)).toHaveLength(12);
    // Настройка ОС «уменьшить движение» включилась: change-событие → ре-рендер
    mql.matches = true;
    act(() => { mql.dispatch(); });
    expect(animations(container)).toEqual([]);
    act(() => { root.unmount(); });
  });
});
