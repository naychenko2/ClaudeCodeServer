// Tooltip (ui) — контракт показа по фокусу (M1, п.48) + позиция плашки (major M1):
//   1. поднимается по focusin (onFocus на обёртке), гаснет по focusout (onBlur);
//   2. проп focused (фокус-цель — предок обёртки) тоже поднимает плашку;
//   3. на тач (canHover=false) focusin — от тапа, и раньше плашка на тач не поднималась
//      вовсе → не плодим нового поведения: kb-состояние не ставится;
//   4. (major M1) плашка — портал position: fixed, зажатая в окно: чистая функция
//      tooltipPlacement держит её в границах на 1280 и 360, рендер-тест — плашка
//      в document.body с position: fixed и координатами из tooltipPlacement.
// Окружение node (без jsdom): fake-DOM + createRoot/act (effects идут). canHover —
// модульная переменная pointer.ts; управляем через vi.mock useCanHover (dispatch
// pointerdown хрупок: listener вешается на window при импорте, а window в тесте fake).
import { createElement } from 'react';
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { beforeEach, describe, expect, it, vi } from 'vitest';

// Управляемый canHover: мок useCanHover возвращает mockState.canHover.
const mockState = { canHover: true };
vi.mock('../../lib/pointer', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/pointer')>();
  return { ...orig, useCanHover: () => mockState.canHover };
});

