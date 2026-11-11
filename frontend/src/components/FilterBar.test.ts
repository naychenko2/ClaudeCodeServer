// FilterBar (FS-FL2b): в режиме «Фичи» оси «Фичи · что показывать» (featureKinds) больше нет —
// список фич раскладывается секциями, фильтра по состоянию нет.
//   1. Секции и чипов kind в шторке нет; сохранённый featureKinds не читается и не рисуется.
//   2. Сводка в title триггера не упоминает ось («Показаны…»).
//   3. Футер на 360 в режиме «Фичи» — «Показать N фич», N = все рисующиеся фичи (chatCount > 0).
// Окружение node (без jsdom): fake-DOM (как в SessionList.test.ts) + createRoot/act
// (effects идут: открытие шторки). Шторка — в document.body (портал); корень React —
// container: клик «прицеливается» через container.fireClick(узел) — делегат React на
// контейнере находит fiber по target, портал это не ломает.

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
import { ALL_FEATURE_KINDS, type ChatFilters, type FeatureKind } from '../lib/chatFilters';
import { FEATURE_KIND_LABEL } from '../lib/chatGroups';
import type { FeatureIndexFeature } from '../types/featureIndex';
import type { Session, Persona } from '../types';

// === фикстуры ===
const feat = (id: string, bucket: FeatureIndexFeature['bucket'], kind: FeatureKind | null, chatCount = 1): FeatureIndexFeature => ({
  featureId: id, title: `Фича ${id}`, projectId: 'p1', bucket,
  attention: null, rail: [], chips: [],
  stepsAccepted: 0, stepsTotal: 0, loops: 0, now0: null, lastActivityAt: null, summary: null,
  keySessionId: null, keyArchived: false, keyProjectId: null, chatCount,
  kind,
} as FeatureIndexFeature);

const baseFilters: ChatFilters = {
  origins: ['manual', 'task', 'automation'],
  statuses: ['active', 'waiting', 'error'],
  personaId: null, only: [], search: '',
  groupBy: 'features', sortOrder: 'newest', hierarchy: true, archivedOnly: false,
  featureKinds: [],
};

// Узел по префиксу title (триггер: «Фильтры и поиск» без фильтров, «Фильтры: …» с ними).
function findTitle(n: _FakeNode, prefix: string): _FakeNode | null {
  if ((n._attrs['title'] ?? '').startsWith(prefix)) return n;
  for (const c of n.childNodes) { const r = findTitle(c, prefix); if (r) return r; }
  return null;
}
// Рендер FilterBar (мобила) + открытие шторки кликом по триггеру. Возвращает body шторки,
// сам триггер (для проверки его title — сводки) и click(узел) — «прицельный» клик через
// корень React (container), см. шапку файла.
async function renderOpenBar(
  features: FeatureIndexFeature[],
  filters: ChatFilters = baseFilters,
  patch: (p: Partial<ChatFilters>) => void = () => {},
): Promise<{ body: _FakeNode; trigger: _FakeNode; click: (n: _FakeNode) => Promise<void> }> {
  const container = new _FakeNode();
  const root = createRoot(container);
  await act(async () => {
    root.render(createElement(FilterBar, {
      sessions: [] as Session[], filters, patch, allPersonas: [] as Persona[],
      hiddenCount: 0, isMobile: true, features,
    }));
  });
  const trigger = findTitle(container, 'Фильтры');
  expect(trigger, 'триггер FilterBar не найден').not.toBeNull();
  await act(async () => { container.fireClick(trigger!); });
  return {
    body: _doc.body,
    trigger: trigger!,
    // Клик по содержимому шторки: React 18 навешивает делегаты и на корень, и на контейнер
    // портала (premountPortal → listenToAllSupportedEvents), поэтому целевой узел портальной
    // шторки «доходит» через _doc.body, а не через container (триггер же — в корне).
    click: async (n: _FakeNode) => { await act(async () => { _doc.body.fireClick(n); }); },
  };
}
// Узел, чей textContent содержит текст (любой тип узла: в fake-DOM React кладёт текст
// в textContent элемента, не только в text-ноду — textOf по nodeType===3 его пропускает).
function findByText(n: _FakeNode, text: string): _FakeNode | null {
  if (n.textContent && n.textContent.includes(text)) return n;
  for (const c of n.childNodes) { const r = findByText(c, text); if (r) return r; }
  return null;
}
// Текст поддерева узла: в fake-DOM React кладёт одиночный текстовый ребёнок в
// textContent самого элемента (setTextContent), а при нескольких детях — в text-ноду
// (nodeType=3), и textContent элемента остаётся ''. Чип = span-точка + подпись + Count,
// поэтому его текст собирается обходом, а не читается из textContent кнопки.
function textOf(n: _FakeNode): string {
  let t = n.textContent ?? '';
  for (const c of n.childNodes) t += textOf(c);
  return t;
}
// Подписи кнопок-чипов шторки, совпавшие с названиями состояний фич (оси в шторке быть не должно).
function collectFeatureChips(n: _FakeNode): string[] {
  const labels = ALL_FEATURE_KINDS.map(k => FEATURE_KIND_LABEL[k]);
  const out: string[] = [];
  const walk = (x: _FakeNode) => {
    if (x.nodeName === 'button') {
      const hit = labels.find(l => textOf(x).includes(l));
      if (hit) out.push(hit);
    }
    for (const c of x.childNodes) walk(c);
  };
  walk(n);
  return out;
}

