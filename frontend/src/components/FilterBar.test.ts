// FilterBar (Ф-2.1): секция «Фичи · что показывать» — ось featureKinds вместо чипов
// «что с ними» + тумблера «Показывать закрытые».
//   1. 9 чипов в порядке ALL_FEATURE_KINDS; тумблера (role="switch"/текста «Показывать
//      закрытые») нет.
//   2. Клик по единственному выбранному чипу → patch({featureKinds: []}) (снятие последнего
//      = умолчание). Клик по нулевому НЕ выбранному чипу → patch не вызван, чип aria-disabled.
//      Нулевой, но выбранный (умолчание «видит все открытые») — остаётся кликабельным.
//   3. R-Ф1.3(а): индекс без kind (старый ответ) — чипы без чисел и кликабельны: нулевые
//      счётчики неотличимы от «нет фич».
//   4. Футер на 360 в режиме «Фичи» — «Показать N фич» (поведение прежнее, регрессия).
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
const feat = (id: string, bucket: FeatureIndexFeature['bucket'], kind: FeatureKind | null): FeatureIndexFeature => ({
  featureId: id, title: `Фича ${id}`, projectId: 'p1', bucket,
  attention: null, rail: [], chips: [],
  stepsAccepted: 0, stepsTotal: 0, loops: 0, now0: null, lastActivityAt: null, summary: null,
  keySessionId: null, keyArchived: false, keyProjectId: null, chatCount: 1,
  kind,
} as FeatureIndexFeature);

const baseFilters: ChatFilters = {
  origins: ['manual', 'task', 'automation'],
  statuses: ['active', 'waiting', 'error'],
  personaId: null, only: [], search: '',
  groupBy: 'features', sortOrder: 'newest', hierarchy: true, archivedOnly: false,
  featureKinds: [], featureChips: [], showClosed: false,
};