// === fake-DOM (механизм как в FilterBar.test.ts + focusin/focusout) ===
// vi.hoisted: window/document обязаны быть fake ДО импорта Tooltip (React createRoot
// читает document; pointer.ts здесь мокается, но document нужен для дерева).
// window.innerWidth задаём явно (major M1): Tooltip замеряет viewport для позиции плашки.
const { _doc, FakeNode } = vi.hoisted(() => {
  class _FakeNode {
    nodeType = 1;
    nodeName = 'div';
    childNodes: _FakeNode[] = [];
    parentElement: _FakeNode | null = null;
    _attrs: Record<string, string> = {};
    style: any = { setProperty: () => {} };
    textContent = '';
    getBoundingClientRect() { return { top: 0, left: 0, right: 100, bottom: 30, width: 100, height: 30, x: 0, y: 0, toJSON() { return {}; } }; }
    focus() {}
    blur() {}
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
    _listeners: Record<string, ((e: any) => void)[]> = {};
    get ownerDocument() { return _doc; }
    get tagName() { return this.nodeName.toUpperCase(); }
    addEventListener = (type: string, h: (e: any) => void) => { (this._listeners[type] ??= []).push(h); };
    removeEventListener = (type: string, h: (e: any) => void) => { const a = this._listeners[type]; if (a) { const i = a.indexOf(h); if (i >= 0) a.splice(i, 1); } };
    // focusin/focusout (bubble-версии focus/blur, на которые мапит React onFocus/onBlur):
    // вызываем слушатели КОРНЯ (React делегирует на контейнер), target — фокус-цель.
    fireFocusIn(target: _FakeNode) {
      const ev: any = { type: 'focusin', target, currentTarget: this, bubbles: true, cancelable: false, preventDefault() {}, stopPropagation() {} };
      ev.nativeEvent = ev;
      for (const h of this._listeners['focusin'] ?? []) h(ev);
    }
    fireFocusOut(target: _FakeNode) {
      const ev: any = { type: 'focusout', target, currentTarget: this, bubbles: true, cancelable: false, preventDefault() {}, stopPropagation() {} };
      ev.nativeEvent = ev;
      for (const h of this._listeners['focusout'] ?? []) h(ev);
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
  _doc.defaultView = _win;
  return { _doc, FakeNode: _FakeNode as typeof _FakeNode };
});

import { Tooltip, tooltipPlacement } from './Tooltip';

// Тип инстанса fake-ноды (FakeNode — конструктор).
type FN = InstanceType<typeof FakeNode>;

// Рендер Tooltip с фокусируемым потомком (кнопка) — как в реальном потребителе.
// Возвращает корневой контейнер (на нём слушатели React) и кнопку-потомка.
async function renderTooltip(props: { focused?: boolean } = {}): Promise<{ container: FN; child: FN }> {
  const container = new FakeNode();
  const root = createRoot(container);
  await act(async () => {
    root.render(createElement(Tooltip, { content: 'Подсказка-контент', ...props },
      createElement('button', { type: 'button', 'data-testid': 'anchor' }, 'Якорь')));
  });
  const find = (n: FN): FN | null => {
    if (n._attrs['data-testid'] === 'anchor') return n;
    for (const c of n.childNodes) { const r = find(c); if (r) return r; }
    return null;
  };
  const child = find(container);
  expect(child, 'кнопка-потомок Tooltip не найдена').not.toBeNull();
  return { container, child: child! };
}

// Плашка в DOM? role="tooltip". (major M1) Плашка — ПОРТАЛ в document.body, не в
// контейнер рендера: ищем в _doc.body (свежий на каждый тест — beforeEach).
function tooltipInBody(): FN | null {
  const walk = (n: FN): FN | null => {
    if (n._attrs['role'] === 'tooltip') return n;
    for (const c of n.childNodes) { const r = walk(c); if (r) return r; }
    return null;
  };
  return walk(_doc.body as FN);
}

beforeEach(() => {
  _doc.body = new FakeNode();
  mockState.canHover = true; // desktop по умолчанию
  (globalThis as any).window.innerWidth = 1024;
});

describe('Tooltip: контракт показа по фокусу (M1, п.48)', () => {
  it('focusin (canHover=true) поднимает плашку, focusout гасит', async () => {
    mockState.canHover = true;
    const { container, child } = await renderTooltip();
    expect(tooltipInBody(), 'в покое плашки нет').toBeNull();
    await act(async () => { container.fireFocusIn(child); });
    expect(tooltipInBody(), 'focusin не поднял плашку').not.toBeNull();
    await act(async () => { container.fireFocusOut(child); });
    expect(tooltipInBody(), 'focusout не погасил плашку').toBeNull();
  });

  it('проп focused=true поднимает плашку (фокус-цель — предок обёртки)', async () => {
    mockState.canHover = true;
    const { container } = await renderTooltip({ focused: true });
    expect(tooltipInBody(), 'focused=true не поднял плашку').not.toBeNull();
  });

  it('тач (canHover=false): focusin от тапа НЕ поднимает плашку (новое поведение не плодим)', async () => {
    mockState.canHover = false;
    const { container, child } = await renderTooltip();
    await act(async () => { container.fireFocusIn(child); });
    expect(tooltipInBody(), 'на тач focusin поднял плашку (раньше её не было)').toBeNull();
  });
});

// === major M1: позиция плашки — чистая функция + рендер-тест ===
// tooltipPlacement держит плашку в границах окна (left >= pad, left + maxWidth <= vw - pad)
// на 1280 и 360. Мутация «убрать зажим по краю» (Math.max(pad, …) / правый сдвиг) красит.
describe('Tooltip: позиция плашки в границах окна (major M1)', () => {
  // Якорь у ПРАВОГО края (x 1158, как в постановке: плашка 379 уезжала за 1280).
  const rightEdgeAnchor = { left: 1158, right: 1200, bottom: 120 };

  it('1280: плашка у правого края не выходит за окно (align=start)', () => {
    const p = tooltipPlacement(rightEdgeAnchor, 1280, 'start', 7, 8);
    expect(p.left, 'левый край за левую кромку').toBeGreaterThanOrEqual(8);
    expect(p.left + p.maxWidth, 'правый край за правую кромку окна').toBeLessThanOrEqual(1280 - 8);
    expect(p.top, 'плашка под якорем').toBe(rightEdgeAnchor.bottom + 7);
  });

  it('360: узкое окно — плашка в границах (align=start)', () => {
    const anchor = { left: 300, right: 340, bottom: 80 };
    const p = tooltipPlacement(anchor, 360, 'start', 7, 8);
    expect(p.left).toBeGreaterThanOrEqual(8);
    expect(p.left + p.maxWidth).toBeLessThanOrEqual(360 - 8);
  });

  it('align=end: правый край плашки у правого края якоря, в границах окна', () => {
    const anchor = { left: 100, right: 140, bottom: 50 };
    const p = tooltipPlacement(anchor, 1280, 'end', 7, 8);
    expect(p.left).toBeGreaterThanOrEqual(8);
    expect(p.left + p.maxWidth).toBeLessThanOrEqual(1280 - 8);
  });

  it('рендер: плашка — портал position:fixed в document.body, координаты из tooltipPlacement', async () => {
    mockState.canHover = true;
    (globalThis as any).window.innerWidth = 1280;
    const { container, child } = await renderTooltip();
    // Якорь Tooltip — обёртка-span (anchorRef), НЕ кнопка-потомок: override rect СПАНА
    // (child.parentNode) — от него Tooltip меряет позицию.
    const span = (child as unknown as { parentNode: { getBoundingClientRect: () => unknown } }).parentNode;
    span.getBoundingClientRect =
      () => ({ top: 90, left: 1158, right: 1200, bottom: 120, width: 42, height: 30, x: 1158, y: 90, toJSON() { return {}; } });
    await act(async () => { container.fireFocusIn(child); });
    const plaque = tooltipInBody();
    expect(plaque, 'фокус не поднял плашку-портал').not.toBeNull();
    const style = (plaque! as unknown as { style: Record<string, string> }).style;
    expect(style.position, 'плашка не position:fixed').toBe('fixed');
    // Координаты совпадают с чистой функцией (тот же якорь, тот же viewport).
    const expected = tooltipPlacement({ left: 1158, right: 1200, bottom: 120 }, 1280, 'start', 7, 8);
    expect(style.left, 'left не совпадает с tooltipPlacement').toBe(`${expected.left}px`);
    expect(style.maxWidth, 'maxWidth не совпадает с tooltipPlacement').toBe(`${expected.maxWidth}px`);
  });
});
