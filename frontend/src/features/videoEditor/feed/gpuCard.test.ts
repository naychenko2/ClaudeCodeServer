// Шаг 37: чистая логика видимости строки ожидания GPU и кнопки «Сделать в облаке»
// (flag, gpu, down, облачный исполнитель, котировка) + «запуск только по клику»
// + (ревью Глеба) защита от двойного клика (ref-guard) и запуск по котировке из хука.
// Окружение node, без jsdom: статичную часть рендерим renderToStaticMarkup — там эффекты
// не гоняются, поэтому videoApi.startJob на монтировании не вызывается; интерактивную часть
// (диалог, двойной клик) рендерим на минимальном фейк-DOM для createRoot.

import { afterAll, afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { renderToStaticMarkup } from 'react-dom/server';
import { setAllFlags } from '../../../lib/featureFlags';
import { videoApi, type VideoCatalog, type VideoQuote } from '../api';
import { CATALOG, PREFS } from '../mocks';
import { __resetVideoStore, __setScopeData } from '../store/videoStore';

// ── Мок-модули (hoisted: фабрики vi.mock не видят верхний скоуп без vi.hoisted) ──
// videoStore мокаем частично: useVideoStoreVersion не тянет живой подписчик
// (ensureLive → videoApi.subscribe → signalr) в клиентский рендер; getCatalog/__setScopeData — настоящие
vi.mock('../store/videoStore', async () => {
  const actual = await vi.importActual<typeof import('../store/videoStore')>('../store/videoStore');
  return { ...actual, useVideoStoreVersion: () => 0 };
});
import {
  GpuQueueBlock, cloudCandidate, cloudQuoteRequest, isCloudButtonEnabled, isStandDown,
  makeLaunchGuard, priceLabel, showCloudButton, showQueueLine,
} from './gpuCard';
import type { LocalGpuQueueInfo } from '../../../types/localGpuQueue';

// ── Мок-данные ──

// Только локальные: облачного исполнителя нет
const LOCAL_ONLY: VideoCatalog = {
  ...CATALOG,
  providers: CATALOG.providers.filter(p => p.key === 'local'),
  autoProviders: ['local'],
};
// Облачные есть, но все недоступны: исполнителя нет
const NO_CLOUD: VideoCatalog = {
  ...CATALOG,
  providers: CATALOG.providers.map(p => (p.key === 'local' ? p : { ...p, available: false, reason: 'Нет доступа' })),
};

const DOWN: LocalGpuQueueInfo = { downSince: 1_700_000_000_000 };
const QUEUED: LocalGpuQueueInfo = { ahead: 0, startInSeconds: 40 };
// «Макс»: несколько семплеров — строка «этап N · шаг k из M»
const STAGED: LocalGpuQueueInfo = { steps: { step: 12, total: 25, stage: 2 }, remainingSeconds: 30 };

// Котировка облачного исполнителя, которую «даёт» хук useCloudQuote
const CLOUD_QUOTE: VideoQuote = {
  quoteId: 'q-cloud', provider: 'fal', model: 'veo-3.1', count: 2, durationSec: 8,
  price: { amount: 3.2, unit: 'usd', approx: true, source: 'pricing' },
  license: 'watermark', heavy: false, expiresAt: '2026-10-02T15:10:00Z',
};

const PARAMS = { count: 2, durationSec: 8, aspect: '16:9', sound: true };

let quoteSpy: ReturnType<typeof vi.spyOn>;
let startJobSpy: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
  vi.clearAllMocks();
  __resetVideoStore();
  setAllFlags({ 'local-model-stats': true });
  __setScopeData('p1', CATALOG, PREFS);
  quoteSpy = vi.spyOn(videoApi, 'quote').mockResolvedValue(CLOUD_QUOTE);
  startJobSpy = vi.spyOn(videoApi, 'startJob').mockResolvedValue({ jobId: 'j-cloud' });
});

afterEach(() => { vi.restoreAllMocks(); });

// ── Чистая логика видимости ──

describe('isStandDown', () => {
  it('false без gpu', () => expect(isStandDown(null)).toBe(false));
  it('false без downSince', () => expect(isStandDown({})).toBe(false));
  it('true при downSince', () => expect(isStandDown(DOWN)).toBe(true));
});

