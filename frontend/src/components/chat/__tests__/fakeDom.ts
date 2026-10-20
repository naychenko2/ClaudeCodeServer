// Минимальный фейк-DOM для createRoot в окружении node (без jsdom). React в клиентском
// рендере вызывает только то, что нужно для монтирования и делегирования событий: nodeType,
// связи родитель/потомок, add/removeEventListener, style, атрибуты, текст. Паттерн — как в
// gpuCard.test.ts; здесь — attribute-based поиск узлов (data-testid/aria-label), а не по тексту.
import { act, type ReactElement } from 'react';
import { createRoot, type Root } from 'react-dom/client';

class FNode {
  nodeType = 3 as 1 | 3;
  nodeName = '#text';
  nodeValue = '';
  id = '';
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
  // children — только элементы (Array.from(probe.children) в WorkspacePage); childNodes — все
  get children(): FNode[] { return this.childNodes.filter(c => c.nodeType === 1); }
  firstChild: FNode | null = null;
  lastChild: FNode | null = null;
  nextSibling: FNode | null = null;
  previousSibling: FNode | null = null;
  ownerDocument: FDoc;
  // style — как в реальном DOM: объект с setProperty/removeProperty (chatGutter и др.
  // пишут CSS-переменные через них); plain-объект без методов роняет useLayoutEffect.
  style: Record<string, string> & { setProperty: (k: string, v: string) => void; removeProperty: (k: string) => void } = {
    setProperty: (k: string, v: string) => { (this.style as Record<string, string>)[k] = v; },
    removeProperty: (k: string) => { delete (this.style as Record<string, string>)[k]; },
  } as any;
  attributes: Record<string, string> = {};
  listeners: Record<string, Set<unknown>> = {};
  // :focus-visible (M1'): управляемое тестом — true = фокус пришёл с клавиатуры (Tab).
  // matches(':focus-visible') читает его; прочие селекторы — false (как в реальном DOM
  // для простого div: :focus-visible не совпадает без фокуса с клавиатуры).
  focusVisible = false;
  matches(selector: string): boolean {
    return selector === ':focus-visible' ? this.focusVisible : false;
  }

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
  scrollIntoView() {}
  // querySelector: поддерживает атрибутный селектор [attr="value"] (PipelineFlow ищет строку
  // шага data-step-ref внутри своего корня); прочие формы — null (как раньше).
  querySelector(sel: string): FNode | null {
    const m = sel.match(/^\[([a-zA-Z-]+)="([^"]*)"\]$/);
    if (!m) return null;
    const [, attr, value] = m;
    let hit: FNode | null = null;
    const walk = (x: FNode) => {
      if (hit) return;
      if (x.nodeType === 1 && x.getAttribute(attr) === value) { hit = x; return; }
      for (const c of x.childNodes) walk(c);
    };
    walk(this);
    return hit;
  }
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
  head: FNode;
  constructor() { this.body = this.createElement('body'); this.head = this.createElement('head'); this.body.appendChild(this.head); }
  // getElementById: по свойству id (RowNotice ставит el.id = 'cc-rownotice-style') или атрибуту
  getElementById(id: string): FNode | null {
    let hit: FNode | null = null;
    const walk = (x: FNode) => {
      if (hit) return;
      if (x.nodeType === 1 && (x.id === id || x.getAttribute('id') === id)) { hit = x; return; }
      for (const c of x.childNodes) walk(c);
    };
    walk(this.body);
    return hit;
  }
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

// ── Глобальные: window (matchMedia для useIsMobile, dispatchEvent) + document (порталы) ──
let installed = false;
export function installFakeDom() {
  if (installed) return;
  installed = true;
  const fdoc = new FDoc();
  const fwindow = {
    // window.event — react-dom читает его в отложенном колбэке scheduler; undefined не бросает
    event: undefined,
    dispatchEvent: () => true,
    matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
    HTMLIFrameElement: class {},
  };
  (globalThis as { window?: unknown }).window = fwindow;
  (globalThis as { document?: unknown }).document = fdoc;
  (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
}
export function uninstallFakeDom() {
  if (!installed) return;
  installed = false;
  // window и document НЕ удаляем: асинхронный колбэк scheduler (window.event) может сработать
  // после afterAll, и «window is not defined» упал бы unhandled. Worker vitest изолирован — утечки нет.
  delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT;
}

// ── Рендер + поиск узлов ──
export function mount(node: ReactElement): { root: Root; container: FNode } {
  const doc = (globalThis as { document: FDoc }).document;
  const container = doc.createElement('div');
  const root = createRoot(container);
  act(() => { root.render(node); });
  return { root, container };
}

// React держит props на узле под ключом __reactProps$<random>; возвращаем его (или null)
export function reactProps(n: FNode): Record<string, unknown> | null {
  const key = Object.keys(n).find(k => k.startsWith('__reactProps$'));
  return key ? (n as unknown as Record<string, unknown>)[key] as Record<string, unknown> : null;
}

// Узел по data-атрибуту (data-testid, aria-label и т. п.); обход дерева от контейнера
export function findNodeByAttr(container: FNode, attr: string, value: string): FNode | null {
  let hit: FNode | null = null;
  const walk = (x: FNode) => {
    if (hit) return;
    if (x.nodeType === 1 && x.getAttribute(attr) === value) { hit = x; return; }
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
  return hit;
}

// Все текстовые ноды дерева (для проверок подписей)
export function textNodes(n: FNode, out: string[] = []): string[] {
  if (n.nodeType === 3) out.push(n.nodeValue);
  for (const c of n.childNodes) textNodes(c, out);
  return out;
}
