// Шаг 36: чистая логика видимости строки ожидания GPU и кнопки «Сделать в облаке»
// (flag, gpu, down, наличие облачного исполнителя/котировки) + «запуск только по клику»
// + (ревью Глеба) защита от двойного клика (ref-guard) и запуск по котировке из хука.
// Окружение node, без jsdom: статичную часть рендерим renderToStaticMarkup — там эффекты
// не гоняются, поэтому audioApi.startJob на монтировании не вызывается; интерактивную часть
// (диалог, двойной клик) рендерим на минимальном фейк-DOM для createRoot.

import { afterAll, afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { renderToStaticMarkup } from 'react-dom/server';
import { setAllFlags } from '../../../lib/featureFlags';
import { audioApi, type AudioCatalog, type AudioModelInfo, type AudioOp, type AudioQuote, type AudioThread } from '../api';
import {
  isStandDown, cloudCandidate, showCloudButton, isCloudButtonEnabled, showQueueLine, cloudQuoteRequest,
  makeLaunchGuard, GpuQueueBlock, originalTaskParams, inputSatisfied, opInput, opInputField,
} from './gpuCard';
import type { LocalGpuQueueInfo } from '../../../types/localGpuQueue';
import type { JobProgress } from './threadStore';

// ── Мок-данные ──

const model = (ops: AudioOp[]): AudioModelInfo => ({
  id: 'auto', label: 'Авто',
  caps: {
    ops, languages: [], voiceKinds: [], producesFiles: [],
    license: { label: 'CC0', kind: 'permissive' }, priceUnit: 'usd',
  },
});

// fal — облачный (доступен, умеет speak), local — локальные (доступен, тоже умеет)
const CATALOG: AudioCatalog = {
  autoModelId: 'auto', maxCount: 4, autoProviders: ['fal', 'local'],
  providers: [
    { key: 'fal', label: 'Fal', priceUnit: 'usd', available: true, reason: null, models: [model(['speak'])] },
    { key: 'local', label: 'Локальные модели', priceUnit: 'free', available: true, reason: null, models: [model(['speak'])] },
  ],
};
// Только локальные: облачного исполнителя нет
const LOCAL_ONLY: AudioCatalog = {
  autoModelId: 'auto', maxCount: 4, autoProviders: ['local'],
  providers: [
    { key: 'local', label: 'Локальные модели', priceUnit: 'free', available: true, reason: null, models: [model(['speak'])] },
  ],
};
// Облачный исполнитель умеет и озвучку (text), и песню/эффект (prompt) — для проверки выбора поля
const CATALOG_ALL: AudioCatalog = {
  autoModelId: 'auto', maxCount: 4, autoProviders: ['fal', 'local'],
  providers: [
    { key: 'fal', label: 'Fal', priceUnit: 'usd', available: true, reason: null, models: [model(['speak', 'song', 'sfx'])] },
    { key: 'local', label: 'Локальные модели', priceUnit: 'free', available: true, reason: null, models: [model(['speak', 'song', 'sfx'])] },
  ],
};

const DOWN: LocalGpuQueueInfo = { downSince: 1_700_000_000_000 };
const QUEUED: LocalGpuQueueInfo = { ahead: 0, startInSeconds: 40 };

const THREAD: AudioThread = {
  id: 't1', file: 'a.mp3', lineage: [], draftFolder: null, createdAt: '2026-01-01T00:00:00Z',
  versions: [], currentVersionId: null,
  // M1: исходный вход задачи — промпт запуска (совпадает с JOB.jobId), который originalTaskParams
  // восстанавливает и передаёт в котировку и запуск
  launches: [{ jobId: 'j1', baseVersionId: null, at: '2026-01-01T00:00:00Z', status: 'running', initiator: 'human', prompt: 'тестовый промпт', license: null }],
  settings: null,
};
// Нить с пустым входом: для операций, где вход обязателен (speak), кнопки не будет
const THREAD_EMPTY: AudioThread = {
  ...THREAD,
  launches: [{ jobId: 'j1', baseVersionId: null, at: '2026-01-01T00:00:00Z', status: 'running', initiator: 'human', prompt: '', license: null }],
};
const JOB: JobProgress = {
  jobId: 'j1', sessionId: 's1', threadId: 't1', stage: 'queued',
  queuePosition: 0, etaSeconds: null, variant: 1, count: 1, gpu: DOWN,
};

// Котировка, которую «даёт» хук useCloudQuote: quoteId, по которому идёт запуск
const HOOK_QUOTE: AudioQuote = {
  quoteId: 'q-hook', mode: 'voice', op: 'speak', provider: 'fal', model: 'auto', count: 1,
  voiceKind: null, price: { amount: 0.04, unit: 'usd', approx: true, source: 'provider', eta: null, queueLength: null },
  license: 'CC0', heavy: false, expiresAt: '2026-01-01T00:00:00Z',
};

// ── Мок-модули (hoisted: фабрики vi.mock не видят верхний скоуп без vi.hoisted) ──
// threadStore мокаем, чтобы getCatalog/useAudioStoreVersion были управляемыми и не тянули
// живой подписчик (ensureLive → audioApi.subscribe) в SSR-рендер.

const { getCatalog, useAudioStoreVersion } = vi.hoisted(() => ({
  getCatalog: vi.fn(),
  useAudioStoreVersion: vi.fn(() => 0),
}));
vi.mock('./threadStore', () => ({ getCatalog, useAudioStoreVersion }));

let startJobSpy: ReturnType<typeof vi.spyOn>;
let quoteSpy: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
  vi.clearAllMocks();
  setAllFlags({ 'local-model-stats': true });
  getCatalog.mockReturnValue(CATALOG);
  startJobSpy = vi.spyOn(audioApi, 'startJob').mockResolvedValue({ jobId: 'j1' });
  quoteSpy = vi.spyOn(audioApi, 'quote').mockResolvedValue(HOOK_QUOTE);
});