describe('cloudCandidate', () => {
  it('null без каталога', () => expect(cloudCandidate(null)).toBeNull());
  it('null, если только локальные', () => expect(cloudCandidate(LOCAL_ONLY)).toBeNull());
  it('null, если облачные все недоступны', () => expect(cloudCandidate(NO_CLOUD)).toBeNull());
  it('даёт первого доступного облачного исполнителя в порядке «Авто»', () => {
    expect(cloudCandidate(CATALOG)?.key).toBe('fal');
  });
  // m4: единая проверка «доступен + умеет» — доступный провайдер без моделей не подходит
  it('m4: доступный провайдер без моделей → null (мутация «убрать supports» красит)', () => {
    const cat: VideoCatalog = {
      ...CATALOG,
      providers: CATALOG.providers.map(p => (p.key === 'fal' ? { ...p, models: [] } : p)),
    };
    // fal доступен, но без моделей; higgsfield недоступен → исполнителя нет
    expect(cloudCandidate(cat)).toBeNull();
  });
});

describe('showCloudButton (видимость)', () => {
  it('true: флаг + down + облачный исполнитель', () => {
    expect(showCloudButton(true, DOWN, CATALOG)).toBe(true);
  });
  it('false: флаг выключен (мутация «показ с выключенным флагом»)', () => {
    expect(showCloudButton(false, DOWN, CATALOG)).toBe(false);
  });
  it('false: стенд не лежит (мутация «показ без down»)', () => {
    expect(showCloudButton(true, QUEUED, CATALOG)).toBe(false);
  });
  it('false: нет gpu', () => expect(showCloudButton(true, null, CATALOG)).toBe(false));
  it('false: нет облачного исполнителя (только локальные)', () => {
    expect(showCloudButton(true, DOWN, LOCAL_ONLY)).toBe(false);
  });
});

describe('isCloudButtonEnabled (кликабельность)', () => {
  it('true: котировка с ценой', () => expect(isCloudButtonEnabled(CLOUD_QUOTE)).toBe(true));
  it('true: котировка «free» — цена известна', () => {
    expect(isCloudButtonEnabled({ ...CLOUD_QUOTE, price: { ...CLOUD_QUOTE.price, amount: null, unit: 'free' } })).toBe(true);
  });
  it('false: нет котировки (грузится)', () => expect(isCloudButtonEnabled(null)).toBe(false));
  it('false: котировка без цены', () => {
    expect(isCloudButtonEnabled({ ...CLOUD_QUOTE, price: { ...CLOUD_QUOTE.price, amount: null } })).toBe(false);
  });
});

describe('showQueueLine', () => {
  it('false: флаг выключен', () => expect(showQueueLine(false, QUEUED)).toBe(false));
  it('false: нет gpu', () => expect(showQueueLine(true, null)).toBe(false));
  it('true: флаг + gpu', () => expect(showQueueLine(true, QUEUED)).toBe(true));
});

describe('cloudQuoteRequest', () => {
  it('null без облачного исполнителя', () => expect(cloudQuoteRequest(LOCAL_ONLY, { sceneId: 's1', ...PARAMS })).toBeNull());
  it('null без каталога', () => expect(cloudQuoteRequest(null, { sceneId: 's1', ...PARAMS })).toBeNull());
  it('строит запрос к облачному исполнителю: «Авто»-модель и параметры сцены как у запуска', () => {
    expect(cloudQuoteRequest(CATALOG, { sceneId: 's1', ...PARAMS }))
      .toMatchObject({ provider: 'fal', model: 'auto', sceneId: 's1', count: 2, durationSec: 8, aspect: '16:9', sound: true });
  });
  it('clamp count в 1', () => expect(cloudQuoteRequest(CATALOG, { sceneId: 's1', count: 0, durationSec: 8, aspect: '16:9', sound: true })?.count).toBe(1));
});

describe('priceLabel', () => {
  it('«≈ $3.20» из котировки', () => expect(priceLabel(CLOUD_QUOTE)).toBe('≈ $3.20'));
  it('«бесплатно» при unit free', () => {
    expect(priceLabel({ ...CLOUD_QUOTE, price: { ...CLOUD_QUOTE.price, amount: null, unit: 'free' } })).toBe('бесплатно');
  });
  it('null без котировки и без цены', () => {
    expect(priceLabel(null)).toBeNull();
    expect(priceLabel({ ...CLOUD_QUOTE, price: { ...CLOUD_QUOTE.price, amount: null } })).toBeNull();
  });
});

describe('makeLaunchGuard (двойной клик)', () => {
  it('второй begin до end — повторный', () => {
    const g = makeLaunchGuard();
    expect(g.begin()).toBe(true);
    expect(g.begin()).toBe(false);
    g.end();
    expect(g.begin()).toBe(true);
  });
});

// ── Запуск только по клику: на монтировании startJob не вызывается ──

