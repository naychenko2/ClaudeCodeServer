// FilterBar (волна 2 «Обзор фич», п.2): два минора ревью.
//   1. Футер на 360 в режиме «Фичи» — «Показать N фич» (N = открытые фичи индекса,
//      bucket != closed), не «Готово» (общий подвал «Чатов»/«Фичи» переключается
//      только по groupBy + isMobile).
//   2. Чип-фильтр фичи с нулевым счётчиком — aria-disabled (состояние слышит
//      скринридер, а не только прозрачность); клик не блокируется (aria-disabled,
//      не disabled).
// Окружение node (без jsdom): fake-DOM (как в SessionList.test.ts) + createRoot/act
// (effects идут: открытие шторки). Шторка — в document.body (портal).

import { createElement } from 'react';
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { beforeEach, describe, expect, it, vi } from 'vitest';

// === fake-DOM (п.66): vi.hoisted — window/document обязаны быть fake ДО импорта FilterBar.
// ESM-импорты выполняются РАНЬШЕ тела модуля: раньше setup стоял в теле, и FilterBar
// импортировался без document/window (работало, т.к. React читает document лениво, но
// любое обращение к document на уровне модуля в FilterBar.tsx уронило бы файл).
// Механизм — как в Tooltip.test.ts. ===
const { _doc, _FakeNode } = vi.hoisted(() => {
  class _FakeNode {
  nodeType = 1;
  nodeName = 'div';
  childNodes: _FakeNode[] = [];
  parentElement: _FakeNode | null = null;
  _attrs: Record<string, string> = {};
  style: any = { setProperty: () => {} };
  textContent = '';
  // dataset — для themeMode (document.documentElement.dataset.theme) на уровне модуля.
  dataset: Record<string, string> = {};
  getBoundingClientRect() { return { top: 0, left: 0, right: 100, bottom: 30, width: 100, height: 30, x: 0, y: 0, toJSON() { return {}; } }; }
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
  // head — для инъекции <style> focus-ring'а (IconButton/Chip/… на уровне модуля):
  // vi.hoisted ставит fake-DOM ДО импортов, поэтому document.head обязана существовать.
  head: new _FakeNode(),
  documentElement: new _FakeNode(),
  getElementById: () => null,
  addEventListener: () => {},
  removeEventListener: () => {},
};
const _win: any = {
  document: _doc, defaultView: null,
  HTMLIFrameElement: _FakeIframe, HTMLInputElement: class {}, HTMLSelectElement: class {}, HTMLTextAreaElement: class {},
  addEventListener: () => {}, removeEventListener: () => {}, location: { href: '' }, navigator: { userAgent: 'test' },
  setTimeout: () => 0, clearTimeout: () => {},
  matchMedia: (q: string) => ({ matches: false, media: q, addEventListener: () => {}, removeEventListener: () => {}, onchange: null, dispatchEvent: () => true }),
};
(globalThis as any).document = _doc;
(globalThis as any).window = _win;
_doc.defaultView = _win;
return { _doc, _FakeNode: _FakeNode as typeof _FakeNode };
});

import { FilterBar } from './FilterBar';
import { featureChipCounts } from '../lib/chatGroups';
import type { ChatFilters } from '../lib/chatFilters';
import type { FeatureIndexFeature } from '../types/featureIndex';
import type { Session, Persona } from '../types';

// === фикстуры ===
const feat = (id: string, bucket: 'running' | 'closed', chips: string[]): FeatureIndexFeature => ({
  featureId: id, title: `Фича ${id}`, projectId: 'p1', bucket,
  attention: null, rail: [], chips: chips as FeatureIndexFeature['chips'],
  stepsAccepted: 0, stepsTotal: 0, loops: 0, now0: null, lastActivityAt: null, summary: null,
  keySessionId: null, keyArchived: false, keyProjectId: null, chatCount: 1,
} as FeatureIndexFeature);

const baseFilters: ChatFilters = {
  origins: ['manual', 'task', 'automation'],
  statuses: ['active', 'waiting', 'error'],
  personaId: null, only: [], search: '',
  groupBy: 'features', sortOrder: 'newest', hierarchy: true, archivedOnly: false,
  featureChips: [], showClosed: false,
};