afterEach(() => { vi.restoreAllMocks(); });

// ── Чистая логика видимости ──

describe('isStandDown', () => {
  it('false без gpu', () => expect(isStandDown(null)).toBe(false));
  it('false без downSince', () => expect(isStandDown({})).toBe(false));
  it('true при downSince', () => expect(isStandDown(DOWN)).toBe(true));
});

describe('cloudCandidate', () => {
  it('null без каталога', () => expect(cloudCandidate(null, 'speak')).toBeNull());
  it('null без операции', () => expect(cloudCandidate(CATALOG, null)).toBeNull());
  it('null, если только локальные', () => expect(cloudCandidate(LOCAL_ONLY, 'speak')).toBeNull());
  it('даёт облачного исполнителя «Автоматически»', () => {
    expect(cloudCandidate(CATALOG, 'speak')?.key).toBe('fal');
  });
});

describe('showCloudButton (видимость)', () => {
  it('true: флаг + down + облачный исполнитель', () => {
    expect(showCloudButton(true, DOWN, CATALOG, 'speak')).toBe(true);
  });
  it('false: флаг выключен (мутация «показ с выключенным флагом»)', () => {
    expect(showCloudButton(false, DOWN, CATALOG, 'speak')).toBe(false);
  });
  it('false: стенд не лежит (мутация «показ без down»)', () => {
    expect(showCloudButton(true, QUEUED, CATALOG, 'speak')).toBe(false);
  });
  it('false: нет gpu', () => expect(showCloudButton(true, null, CATALOG, 'speak')).toBe(false));
  it('false: нет облачного исполнителя (только локальные)', () => {
    expect(showCloudButton(true, DOWN, LOCAL_ONLY, 'speak')).toBe(false);
  });
});

describe('isCloudButtonEnabled (кликабельность)', () => {
  const quote = (amount: number | null) => ({
    quoteId: 'q1', mode: 'voice', op: 'speak', provider: 'fal', model: 'auto', count: 1,
    voiceKind: null,
    price: { amount, unit: 'usd', approx: true, source: 'provider', eta: null, queueLength: null },
    license: 'CC0', heavy: false, expiresAt: '2026-01-01T00:00:00Z',
  });
  it('true: котировка с ценой', () => expect(isCloudButtonEnabled(quote(0.04))).toBe(true));
  it('false: нет котировки (грузится)', () => expect(isCloudButtonEnabled(null)).toBe(false));
  it('false: котировка без цены', () => expect(isCloudButtonEnabled(quote(null))).toBe(false));
});

describe('showQueueLine', () => {
  it('false: флаг выключен', () => expect(showQueueLine(false, QUEUED)).toBe(false));
  it('false: нет gpu', () => expect(showQueueLine(true, null)).toBe(false));
  it('true: флаг + gpu', () => expect(showQueueLine(true, QUEUED)).toBe(true));
  // Шаг 2: gpu есть (running) → showQueueLine true → собственная полоса карточки прячется
  it('gpu есть (running) → showQueueLine true → без собственной полосы', () =>
    expect(showQueueLine(true, { running: true, steps: { step: 4, total: 10 } })).toBe(true));
});