describe('GpuQueueBlock: запуск только по клику', () => {
  it('на монтировании startJob не вызывается (даже при down + флага), кнопка видна', () => {
    const html = renderToStaticMarkup(
      createElement(GpuQueueBlock, {
        scope: 'p1', sessionId: 's1', sceneId: 's1', feature: true, gpu: DOWN, params: PARAMS,
      }),
    );
    expect(html).toContain('Сделать в облаке');
    expect(startJobSpy).not.toHaveBeenCalled();
  });

  it('без gpu блок не рендерится и startJob не вызывается', () => {
    const html = renderToStaticMarkup(
      createElement(GpuQueueBlock, {
        scope: 'p1', sessionId: 's1', sceneId: 's1', feature: true, gpu: null, params: PARAMS,
      }),
    );
    expect(html).toBe('');
    expect(startJobSpy).not.toHaveBeenCalled();
  });

  it('флаг выключен: блок не рендерится даже с gpu', () => {
    const html = renderToStaticMarkup(
      createElement(GpuQueueBlock, {
        scope: 'p1', sessionId: 's1', sceneId: 's1', feature: false, gpu: DOWN, params: PARAMS,
      }),
    );
    expect(html).toBe('');
    expect(startJobSpy).not.toHaveBeenCalled();
  });
});

// ── Минимальный фейк-DOM для createRoot (node, без jsdom) ──
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
  // Порталы (Modal) рендерятся в document.body
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
// window нужен showToast (window.dispatchEvent)
const fwindow = {
  dispatchEvent: () => true,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
  // getActiveElementDeep: `element instanceof window.HTMLIFrameElement`
  HTMLIFrameElement: class {},
};
(globalThis as { window?: unknown }).window = fwindow;
// Modal рендерит портал в document.body
(globalThis as { document?: unknown }).document = fdoc;
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

afterAll(() => {
  delete (globalThis as { window?: unknown }).window;
  delete (globalThis as { document?: unknown }).document;
  delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT;
});

// ── Вспомогательное: рендер + поиск узлов ──

function mount(node: ReturnType<typeof createElement>): { root: Root; container: FNode } {
  const container = fdoc.createElement('div');
  const root = createRoot(container);
  act(() => { root.render(node); });
  return { root, container };
}

// Все текстовые ноды дерева
function textNodes(n: FNode, out: string[] = []): string[] {
  if (n.nodeType === 3) out.push(n.nodeValue);
  for (const c of n.childNodes) textNodes(c, out);
  return out;
}

