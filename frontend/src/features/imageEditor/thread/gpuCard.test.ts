// Шаг 35: чистая логика видимости строки ожидания GPU и кнопки «Сделать в облаке»
// (flag, gpu, down, наличие котировки/исполнителя) + «запуск только по клику» +
// (ревью Глеба) честный текст про локальную задачу, op котировки по меткам нити и
// защита от двойного клика.
// Окружение node, без jsdom: статичную часть рендерим renderToStaticMarkup — там эффекты
// не гоняются, поэтому launchThread на монтировании не вызывается; интерактивную часть
// (диалог, двойной клик) рендерим на минимальном фейк-DOM для createRoot.

import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { renderToStaticMarkup } from 'react-dom/server';
import { setAllFlags } from '../../../lib/featureFlags';
import {
  CLOUD_REPRODUCIBLE_OPS, cloudCandidate, cloudOriginalParams, cloudQuoteRequest, GpuQueueBlock,
  isCloudButtonEnabled, isStandDown, makeLaunchGuard, originalTaskParams, showCloudButton, showQueueLine,
} from './gpuCard';
import { JobBlock } from './ThreadCard';
import { threadHasMask } from './useThreadLaunch';
import { setThreadMarks } from './threadStore';
import type { ImageEditCatalog, ImageEditCaps, ImageEditQuote, ImageEditQuoteRequest } from '../api';
import type { LocalGpuQueueInfo } from '../../../types/localGpuQueue';
import type { ImageThread, ImageThreadCharacter } from './threadsApi';
import type { JobStatus } from './useJobStatus';

// ── Мок-данные ──

const CATALOG: ImageEditCatalog = {
  default: { provider: 'fal', model: 'auto' },
  providers: [
    { key: 'fal', label: 'Fal', priceUnit: 'usd', models: [{ id: 'auto', label: 'Авто' }] },
    { key: 'local', label: 'Локальные модели', priceUnit: 'free', models: [{ id: 'auto', label: 'Авто' }] },
  ],
  limits: { maxFileMb: 20, maxReferences: 4, maxCount: 4 },
  reason: null,
};
// Умовчание — локальные: облачного исполнителя нет
const LOCAL_DEFAULT: ImageEditCatalog = { ...CATALOG, default: { provider: 'local', model: 'auto' } };

const QUOTE: ImageEditQuote = {
  quoteId: 'q1', provider: 'fal', model: 'auto',
  estimate: { amount: 0.04, unit: 'usd', approx: true, source: 'provider' },
};
const NO_PRICE: ImageEditQuote = { ...QUOTE, estimate: { ...QUOTE.estimate, amount: null } };

const DOWN: LocalGpuQueueInfo = { downSince: 1_700_000_000_000 };
const QUEUED: LocalGpuQueueInfo = { ahead: 0, startInSeconds: 40 };
const EMPTY_THREAD = {
  id: 't1', file: 'a.png', lineage: [], stacks: [], currentStackId: null, currentStepId: null,
  settings: null, pendingJobId: 'j1', createdAt: '2026-01-01T00:00:00Z',
  // M3: запуск с промптом — чтобы originalTaskParams нашёл исходные параметры
  launches: [{ jobId: 'j1', baseVersionId: null, baseStepId: null, at: '2026-01-01T00:00:00Z', status: 'running', initiator: 'human', prompt: 'тестовый промпт', op: 'edit' }],
} as unknown as ImageThread;
// m5: нить с пустым промптом запуска — originalTaskParams вернёт null, кнопки не будет
const EMPTY_PROMPT_THREAD = {
  ...EMPTY_THREAD,
  launches: [{ jobId: 'j1', baseVersionId: null, baseStepId: null, at: '2026-01-01T00:00:00Z', status: 'running', initiator: 'human', prompt: '', op: 'edit' }],
} as unknown as ImageThread;
// Стенд лежит, задача с двумя вариантами
const RUN_STATUS: JobStatus = {
  phase: 'run', variants: [], count: 2, cost: null, charged: null, error: null, model: null,
  createdAt: null, run: null, runs: null, eta: null, runStartedAt: null, queuePosition: null,
  fallbackEta: null, gpu: DOWN,
};