describe('cloudQuoteRequest', () => {
  it('null без облачного исполнителя', () => expect(cloudQuoteRequest(LOCAL_ONLY, 'speak', 'voice', 2, 'п')).toBeNull());
  it('null без каталога', () => expect(cloudQuoteRequest(null, 'speak', 'voice', 2, 'п')).toBeNull());
  it('null без режима', () => expect(cloudQuoteRequest(CATALOG, 'speak', null, 2, 'п')).toBeNull());
  it('строит запрос к облачному исполнителю с «Авто»-моделью', () => {
    expect(cloudQuoteRequest(CATALOG, 'speak', 'voice', 2, 'тестовый промпт'))
      .toMatchObject({ mode: 'voice', operation: 'speak', provider: 'fal', model: 'auto', count: 2, text: 'тестовый промпт', prompt: null });
  });
  it('clamp count в 1', () => expect(cloudQuoteRequest(CATALOG, 'speak', 'voice', 0, 'п')?.count).toBe(1));
  it('speak (text) → вход в text, а не prompt (мутация «всегда prompt» красит)', () => {
    const req = cloudQuoteRequest(CATALOG_ALL, 'speak', 'voice', 1, 'текст для озвучки');
    expect(req?.text).toBe('текст для озвучки');
    expect(req?.prompt).toBeNull();
  });
  it('song (prompt) → вход в prompt', () => {
    const req = cloudQuoteRequest(CATALOG_ALL, 'song', 'music', 1, 'стиль: джаз');
    expect(req?.prompt).toBe('стиль: джаз');
    expect(req?.text).toBeNull();
  });
  it('sfx (prompt) → вход в prompt', () => {
    const req = cloudQuoteRequest(CATALOG_ALL, 'sfx', 'music', 1, 'скрип двери');
    expect(req?.prompt).toBe('скрип двери');
    expect(req?.text).toBeNull();
  });
});

describe('opInput / opInputField (общая сборка входа: один источник правды)', () => {
  it('speak → text', () => expect(opInput('speak', 'текст')).toEqual({ text: 'текст', prompt: null }));
  it('song → prompt', () => expect(opInput('song', 'стиль')).toEqual({ text: null, prompt: 'стиль' }));
  it('denoise (none) → оба поля null', () => expect(opInput('denoise', 'x')).toEqual({ text: null, prompt: null }));
  it('операция не в каталоге → null (поле нельзя определить)', () => expect(opInput(null, 'x')).toBeNull());
  it('opInputField: speak → text, song → prompt, null → null', () => {
    expect(opInputField('speak')).toBe('text');
    expect(opInputField('song')).toBe('prompt');
    expect(opInputField(null)).toBeNull();
  });
});

describe('originalTaskParams (M1: исходный вход из нити)', () => {
  it('возвращает промпт запуска по jobId', () => {
    expect(originalTaskParams(THREAD, 'j1')).toBe('тестовый промпт');
  });
  it('null, если jobId не задан', () => expect(originalTaskParams(THREAD, null)).toBeNull());
  it('null, если запуск не найден', () => expect(originalTaskParams(THREAD, 'j-dругой')).toBeNull());
  it('null, если промпт пуст', () => expect(originalTaskParams(THREAD_EMPTY, 'j1')).toBeNull());
});