// Кнопка по её точной подписи: React держит props на узле под ключом __reactProps$<random>
function findButton(container: FNode, label: string): { onClick: () => void } | null {
  let hit: { onClick: () => void } | null = null;
  const walk = (x: FNode) => {
    if (hit) return;
    if (x.nodeType === 1) {
      const p = Object.keys(x).find(k => k.startsWith('__reactProps$'));
      if (p) {
        const props = (x as unknown as Record<string, unknown>)[p] as Record<string, unknown> | undefined;
        if (props?.onClick && textNodes(x).join('') === label) hit = { onClick: props.onClick as () => void };
      }
    }
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
  return hit;
}

// ── Интерактив: диалог, двойной клик, котировка из хука ──

// Рендер блока «стенд лежит + облачный исполнитель» и доводим котировку хука до готовности:
// useCloudQuote котирует с задержкой 300 мс, поэтому под фейковыми таймерами дожидаемся её.
async function renderBlockDown() {
  vi.useFakeTimers();
  const { root, container } = mount(createElement(GpuQueueBlock, {
    scope: 'p1', sessionId: 's1', sceneId: 's1', feature: true, gpu: DOWN, params: PARAMS,
  }));
  // Прогоняем 300 мс: таймаут хука срабатывает, videoApi.quote (мок) разрешает котировку,
  // setState перерисовывает кнопку с ценой. Await — чтобы микротаск .then() отработал.
  await act(async () => { vi.advanceTimersByTime(300); });
  return { root, container };
}

describe('GpuQueueBlock: интерактив (ревью)', () => {
  afterEach(() => { vi.useRealTimers(); });

  it('диалог честный: «продолжит ждать», слова «отмен…» нет (мутация: вернуть старую строку)', async () => {
    const { root, container } = await renderBlockDown();
    const btn = findButton(container, 'Сделать в облаке · ≈ $3.20');
    expect(btn, 'кнопка «Сделать в облаке» не найдена').not.toBeNull();
    act(() => { btn!.onClick(); });
    // Диалог — портал в document.body
    const subtitle = textNodes(fdoc.body).find(t => t.includes('Стоимость'));
    expect(subtitle, 'подзаголовок диалога не найден').toBeDefined();
    expect(subtitle).toContain('продолжит ждать');
    expect(subtitle).not.toMatch(/отмен/i);
    act(() => { root.unmount(); });
  });

  it('два клика подряд → startJob вызван один раз (мутация: убрать guard)', async () => {
    const { root, container } = await renderBlockDown();
    const btn = findButton(container, 'Сделать в облаке · ≈ $3.20');
    expect(btn, 'кнопка «Сделать в облаке» не найдена').not.toBeNull();
    act(() => { btn!.onClick(); });
    const confirm = findButton(fdoc.body, 'Запустить');
    expect(confirm, 'кнопка «Запустить» не найдена').not.toBeNull();
    // Два синхронных клика: окно микротаска, пока ре-рендер с busy не заблокировал кнопку
    act(() => { confirm!.onClick(); confirm!.onClick(); });
    expect(startJobSpy).toHaveBeenCalledTimes(1);
    act(() => { root.unmount(); });
  });

  it('запуск по котировке из хука: startJob получает quoteId хука, повторного quote нет (мутация: re-quote в launch)', async () => {
    const { root, container } = await renderBlockDown();
    const btn = findButton(container, 'Сделать в облаке · ≈ $3.20');
    expect(btn, 'кнопка «Сделать в облаке» не найдена').not.toBeNull();
    act(() => { btn!.onClick(); });
    const confirm = findButton(fdoc.body, 'Запустить');
    expect(confirm, 'кнопка «Запустить» не найдена').not.toBeNull();
    act(() => { confirm!.onClick(); });
    // Запуск шёл по quoteId из хука, а не по повторной котировке
    expect(startJobSpy).toHaveBeenCalledTimes(1);
    expect(startJobSpy).toHaveBeenCalledWith('p1', 's1', expect.objectContaining({ quoteId: 'q-cloud', sceneId: 's1' }));
    // videoApi.quote вызван ровно один раз — самим хуком; launch() его не повторяет
    expect(quoteSpy).toHaveBeenCalledTimes(1);
    act(() => { root.unmount(); });
  });

  it('M3: исключение в запуске → guard освобождён через finally → повторный запуск проходит (мутация: убрать finally)', async () => {
    const { root, container } = await renderBlockDown();
    const btn = findButton(container, 'Сделать в облаке · ≈ $3.20');
    expect(btn, 'кнопка «Сделать в облаке» не найдена').not.toBeNull();
    // Первый запуск падает: guard должен быть освобождён в finally
    startJobSpy.mockRejectedValueOnce(new Error('сбой запуска'));
    act(() => { btn!.onClick(); });
    let confirm = findButton(fdoc.body, 'Запустить');
    expect(confirm, 'кнопка «Запустить» не найдена').not.toBeNull();
    act(() => { confirm!.onClick(); });
    await act(async () => { await Promise.resolve(); });
    expect(startJobSpy).toHaveBeenCalledTimes(1);
    // Повторный запуск: guard свободен → begin() проходит, startJob вызывается второй раз
    act(() => { btn!.onClick(); });
    confirm = findButton(fdoc.body, 'Запустить');
    expect(confirm, 'кнопка «Запустить» (повтор) не найдена').not.toBeNull();
    act(() => { confirm!.onClick(); });
    await act(async () => { await Promise.resolve(); });
    expect(startJobSpy, 'повторный запуск не прошёл: guard не освобождён').toHaveBeenCalledTimes(2);
    act(() => { root.unmount(); });
  });
});

// ── Строка ожидания: «этап N · шаг k из M» через общий форматтер ──

describe('GpuQueueBlock: строка ожидания', () => {
  it('идущая задача с этапами: «этап 2 · шаг 12 из 25» (длинная форма)', () => {
    const html = renderToStaticMarkup(
      createElement(GpuQueueBlock, {
        scope: 'p1', sessionId: 's1', sceneId: 's1', feature: true, gpu: STAGED, params: PARAMS,
      }),
    );
    expect(html).toContain('этап 2 · шаг 12 из 25');
  });

  it('в очереди: «В очереди · старт ≈ через 40 с»', () => {
    const html = renderToStaticMarkup(
      createElement(GpuQueueBlock, {
        scope: 'p1', sessionId: 's1', sceneId: 's1', feature: true, gpu: QUEUED, params: PARAMS,
      }),
    );
    expect(html).toContain('старт ≈ через 40 с');
  });
});