// Рендер FilterBar (мобила) + открытие шторки кликом по триггеру; возвращает body.
async function renderOpenBar(features: FeatureIndexFeature[], filters: ChatFilters = baseFilters): Promise<_FakeNode> {
  const container = new _FakeNode();
  const root = createRoot(container);
  await act(async () => {
    root.render(createElement(FilterBar, {
      sessions: [] as Session[], filters, patch: () => {}, allPersonas: [] as Persona[],
      hiddenCount: 0, isMobile: true, features,
    }));
  });
  // Триггер (title «Фильтры и поиск», фильтры по дефолту) → клик открывает шторку.
  const findTitle = (n: _FakeNode, title: string): _FakeNode | null => {
    if (n._attrs['title'] === title) return n;
    for (const c of n.childNodes) { const r = findTitle(c, title); if (r) return r; }
    return null;
  };
  const trigger = findTitle(container, 'Фильтры и поиск');
  expect(trigger, 'триггер FilterBar не найден').not.toBeNull();
  await act(async () => { container.fireClick(trigger!); });
  return _doc.body;
}
// Узел по aria-атрибуту (обход поддерева).
function findByAria(n: _FakeNode, attr: string, value: string): _FakeNode | null {
  if (n._attrs[attr] === value) return n;
  for (const c of n.childNodes) { const r = findByAria(c, attr, value); if (r) return r; }
  return null;
}
// Узел, чей textContent содержит текст (любой тип узла: в fake-DOM React кладёт текст
// в textContent элемента, не только в text-ноду — textOf по nodeType===3 его пропускает).
function findByText(n: _FakeNode, text: string): _FakeNode | null {
  if (n.textContent && n.textContent.includes(text)) return n;
  for (const c of n.childNodes) { const r = findByText(c, text); if (r) return r; }
  return null;
}

beforeEach(() => {
  _doc.body = new _FakeNode();
});

describe('FilterBar: п.2 футер «Показать N фич» (360, режим «Фичи»)', () => {
  it('360 + groupBy=features → футер «Показать N фич» (N = открытые фичи), не «Готово»', async () => {
    // 2 открытые (running) + 1 закрытая → N = 2.
    const features = [feat('f1', 'running', ['open', 'waiting']), feat('f2', 'running', ['open', 'waiting']), feat('c1', 'closed', ['closed'])];
    const body = await renderOpenBar(features);
    // Футер-кнопка «Показать 2 фич» (N = открытые фичи) — на месте, «Готово» нет.
    expect(findByText(body, 'Показать 2 фич'), 'футер «Показать 2 фич» отсутствует').not.toBeNull();
    expect(findByText(body, 'Готово'), 'футер остался «Готово» (не переключился на режим фич)').toBeNull();
    // N = открытые фичи индекса (bucket != closed) = 2 (закрытая не входит).
    expect(features.filter(f => f.bucket !== 'closed').length).toBe(2);
  });

  it('дефолт (groupBy=days, 360) → футер «Готово», не «Показать N фич»', async () => {
    const features = [feat('f1', 'running', ['open', 'waiting'])];
    const body = await renderOpenBar(features, { ...baseFilters, groupBy: 'days' });
    expect(findByText(body, 'Готово'), 'футер «Готово» отсутствует в режиме «Дни»').not.toBeNull();
    // «Показать 1 фич» — футер режима «Фичи»; в «Дни» его нет (ищет точную фразу,
    // не «Показать», чтобы не словить заголовок секции «Показать только»).
    expect(findByText(body, 'Показать 1 фич'), '«Показать N фич» в режиме «Дни» (только в «Фичи»)').toBeNull();
  });
});

describe('FilterBar: п.2 aria-disabled у нулевого чипа-фильтра фич', () => {
  it('чип с нулевым счётчиком — aria-disabled="true"; с ненулевым — нет', async () => {
    // waiting=2 (f1,f2), stopped/merge/running=0, closed=1 (c1).
    const features = [feat('f1', 'running', ['open', 'waiting']), feat('f2', 'running', ['open', 'waiting']), feat('c1', 'closed', ['closed'])];
    const counts = featureChipCounts(features);
    expect(counts.waiting).toBe(2);
    expect(counts.stopped).toBe(0);

    const body = await renderOpenBar(features);
    // Нулевой чип (stopped) — aria-disabled="true" (состояние слышит скринридер).
    const disabled = findByAria(body, 'aria-disabled', 'true');
    expect(disabled, 'нулевой чип без aria-disabled="true"').not.toBeNull();
    // Ненулевой чип (waiting, count=2) — aria-disabled не задан.
    // aria-disabled="false" не используется (undefined) → таких узлов нет.
    expect(findByAria(body, 'aria-disabled', 'false'), 'aria-disabled="false" не ожидается (Chip ставит undefined)').toBeNull();
  });
});