// Рендер FilterBar (мобила) + открытие шторки кликом по триггеру. Возвращает body шторки
// и click(узел) — «прицельный» клик через корень React (container), см. шапку файла.
async function renderOpenBar(
  features: FeatureIndexFeature[],
  filters: ChatFilters = baseFilters,
  patch: (p: Partial<ChatFilters>) => void = () => {},
): Promise<{ body: _FakeNode; click: (n: _FakeNode) => Promise<void> }> {
  const container = new _FakeNode();
  const root = createRoot(container);
  await act(async () => {
    root.render(createElement(FilterBar, {
      sessions: [] as Session[], filters, patch, allPersonas: [] as Persona[],
      hiddenCount: 0, isMobile: true, features,
    }));
  });
  // Триггер: title «Фильтры и поиск» (без фильтров) или «Фильтры: …» (с фильтрами) — ищем по префиксу.
  const findTitle = (n: _FakeNode, prefix: string): _FakeNode | null => {
    if ((n._attrs['title'] ?? '').startsWith(prefix)) return n;
    for (const c of n.childNodes) { const r = findTitle(c, prefix); if (r) return r; }
    return null;
  };
  const trigger = findTitle(container, 'Фильтры');
  expect(trigger, 'триггер FilterBar не найден').not.toBeNull();
  await act(async () => { container.fireClick(trigger!); });
  return {
    body: _doc.body,
    // Клик по содержимому шторки: React 18 навешивает делегаты и на корень, и на контейнер
    // портала (premountPortal → listenToAllSupportedEvents), поэтому целевой узел портальной
    // шторки «доходит» через _doc.body, а не через container (триггер же — в корне).
    click: async (n: _FakeNode) => { await act(async () => { _doc.body.fireClick(n); }); },
  };
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
// Текст поддерева узла: в fake-DOM React кладёт одиночный текстовый ребёнок в
// textContent самого элемента (setTextContent), а при нескольких детях — в text-ноду
// (nodeType=3), и textContent элемента остаётся ''. Чип = span-точка + подпись + Count,
// поэтому его текст собирается обходом, а не читается из textContent кнопки.
function textOf(n: _FakeNode): string {
  let t = n.textContent ?? '';
  for (const c of n.childNodes) t += textOf(c);
  return t;
}
// Кнопка-чип по подписи: только <button> (подсказка «Закрытые встают секцией…» — div,
// не чип; триггер — кнопка без текста).
function findChipButton(n: _FakeNode, label: string): _FakeNode | null {
  if (n.nodeName === 'button' && textOf(n).includes(label)) return n;
  for (const c of n.childNodes) { const r = findChipButton(c, label); if (r) return r; }
  return null;
}
// Последовательность подписей оси в кнопках-чипах, в порядке обхода DOM (document order).
// Каждая подпись — подстрока ровно одного чипа (составляют FEATURE_KIND_LABEL).
function collectFeatureChips(n: _FakeNode): string[] {
  const labels = ALL_FEATURE_KINDS.map(k => FEATURE_KIND_LABEL[k]);
  const out: string[] = [];
  const walk = (x: _FakeNode) => {
    if (x.nodeName === 'button') {
      const t = textOf(x);
      if (t) {
        const hit = labels.find(l => t.includes(l));
        if (hit) out.push(hit);
      }
    }
    for (const c of x.childNodes) walk(c);
  };
  walk(n);
  return out;
}

beforeEach(() => {
  _doc.body = new _FakeNode();
});

describe('FilterBar: «Фичи · что показывать» — 9 чипов, тумблера нет (Ф-2.1 c1)', () => {
  it('рендерит 9 чипов в порядке ALL_FEATURE_KINDS; role="switch" и текста «Показывать закрытые» нет', async () => {
    const features = [
      feat('w1', 'running', 'waiting_you'), feat('e1', 'running', 'error'), feat('r1', 'running', 'running'),
      feat('q1', 'running', 'queued'), feat('p1', 'running', 'paused'), feat('d1', 'draft', 'draft'),
      feat('i1', 'idle', 'idle'), feat('m1', 'merged', 'merged'), feat('c1', 'closed', 'closed'),
    ];
    const { body } = await renderOpenBar(features);
    expect(findByText(body, 'Фичи · что показывать'), 'заголовка секции нет').not.toBeNull();
    // Старый тумблер удалён: ни role="switch", ни его текста
    expect(findByAria(body, 'role', 'switch'), 'тумблер «Показывать закрытые» остался (role="switch")').toBeNull();
    expect(findByText(body, 'Показывать закрытые'), 'текст старого тумблера остался').toBeNull();
    // Порядок обхода кнопок-чипов = порядок оси: восемь открытых, «Закрытые» последней
    expect(collectFeatureChips(body)).toEqual(ALL_FEATURE_KINDS.map(k => FEATURE_KIND_LABEL[k]));
  });
});

describe('FilterBar: клик по чипам оси (Ф-2.1 c2)', () => {
  it('снятие единственного выбранного чипа → patch({featureKinds: []}) — умолчание', async () => {
    const features = [feat('w1', 'running', 'waiting_you')];
    const calls: Partial<ChatFilters>[] = [];
    const { body, click } = await renderOpenBar(features, { ...baseFilters, featureKinds: ['waiting_you'] }, p => calls.push(p));
    const chip = findChipButton(body, FEATURE_KIND_LABEL.waiting_you);
    expect(chip, 'чип «Ждут вас» не найден').not.toBeNull();
    await click(chip!);
    expect(calls.filter(c => c.featureKinds !== undefined), 'патч по оси не тот').toEqual([{ featureKinds: [] }]);
  });

  it('нулевой и НЕ выбранный чип: aria-disabled="true", клик игнорируется (патча нет)', async () => {
    // closed: 0 фич, в умолчании не выбран → приглушён и глух к клику
    const features = [feat('w1', 'running', 'waiting_you')];
    const calls: Partial<ChatFilters>[] = [];
    const { body, click } = await renderOpenBar(features, baseFilters, p => calls.push(p));
    const chip = findChipButton(body, FEATURE_KIND_LABEL.closed);
    expect(chip, 'чип «Закрытые» не найден').not.toBeNull();
    expect(chip!.getAttribute('aria-disabled'), 'нулевой не выбранный чип без aria-disabled="true"').toBe('true');
    await click(chip!);
    expect(calls.filter(c => c.featureKinds !== undefined), 'клик по нулевому чипу изменил ось').toEqual([]);
  });

  it('нулевой, но выбранный умолчанием чип кликабельен: снятие → ось без него', async () => {
    // error: 0 фич, но в умолчании все открытые «выбраны» — такой чип можно снять
    // (записка п. 3: «выбранный чип с нулём остаётся активным, чтобы его можно было снять»)
    const features = [feat('w1', 'running', 'waiting_you')];
    const calls: Partial<ChatFilters>[] = [];
    const { body, click } = await renderOpenBar(features, baseFilters, p => calls.push(p));
    const chip = findChipButton(body, FEATURE_KIND_LABEL.error);
    expect(chip, 'чип «Остановлены» не найден').not.toBeNull();
    expect(chip!.getAttribute('aria-disabled'), 'выбранный нулевой чип не должен быть aria-disabled').not.toBe('true');
    await click(chip!);
    expect(calls.find(c => c.featureKinds !== undefined)?.featureKinds).toEqual(
      ALL_FEATURE_KINDS.filter(k => k !== 'closed' && k !== 'error'),
    );
  });
});

describe('FilterBar: индекс без kind (R-Ф1.3(а))', () => {
  it('ни у одной фичи нет kind: чипы без чисел, не приглушены, кликабельны', async () => {
    const features = [feat('x1', 'running', null)];
    const calls: Partial<ChatFilters>[] = [];
    const { body, click } = await renderOpenBar(features, baseFilters, p => calls.push(p));
    const chip = findChipButton(body, FEATURE_KIND_LABEL.closed);
    expect(chip, 'чип «Закрытые» не найден').not.toBeNull();
    expect(chip!.getAttribute('aria-disabled'), 'при неизвестном kind чипы не приглушаются').not.toBe('true');
    expect(/\d/.test(textOf(chip!)), 'на чипе есть число, хотя в индексе нет kind').toBe(false);
    await click(chip!);
    expect(calls.filter(c => c.featureKinds !== undefined)).toEqual([{ featureKinds: [...ALL_FEATURE_KINDS] }]);
  });
});

describe('FilterBar: п.2 футер «Показать N фич» (360, режим «Фичи»)', () => {
  it('360 + groupBy=features → футер «Показать N фич» (N = открытые фичи), не «Готово»', async () => {
    // 2 открытые (running) + 1 закрытая → N = 2.
    const features = [feat('f1', 'running', 'running'), feat('f2', 'running', 'running'), feat('c1', 'closed', 'closed')];
    const { body } = await renderOpenBar(features);
    // Футер-кнопка «Показать 2 фич» (N = открытые фичи) — на месте, «Готово» нет.
    expect(findByText(body, 'Показать 2 фич'), 'футер «Показать 2 фич» отсутствует').not.toBeNull();
    expect(findByText(body, 'Готово'), 'футер остался «Готово» (не переключился на режим фич)').toBeNull();
    // N = открытые фичи индекса (bucket != closed) = 2 (закрытая не входит).
    expect(features.filter(f => f.bucket !== 'closed').length).toBe(2);
  });

  it('дефолт (groupBy=days, 360) → футер «Готово», не «Показать N фич»', async () => {
    const features = [feat('f1', 'running', 'running')];
    const { body } = await renderOpenBar(features, { ...baseFilters, groupBy: 'days' });
    expect(findByText(body, 'Готово'), 'футер «Готово» отсутствует в режиме «Дни»').not.toBeNull();
    // «Показать 1 фич» — футер режима «Фичи»; в «Дни» его нет (ищет точную фразу,
    // не «Показать», чтобы не словить заголовок секции «Показать только»).
    expect(findByText(body, 'Показать 1 фич'), '«Показать N фич» в режиме «Дни» (только в «Фичи»)').toBeNull();
  });
});