beforeEach(() => {
  _doc.body = new _FakeNode();
});

describe('FilterBar: оси «Фичи · что показывать» нет (FS-FL2b)', () => {
  const features = [
    feat('w1', 'running', 'waiting_you'), feat('e1', 'running', 'error'), feat('r1', 'running', 'running'),
    feat('d1', 'draft', 'draft'), feat('m1', 'merged', 'merged'), feat('c1', 'closed', 'closed'),
  ];

  it('в режиме «Фичи» шторка без секции оси, чипов kind и «Сбросить», остальные секции на месте', async () => {
    const { body } = await renderOpenBar(features);
    expect(findByText(body, 'Фичи · что показывать'), 'секция оси осталась').toBeNull();
    expect(collectFeatureChips(body), 'в шторке остались чипы состояний фич').toEqual([]);
    // Прочие секции режима не тронуты
    expect(findByText(body, 'Статус'), 'секция «Статус» пропала').not.toBeNull();
  });

  it('сохранённый featureKinds не читается: ни секции, ни кнопки «Сбросить» оси', async () => {
    const { body } = await renderOpenBar(features, { ...baseFilters, featureKinds: ['waiting_you'] });
    expect(findByText(body, 'Фичи · что показывать')).toBeNull();
    expect(collectFeatureChips(body)).toEqual([]);
  });

  it('сводка title триггера не упоминает ось («Показаны…», «кроме»)', async () => {
    const { trigger } = await renderOpenBar(features, { ...baseFilters, featureKinds: ['waiting_you'] });
    const title = trigger.getAttribute('title') ?? '';
    expect(title.includes('Показаны'), `в сводке осталась ось: ${title}`).toBe(false);
    expect(title.includes('кроме'), `в сводке осталась ось: ${title}`).toBe(false);
  });
});

describe('FilterBar: футер «Показать N фич» (360, режим «Фичи»)', () => {
  it('360 + groupBy=features → «Показать 3 фич»: N = все рисующиеся, закрытые тоже; не «Готово»', async () => {
    const features = [feat('f1', 'running', 'running'), feat('f2', 'running', 'running'), feat('c1', 'closed', 'closed')];
    const { body } = await renderOpenBar(features);
    expect(findByText(body, 'Показать 3 фич'), 'футер «Показать 3 фич» отсутствует').not.toBeNull();
    expect(findByText(body, 'Готово'), 'футер остался «Готово»').toBeNull();
  });

  // Сохранённая ось не влияет на N. Мутация: вернуть в футер отбор по featureKinds → «Показать 1 фич».
  it('сохранённый featureKinds=[closed] на N не влияет', async () => {
    const features = [feat('f1', 'running', 'running'), feat('f2', 'running', 'running'), feat('c1', 'closed', 'closed')];
    const { body } = await renderOpenBar(features, { ...baseFilters, featureKinds: ['closed'] });
    expect(findByText(body, 'Показать 3 фич'), 'N считается по сохранённой оси').not.toBeNull();
  });

  // Мутация: убрать `f.chatCount > 0` из футера → «Показать 2 фич».
  it('фича без чатов в счёт футера не входит: «Показать 1 фич», не «2»', async () => {
    const features = [feat('z0', 'running', 'running', 0), feat('f1', 'running', 'running', 1)];
    const { body } = await renderOpenBar(features);
    expect(findByText(body, 'Показать 1 фич'), 'футер не пересчитал по chatCount').not.toBeNull();
    expect(findByText(body, 'Показать 2 фич'), 'фича без чатов попала в счёт').toBeNull();
  });

  it('дефолт (groupBy=days, 360) → футер «Готово», не «Показать N фич»', async () => {
    const { body } = await renderOpenBar([feat('f1', 'running', 'running')], { ...baseFilters, groupBy: 'days' });
    expect(findByText(body, 'Готово'), 'футер «Готово» отсутствует в режиме «Дни»').not.toBeNull();
    expect(findByText(body, 'Показать 1 фич'), '«Показать N фич» в режиме «Дни»').toBeNull();
  });
});