// ── Мок-модули (hoisted: фабрики vi.mock не видят верхний скоуп без vi.hoisted) ──

const { launchThread, useQuote, useCatalog, useBoxWidth, useJobStatus, useProgress } = vi.hoisted(() => ({
  launchThread: vi.fn(async () => true),
  useQuote: vi.fn(),
  useCatalog: vi.fn(),
  useBoxWidth: vi.fn(() => 0),
  useJobStatus: vi.fn(),
  useProgress: vi.fn(() => ({ percent: 0, queuePosition: null })),
}));
// threadHasImage/threadHasMask — настоящие (именно их расчёт проверяем); launchThread — мок,
// чтобы запуск не ходил в api.quote/startJob
vi.mock('./useThreadLaunch', async () => {
  const actual = await vi.importActual<typeof import('./useThreadLaunch')>('./useThreadLaunch');
  return { ...actual, launchThread };
});
vi.mock('../useQuote', () => ({ useQuote }));
vi.mock('./catalog', () => ({ useCatalog }));
vi.mock('../../videoEditor/useBoxWidth', () => ({ useBoxWidth }));
vi.mock('./useJobStatus', () => ({
  useJobStatus, useProgress,
  queueText: (p: number | null) => (p ? ` · в очереди ${p}` : ''),
}));

beforeEach(() => {
  vi.clearAllMocks();
  setAllFlags({ 'local-model-stats': true });
  (useQuote as ReturnType<typeof vi.fn>).mockReturnValue({ quote: null, loading: false });
  (useCatalog as ReturnType<typeof vi.fn>).mockReturnValue(null);
  (useBoxWidth as ReturnType<typeof vi.fn>).mockReturnValue(0);
  (useJobStatus as ReturnType<typeof vi.fn>).mockReturnValue({ status: null, cancel: vi.fn() });
  (useProgress as ReturnType<typeof vi.fn>).mockReturnValue({ percent: 0, queuePosition: null });
});

// ── Чистая логика видимости ──

describe('isStandDown', () => {
  it('false без gpu', () => expect(isStandDown(null)).toBe(false));
  it('false без downSince', () => expect(isStandDown({})).toBe(false));
  it('true при downSince', () => expect(isStandDown(DOWN)).toBe(true));
});

describe('cloudCandidate', () => {
  it('null без каталога', () => expect(cloudCandidate(null)).toBeNull());
  it('null, если умолчание — локальные', () => expect(cloudCandidate(LOCAL_DEFAULT)).toBeNull());
  it('null, если умолчание null', () => expect(cloudCandidate({ ...CATALOG, default: { provider: null, model: 'auto' } })).toBeNull());
  it('даёт облачного исполнителя «Автоматически»', () => {
    expect(cloudCandidate(CATALOG)?.key).toBe('fal');
  });
  // M2: провайдер есть, но недоступен
  it('M2: провайдер есть, но available=false → null (мутация «убрать available» красит)', () => {
    const cat: ImageEditCatalog = {
      ...CATALOG,
      providers: CATALOG.providers.map(p => (p.key === 'fal' ? { ...p, available: false } : p)),
    };
    expect(cloudCandidate(cat)).toBeNull();
  });
  // M2: провайдер есть, но ни одна модель не поддерживает операцию
  it('M2: провайдер есть, но модель не поддерживает операцию (caps.ops) → null (мутация «убрать caps.ops» красит)', () => {
    const cat: ImageEditCatalog = {
      ...CATALOG,
      providers: CATALOG.providers.map(p => (p.key === 'fal'
        ? { ...p, models: [{ id: 'auto', label: 'Авто', caps: { ops: ['inpaint'], mask: 'none', maxReferences: 4, maxCount: 4, faceByReferences: false } as ImageEditCaps }] }
        : p)),
    };
    // 'edit' не в caps.ops → исполнителя нет
    expect(cloudCandidate(cat, 'edit')).toBeNull();
    // 'inpaint' в caps.ops → исполнитель есть
    expect(cloudCandidate(cat, 'inpaint')?.key).toBe('fal');
  });
});