describe('inputSatisfied (M1: вход обязателен для операций с text/prompt)', () => {
  it('speak (text) с промптом — ок', () => expect(inputSatisfied('speak', 'текст')).toBe(true));
  it('speak (text) без промпта — нет (мутация «не проверять вход» красит)', () => expect(inputSatisfied('speak', null)).toBe(false));
  it('song (prompt) без промпта — нет', () => expect(inputSatisfied('song', null)).toBe(false));
  it('denoise (none) без промпта — ок', () => expect(inputSatisfied('denoise', null)).toBe(true));
  it('op null — нет (поле нельзя определить: кнопку не показываем)', () => expect(inputSatisfied(null, null)).toBe(false));
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
    getCatalog.mockReturnValue(CATALOG);
    const html = renderToStaticMarkup(
      createElement(GpuQueueBlock, {
        scope: 'p1', sessionId: 's1', thread: THREAD, job: JOB,
        feature: true, op: 'speak', mode: 'voice', count: 1,
      }),
    );
    expect(html).toContain('Сделать в облаке');
    expect(startJobSpy).not.toHaveBeenCalled();
  });

  it('без gpu блок не рендерится и startJob не вызывается', () => {
    getCatalog.mockReturnValue(CATALOG);
    const html = renderToStaticMarkup(
      createElement(GpuQueueBlock, {
        scope: 'p1', sessionId: 's1', thread: THREAD, job: null,
        feature: true, op: 'speak', mode: 'voice', count: 1,
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
// window нужен showToast (window.dispatchEvent) и useMediaQuery (matchMedia)
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
  getCatalog.mockReturnValue(CATALOG);
  vi.useFakeTimers();
  const { root, container } = mount(createElement(GpuQueueBlock, {
    scope: 'p1', sessionId: 's1', thread: THREAD, job: JOB,
    feature: true, op: 'speak', mode: 'voice', count: 1,
  }));
  // Прогоняем 300 мс: таймаут хука срабатывает, audioApi.quote (мок) разрешает котировку,
  // setState перерисовывает кнопку с ценой. Await — чтобы микротаск .then() отработал.
  await act(async () => { vi.advanceTimersByTime(300); });
  return { root, container };
}

describe('GpuQueueBlock: интерактив (ревью)', () => {
  afterEach(() => { vi.useRealTimers(); });

  it('два клика подряд → startJob вызван один раз (мутация: убрать guard)', async () => {
    const { root, container } = await renderBlockDown();
    const btn = findButton(container, 'Сделать в облаке · ≈ $0.04');
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
    const btn = findButton(container, 'Сделать в облаке · ≈ $0.04');
    expect(btn, 'кнопка «Сделать в облаке» не найдена').not.toBeNull();
    act(() => { btn!.onClick(); });
    const confirm = findButton(fdoc.body, 'Запустить');
    expect(confirm, 'кнопка «Запустить» не найдена').not.toBeNull();
    act(() => { confirm!.onClick(); });
    // Запуск шёл по quoteId из хука, а не по повторной котировке
    expect(startJobSpy).toHaveBeenCalledTimes(1);
    expect(startJobSpy).toHaveBeenCalledWith('p1', 's1', expect.objectContaining({ quoteId: 'q-hook' }));
    // audioApi.quote вызван ровно один раз — самим хуком; launch() его не повторяет
    expect(quoteSpy).toHaveBeenCalledTimes(1);
    act(() => { root.unmount(); });
  });
});

// ── M1: исходный вход задачи в котировке и запуске ──

describe('GpuQueueBlock: M1 (исходный вход)', () => {
  afterEach(() => { vi.useRealTimers(); });

  // Рендер блока с заданной нитью: доводим котировку хука до готовности
  async function renderWithThread(thread: AudioThread) {
    getCatalog.mockReturnValue(CATALOG);
    vi.useFakeTimers();
    const { root, container } = mount(createElement(GpuQueueBlock, {
      scope: 'p1', sessionId: 's1', thread, job: JOB,
      feature: true, op: 'speak', mode: 'voice', count: 1,
    }));
    await act(async () => { vi.advanceTimersByTime(300); });
    return { root, container };
  }

  it('пустой вход у speak → кнопки нет (мутация «не требовать вход» красит)', async () => {
    const { root, container } = await renderWithThread(THREAD_EMPTY);
    // Кнопка «Сделать в облаке» не рендерится: вход обязателен, а промпт пуст
    expect(findButton(container, 'Сделать в облаке · ≈ $0.04')).toBeNull();
    act(() => { root.unmount(); });
  });

  it('отсутствующий запуск (jobId не в нити) → кнопки нет', async () => {
    // jobId 'j1' есть в THREAD.launches, но если нить не несёт этот запуск — входа нет
    const threadNoLaunch: AudioThread = { ...THREAD, launches: [] };
    const { root, container } = await renderWithThread(threadNoLaunch);
    expect(findButton(container, 'Сделать в облаке · ≈ $0.04')).toBeNull();
    act(() => { root.unmount(); });
  });

  it('вход есть → тела котировки и запуска содержат ОДИНАКОВЫЙ text (speak: поле text, проверка равенства)', async () => {
    const { root, container } = await renderWithThread(THREAD);
    const btn = findButton(container, 'Сделать в облаке · ≈ $0.04');
    expect(btn, 'кнопка «Сделать в облаке» не найдена').not.toBeNull();
    act(() => { btn!.onClick(); });
    const confirm = findButton(fdoc.body, 'Запустить');
    expect(confirm, 'кнопка «Запустить» не найдена').not.toBeNull();
    act(() => { confirm!.onClick(); });
    // Котировка (хук) и запуск получили один и тот же исходный вход, в поле, которого требует операция
    const quoteReq = quoteSpy.mock.calls[0]?.[2] as { text?: string | null; prompt?: string | null } | undefined;
    const launchInput = startJobSpy.mock.calls[0]?.[2] as { text?: string | null; prompt?: string | null } | undefined;
    expect(quoteReq?.text, 'котировка без text').toBe('тестовый промпт');
    expect(quoteReq?.prompt, 'у speak не должно быть prompt').toBeNull();
    expect(launchInput?.text, 'запуск без text').toBe('тестовый промпт');
    expect(launchInput?.prompt, 'у speak не должно быть prompt').toBeNull();
    // Поля котировки и запуска равны (один источник правды)
    expect(launchInput?.text).toBe(quoteReq?.text);
    expect(launchInput?.prompt).toBe(quoteReq?.prompt);
    act(() => { root.unmount(); });
  });

  it('M3: исключение в запуске → guard освобождён через finally → повторный запуск проходит (мутация: убрать finally)', async () => {
    const { root, container } = await renderWithThread(THREAD);
    const btn = findButton(container, 'Сделать в облаке · ≈ $0.04');
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