describe('originalTaskParams (m5: исходные параметры запуска)', () => {
  it('возвращает prompt и op запуска', () => {
    expect(originalTaskParams(EMPTY_THREAD, 'j1')).toEqual({ prompt: 'тестовый промпт', op: 'edit' });
  });
  it('null, если pendingJobId не задан', () => expect(originalTaskParams(EMPTY_THREAD, null)).toBeNull());
  it('null, если запуск не найден', () => expect(originalTaskParams(EMPTY_THREAD, 'j-другой')).toBeNull());
  it('m5: пустой prompt → null (мутация «убрать проверку пустоты» красит)', () => {
    expect(originalTaskParams(EMPTY_PROMPT_THREAD, 'j1')).toBeNull();
  });
});

describe('GpuQueueBlock: m5 (пустой промпт → кнопки нет)', () => {
  it('пустой промпт запуска → кнопки «Сделать в облаке» нет (мутация «убрать проверку пустоты» красит)', () => {
    (useQuote as ReturnType<typeof vi.fn>).mockReturnValue({ quote: QUOTE, loading: false });
    (useCatalog as ReturnType<typeof vi.fn>).mockReturnValue(CATALOG);
    const html = renderToStaticMarkup(
      createElement(GpuQueueBlock, {
        projectId: 'p1', sessionId: 's1', thread: EMPTY_PROMPT_THREAD, status: { gpu: DOWN } as never,
        feature: true, count: 2,
      }),
    );
    expect(html).not.toContain('Сделать в облаке');
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
  it('false: нет облачного исполнителя (умолчание локальное)', () => {
    expect(showCloudButton(true, DOWN, LOCAL_DEFAULT)).toBe(false);
  });
});

describe('isCloudButtonEnabled (кликабельность)', () => {
  it('true: котировка с ценой', () => expect(isCloudButtonEnabled(QUOTE)).toBe(true));
  it('false: нет котировки (грузится)', () => expect(isCloudButtonEnabled(null)).toBe(false));
  it('false: котировка без цены', () => expect(isCloudButtonEnabled(NO_PRICE)).toBe(false));
});

describe('showQueueLine', () => {
  it('false: флаг выключен', () => expect(showQueueLine(false, QUEUED)).toBe(false));
  it('false: нет gpu', () => expect(showQueueLine(true, null)).toBe(false));
  it('true: флаг + gpu', () => expect(showQueueLine(true, QUEUED)).toBe(true));
});

describe('cloudQuoteRequest', () => {
  it('null без облачного исполнителя', () => expect(cloudQuoteRequest(LOCAL_DEFAULT, 'edit', 2, false)).toBeNull());
  it('null без каталога', () => expect(cloudQuoteRequest(null, 'edit', 2, false)).toBeNull());
  it('строит запрос к облачному исполнителю с «Авто»-моделью', () => {
    const req = cloudQuoteRequest(CATALOG, 'edit', 2, false);
    expect(req).toMatchObject({ provider: 'fal', model: 'auto', mode: 'auto', op: 'edit', count: 2, hasMask: false });
  });
  it('hasMask уходит в тело (мутация «вернуть hasMask: false» красит)', () => {
    expect(cloudQuoteRequest(CATALOG, 'inpaint', 1, true)?.hasMask).toBe(true);
  });
  it('clamp count в 1', () => expect(cloudQuoteRequest(CATALOG, 'edit', 0, false)?.count).toBe(1));
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

// ── Честный текст (ревью): «отмен…» в диалоге не возвращается ──

describe('текст про локальную задачу', () => {
  it('note: «продолжит ждать», слова «отмен…» нет (мутация: вернуть старую строку)', () => {
    (useQuote as ReturnType<typeof vi.fn>).mockReturnValue({ quote: QUOTE, loading: false });
    (useCatalog as ReturnType<typeof vi.fn>).mockReturnValue(CATALOG);
    const html = renderToStaticMarkup(
      createElement(GpuQueueBlock, {
        projectId: 'p1', sessionId: 's1', thread: EMPTY_THREAD, status: { gpu: DOWN } as never,
        feature: true, count: 2,
      }),
    );
    expect(html).toContain('локальная задача продолжит ждать');
    expect(html).not.toMatch(/отмен/i);
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
// window нужен showToast (window.dispatchEvent) и useIsMobile (matchMedia)
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
  for (const n of textNodes(container)) void n;
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

// ── Интерактив: диалог, двойной клик ──

function renderBlockDown() {
  (useQuote as ReturnType<typeof vi.fn>).mockReturnValue({ quote: QUOTE, loading: false });
  (useCatalog as ReturnType<typeof vi.fn>).mockReturnValue(CATALOG);
  return mount(createElement(GpuQueueBlock, {
    projectId: 'p1', sessionId: 's1', thread: EMPTY_THREAD, status: { gpu: DOWN } as never,
    feature: true, count: 2,
  }));
}

describe('GpuQueueBlock: интерактив (ревью)', () => {
  it('диалог честный: «продолжит ждать», слова «отмен…» нет (мутация: вернуть старую строку)', () => {
    const { root, container } = renderBlockDown();
    const btn = findButton(container, 'Сделать в облаке · ≈ $0.04');
    expect(btn, 'кнопка «Сделать в облаке» не найдена').not.toBeNull();
    act(() => { btn!.onClick(); });
    // Диалог — портал в document.body
    const subtitle = textNodes(fdoc.body).find(t => t.includes('Стоимость'));
    expect(subtitle, 'подзаголовок диалога не найден').toBeDefined();
    expect(subtitle).toContain('продолжит ждать');
    expect(subtitle).not.toMatch(/отмен/i);
    act(() => { root.unmount(); });
  });

  it('два клика подряд → launchThread вызван один раз (мутация: убрать guard)', () => {
    const { root, container } = renderBlockDown();
    const btn = findButton(container, 'Сделать в облаке · ≈ $0.04');
    expect(btn, 'кнопка «Сделать в облаке» не найдена').not.toBeNull();
    act(() => { btn!.onClick(); });
    const confirm = findButton(fdoc.body, 'Запустить');
    expect(confirm, 'кнопка «Запустить» не найдена').not.toBeNull();
    // Два синхронных клика: окно микротаска, пока ре-рендер с busy не заблокировал кнопку
    act(() => { confirm!.onClick(); confirm!.onClick(); });
    expect(launchThread).toHaveBeenCalledTimes(1);
    act(() => { root.unmount(); });
  });

  it('M3: исключение в запуске → guard освобождён через finally → повторный запуск проходит (мутация: убрать finally)', async () => {
    const { root, container } = renderBlockDown();
    const btn = findButton(container, 'Сделать в облаке · ≈ $0.04');
    expect(btn, 'кнопка «Сделать в облаке» не найдена').not.toBeNull();
    // Первый запуск падает: guard должен быть освобождён в finally
    (launchThread as ReturnType<typeof vi.fn>).mockRejectedValueOnce(new Error('сбой запуска'));
    act(() => { btn!.onClick(); });
    let confirm = findButton(fdoc.body, 'Запустить');
    expect(confirm, 'кнопка «Запустить» не найдена').not.toBeNull();
    act(() => { confirm!.onClick(); });
    // Дожидаемся, пока отклонённый прогон дойдёт до finally (микрозадачи)
    await act(async () => { await Promise.resolve(); });
    expect(launchThread).toHaveBeenCalledTimes(1);
    // Повторный запуск: guard свободен → begin() проходит, launchThread вызывается второй раз
    act(() => { btn!.onClick(); });
    confirm = findButton(fdoc.body, 'Запустить');
    expect(confirm, 'кнопка «Запустить» (повтор) не найдена').not.toBeNull();
    act(() => { confirm!.onClick(); });
    await act(async () => { await Promise.resolve(); });
    expect(launchThread, 'повторный запуск не прошёл: guard не освобождён').toHaveBeenCalledTimes(2);
    act(() => { root.unmount(); });
  });
});

// ── op котировки по меткам нити (ревью): маска → inpaint ──

describe('JobBlock: op котировки по меткам нити', () => {
  const renderJob = (withMask: boolean) => {
    (useJobStatus as ReturnType<typeof vi.fn>).mockReturnValue({ status: RUN_STATUS, cancel: vi.fn() });
    (useQuote as ReturnType<typeof vi.fn>).mockReturnValue({ quote: QUOTE, loading: false });
    (useCatalog as ReturnType<typeof vi.fn>).mockReturnValue(CATALOG);
    setThreadMarks('t1', withMask ? [{ type: 'mask', points: [[0, 0], [10, 10]], width: 3 }] : [], null);
    return mount(createElement(JobBlock, { projectId: 'p1', sessionId: 's1', thread: EMPTY_THREAD }));
  };

  it('финальное ревью: op из исходной задачи (edit), не из текущего состояния нити (inpaint)', () => {
    const { root } = renderJob(true);
    const req = (useQuote as ReturnType<typeof vi.fn>).mock.calls.at(-1)?.[2] as ImageEditQuoteRequest | null | undefined;
    expect(req, 'котировка не запрошена').not.toBeNull();
    // op — из исходной задачи (EMPTY_THREAD.launches[0].op = 'edit'), не из pickOp(маска → inpaint)
    expect(req?.op).toBe('edit');
    // hasMask — из меток нити (тот же хелпер, что в launchThread)
    expect(req?.hasMask).toBe(true);
    act(() => { root.unmount(); });
  });

  it('без маски → edit (мутация: вернуть захардкоженное false красит)', () => {
    const { root } = renderJob(false);
    const req = (useQuote as ReturnType<typeof vi.fn>).mock.calls.at(-1)?.[2] as ImageEditQuoteRequest | null | undefined;
    expect(req, 'котировка не запрошена').not.toBeNull();
    expect(req?.op).toBe('edit');
    expect(req?.hasMask).toBe(false);
    act(() => { root.unmount(); });
  });

  it('hasMask котировки совпадает с тем, что получит launchThread, для тех же меток', () => {
    const { root } = renderJob(true);
    const req = (useQuote as ReturnType<typeof vi.fn>).mock.calls.at(-1)?.[2] as ImageEditQuoteRequest | null | undefined;
    expect(req, 'котировка не запрошена').not.toBeNull();
    // threadHasMask — тот же хелпер, которым launchThread считает маску: котировка и запуск не расходятся
    expect(req?.hasMask).toBe(threadHasMask(EMPTY_THREAD));
    act(() => { root.unmount(); });
  });
});

// ── Шаг 2: gpu есть → без собственной полосы (ровно одна полоса, не дубль) ──

describe('JobBlock: gpu есть → без собственной полосы', () => {
  it('running gpu: ровно одна полоса (собственная скрыта, остаётся строка GPU)', () => {
    const runningGpu: LocalGpuQueueInfo = { running: true, steps: { step: 4, total: 10 } };
    (useJobStatus as ReturnType<typeof vi.fn>).mockReturnValue({ status: { ...RUN_STATUS, gpu: runningGpu }, cancel: vi.fn() });
    (useQuote as ReturnType<typeof vi.fn>).mockReturnValue({ quote: QUOTE, loading: false });
    (useCatalog as ReturnType<typeof vi.fn>).mockReturnValue(CATALOG);
    const html = renderToStaticMarkup(
      createElement(JobBlock, { projectId: 'p1', sessionId: 's1', thread: EMPTY_THREAD }),
    );
    // Собственная полоса (transition width .5s) + полоса GPU-строки. gpu running → showQueueLine true →
    // собственная скрыта, остаётся ровно одна (строка GPU). Без правки их было бы две.
    const bars = (html.match(/role="progressbar"/g) ?? []).length;
    expect(bars, 'ровно одна полоса, не дубль').toBe(1);
  });
});

// ── Запуск только по клику: на монтировании launchThread не вызывается ──

describe('GpuQueueBlock: запуск только по клику', () => {
  it('на монтировании launchThread не вызывается (даже при down + флага)', () => {
    (useQuote as ReturnType<typeof vi.fn>).mockReturnValue({ quote: QUOTE, loading: false });
    (useCatalog as ReturnType<typeof vi.fn>).mockReturnValue(CATALOG);
    renderToStaticMarkup(
      createElement(GpuQueueBlock, {
        projectId: 'p1', sessionId: 's1', thread: EMPTY_THREAD, status: { gpu: DOWN } as never,
        feature: true, count: 2,
      }),
    );
    expect(launchThread).not.toHaveBeenCalled();
  });

  it('без gpu блок не рендерится и launchThread не вызывается', () => {
    (useCatalog as ReturnType<typeof vi.fn>).mockReturnValue(CATALOG);
    renderToStaticMarkup(
      createElement(GpuQueueBlock, {
        projectId: 'p1', sessionId: 's1', thread: EMPTY_THREAD, status: null,
        feature: true, count: 2,
      }),
    );
    expect(launchThread).not.toHaveBeenCalled();
  });
});

// ── Финальное ревью: операция и входы из ИСХОДНОЙ задачи ──

describe('CLOUD_REPRODUCIBLE_OPS', () => {
  it('содержит generate, edit, inpaint', () => {
    expect(CLOUD_REPRODUCIBLE_OPS.has('generate')).toBe(true);
    expect(CLOUD_REPRODUCIBLE_OPS.has('edit')).toBe(true);
    expect(CLOUD_REPRODUCIBLE_OPS.has('inpaint')).toBe(true);
  });
  it('не содержит outpaint, removeBackground, upscale, enhanceFaces', () => {
    expect(CLOUD_REPRODUCIBLE_OPS.has('outpaint')).toBe(false);
    expect(CLOUD_REPRODUCIBLE_OPS.has('removeBackground')).toBe(false);
    expect(CLOUD_REPRODUCIBLE_OPS.has('upscale')).toBe(false);
    expect(CLOUD_REPRODUCIBLE_OPS.has('enhanceFaces')).toBe(false);
  });
});

describe('cloudOriginalParams (финальное ревью)', () => {
  const mkThread = (op: string, prompt = 'тест', extra: { character?: ImageThreadCharacter } = {}) =>
    ({
      id: 't1', file: 'a.png', lineage: [], stacks: [], currentStackId: null, currentStepId: null,
      settings: null, pendingJobId: 'j1', createdAt: '2026-01-01T00:00:00Z',
      launches: [{ jobId: 'j1', baseVersionId: null, baseStepId: null, at: '2026-01-01T00:00:00Z',
        status: 'running', initiator: 'human', prompt, op, ...extra }],
    } as unknown as ImageThread);

  it('outpaint → null (мутация «показывать кнопку для outpaint» красит)', () => {
    expect(cloudOriginalParams(mkThread('outpaint'), 'j1')).toBeNull();
  });
  it('removeBackground → null', () => {
    expect(cloudOriginalParams(mkThread('removeBackground'), 'j1')).toBeNull();
  });
  it('upscale → null', () => {
    expect(cloudOriginalParams(mkThread('upscale'), 'j1')).toBeNull();
  });
  it('enhanceFaces → null', () => {
    expect(cloudOriginalParams(mkThread('enhanceFaces'), 'j1')).toBeNull();
  });
  it('edit → op=edit, prompt из запуска', () => {
    const r = cloudOriginalParams(mkThread('edit', 'мой промпт'), 'j1');
    expect(r).not.toBeNull();
    expect(r!.op).toBe('edit');
    expect(r!.prompt).toBe('мой промпт');
  });
  it('generate → op=generate', () => {
    const r = cloudOriginalParams(mkThread('generate', 'нарисуй'), 'j1');
    expect(r?.op).toBe('generate');
  });
  it('inpaint без маски → null (маска обязательна для inpaint)', () => {
    // threadHasMask читает из стора; в этом тесте стор пуст → no mask
    setThreadMarks('t1', [], null);
    expect(cloudOriginalParams(mkThread('inpaint', 'закрась'), 'j1')).toBeNull();
  });
  it('inpaint с маской → op=inpaint, hasMask=true', () => {
    setThreadMarks('t1', [{ type: 'mask', points: [[0, 0], [10, 10]], width: 3 }], null);
    const r = cloudOriginalParams(mkThread('inpaint', 'закрась'), 'j1');
    expect(r).not.toBeNull();
    expect(r!.op).toBe('inpaint');
    expect(r!.hasMask).toBe(true);
  });
  it('removal: op=inpaint + промпт «удали фон» → removal=true (та же формула, что в launchThread)', () => {
    setThreadMarks('t1', [{ type: 'mask', points: [[0, 0], [10, 10]], width: 3 }], null);
    const r = cloudOriginalParams(mkThread('inpaint', 'удали фон'), 'j1');
    expect(r?.removal).toBe(true);
  });
  it('removal: op=edit + промпт «удали фон» → removal=false (не привязано к op=edit, как в launchThread)', () => {
    const r = cloudOriginalParams(mkThread('edit', 'удали фон'), 'j1');
    expect(r?.removal).toBe(false);
  });
  it('removal: обычный промпт → removal=false', () => {
    const r = cloudOriginalParams(mkThread('edit', 'сделай ярче'), 'j1');
    expect(r?.removal).toBe(false);
  });
  it('character в launch → hasCharacter=true', () => {
    const r = cloudOriginalParams(mkThread('edit', 'тест', { character: { slug: 'c1', photos: [], pick: 'auto', scene: null } }), 'j1');
    expect(r?.hasCharacter).toBe(true);
  });
  it('без character → hasCharacter=false', () => {
    const r = cloudOriginalParams(mkThread('edit', 'тест'), 'j1');
    expect(r?.hasCharacter).toBe(false);
  });
  it('pendingJobId=null → null', () => {
    expect(cloudOriginalParams(mkThread('edit'), null)).toBeNull();
  });
  it('пустой prompt → null', () => {
    expect(cloudOriginalParams(mkThread('edit', '  '), 'j1')).toBeNull();
  });
});

// Мутации: «use pickOp instead of original op» и «show button for outpaint»
describe('GpuQueueBlock: мутации финального ревью', () => {
  const renderWith = (thread: ImageThread) => {
    (useQuote as ReturnType<typeof vi.fn>).mockReturnValue({ quote: QUOTE, loading: false });
    (useCatalog as ReturnType<typeof vi.fn>).mockReturnValue(CATALOG);
    return renderToStaticMarkup(
      createElement(GpuQueueBlock, {
        projectId: 'p1', sessionId: 's1', thread, status: { gpu: DOWN } as never,
        feature: true, count: 2,
      }),
    );
  };

  it('мутация «показывать кнопку для outpaint»: op=outpaint → кнопки нет', () => {
    const thread = {
      ...EMPTY_THREAD,
      launches: [{ jobId: 'j1', baseVersionId: null, baseStepId: null, at: '2026-01-01T00:00:00Z',
        status: 'running', initiator: 'human', prompt: 'расширь', op: 'outpaint' }],
    } as unknown as ImageThread;
    expect(renderWith(thread)).not.toContain('Сделать в облаке');
  });

  it('мутация «use pickOp instead of original op»: thread с mask → op из original (edit), не inpaint', () => {
    (useQuote as ReturnType<typeof vi.fn>).mockReturnValue({ quote: QUOTE, loading: false });
    (useCatalog as ReturnType<typeof vi.fn>).mockReturnValue(CATALOG);
    setThreadMarks('t1', [{ type: 'mask', points: [[0, 0], [10, 10]], width: 3 }], null);
    const { root } = mount(createElement(GpuQueueBlock, {
      projectId: 'p1', sessionId: 's1', thread: EMPTY_THREAD, status: { gpu: DOWN } as never,
      feature: true, count: 2,
    }));
    // op из original task (edit), не из pickOp (inpaint)
    const req = (useQuote as ReturnType<typeof vi.fn>).mock.calls.at(-1)?.[2] as ImageEditQuoteRequest | null | undefined;
    expect(req?.op).toBe('edit');
    act(() => { root.unmount(); });
  });
});
