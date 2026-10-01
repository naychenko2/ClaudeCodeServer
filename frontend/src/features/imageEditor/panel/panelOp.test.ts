import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// Окружение node: мок API включается ключом localStorage, его таймеры ставятся через window
const storage = new Map<string, string>([['cc-image-editor-mock', 'all']]);
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => storage.get(k) ?? null, setItem: (k: string, v: string) => { storage.set(k, v); },
  removeItem: (k: string) => { storage.delete(k); }, clear: () => storage.clear(), key: () => null, length: 0,
} as Storage;
// Адрес файла проекта берёт токен из sessionStorage
(globalThis as unknown as { sessionStorage: Storage }).sessionStorage = {
  getItem: () => null, setItem: () => {}, removeItem: () => {}, clear: () => {}, key: () => null, length: 0,
} as Storage;
const win = Object.assign(new EventTarget(), { setTimeout, clearTimeout, setInterval, clearInterval });
vi.stubGlobal('window', win);
const toasts: string[] = [];
win.addEventListener('cc-local-toast', e => toasts.push((e as CustomEvent<{ title: string }>).detail.title));
import { __resetComposerStrips, registerComposerSubmit } from '../../../lib/composerStrips';
import { imageEditorApi, type ImageEditQuoteRequest } from '../api';
import { pickOp } from '../format';
import type { Mark } from '../marks';
import { __resetPrefs } from '../thread/prefs';
import type { ImageThread, ImageThreadsState } from '../thread/threadsApi';
import { __applyThreads, __resetThreadStore, setThreadMarks } from '../thread/threadStore';
import { launchThread } from '../thread/useThreadLaunch';
import { panelRun } from './ImagesPanel';
import {
  __resetPanelChoice, footPrice, opBlockReason, opHint, PANEL_OPS, queueText, resolveOp, setPanelChoice,
} from './panelOp';

const P = 'p1';
const S = 's1';
const thread = (patch: Partial<ImageThread> = {}): ImageThread => ({
  id: 't1', file: 'images/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null,
  currentStepId: null, settings: null, pendingJobId: null, createdAt: '2026-10-01T00:00:00Z', ...patch,
});
const state = (t: ImageThread | null): ImageThreadsState => ({ focus: t?.id ?? null, revision: 1, threads: t ? [t] : [] });
const MASK: Mark = { type: 'mask', points: [[1, 1], [5, 5]], width: 4 };

const STATES: [boolean, boolean][] = [[false, false], [true, false], [true, true]];

describe('«Авто» — ровно pickOp', () => {
  it.each(STATES)('картинка %s, отметки %s', (hasImage, hasMask) => {
    expect(resolveOp('auto', hasImage, hasMask)).toBe(pickOp(hasImage, hasMask));
    expect(opBlockReason('auto', hasImage, hasMask)).toBe('');
  });

  it('строка «Сейчас это …» называет фактическую операцию', () => {
    expect(opHint('auto', true, false)).toEqual(['Сейчас это ', 'правка', ': картинка выбрана, отметок нет. Так ведёт себя полоса сейчас.']);
    expect(opHint('auto', false, false)[1]).toBe('по тексту');
    expect(opHint('auto', true, true)[1]).toBe('по отмеченному');
    // Слово строки — подпись пилюли той операции, которую выберет pickOp
    for (const [img, mask] of STATES) {
      const label = PANEL_OPS.find(o => o.op === pickOp(img, mask))!.label.toLowerCase();
      expect(opHint('auto', img, mask)[1]).toBe(label);
    }
  });
});

describe('операции и причины', () => {
  it('без картинки — только «Авто» и «По тексту»', () => {
    const open = PANEL_OPS.filter(o => !opBlockReason(o.op, false, false)).map(o => o.op);
    expect(open).toEqual(['auto', 'generate']);
    expect(opBlockReason('removeBackground', false, false)).toBe('Сначала выберите картинку в ленте или загрузите её');
  });

  it('«По отмеченному» ждёт отметки', () => {
    expect(opBlockReason('inpaint', true, false)).toBe('Отметьте место кистью в редакторе картинки');
    expect(opBlockReason('inpaint', true, true)).toBe('');
  });
});

describe('цена низа в две строки', () => {
  it('платная: итог и короткая расшифровка', () => {
    expect(footPrice({ amount: 0.08, unit: 'usd', approx: true }, 2)).toEqual(['≈ $0.08', '2 × $0.04']);
    expect(footPrice({ amount: 4, unit: 'credits', approx: false }, 2)).toEqual(['4 кредита', '2 × 2 кр.']);
  });

  it('бесплатная: время и очередь GPU; очередь бейджем, только непустая', () => {
    expect(footPrice({ amount: 0, unit: 'free', approx: false, etaSeconds: 30, queueLength: 2 }, 1))
      .toEqual(['Бесплатно', '≈ 30 с · очередь GPU: 2']);
    expect(queueText({ unit: 'free', queueLength: 2 })).toBe('GPU: перед вами 2 в очереди');
    expect(queueText({ unit: 'free', queueLength: 0 })).toBeUndefined();
    expect(queueText({ unit: 'usd', queueLength: 3 })).toBeUndefined();
  });
});

describe('запуск: операция панели идёт общим launchThread', () => {
  let quotes: ImageEditQuoteRequest[];
  let started: number;

  beforeEach(() => {
    __resetThreadStore();
    __resetPrefs();
    __resetPanelChoice();
    __resetComposerStrips();
    quotes = [];
    started = 0;
    const api = imageEditorApi();
    const quote = api.quote.bind(api);
    vi.spyOn(api, 'quote').mockImplementation((p, req) => { quotes.push(req); return quote(p, req); });
    vi.spyOn(api, 'startJob').mockImplementation(async () => { started++; return { jobId: 'j1' } as never; });
    vi.stubGlobal('fetch', async () => ({ blob: async () => new Blob(['x']) }));
  });
  afterEach(() => {
    toasts.length = 0;
    vi.restoreAllMocks();
  });

  it.each(STATES.filter(([img]) => img))('«Авто» уходит операцией pickOp (отметки %s/%s)', async (_img, mask) => {
    const t = thread();
    __applyThreads(S, P, state(t));
    if (mask) setThreadMarks(t.id, [MASK], { w: 100, h: 100 });
    // Маску в node не нарисовать (нет canvas) — операцию и маску проверяем по котировке
    await launchThread(P, S, t, { kind: 'prompt', prompt: 'сделай небо розовым' });
    expect(quotes.at(-1)?.op).toBe(pickOp(true, mask));
    expect(quotes.at(-1)?.hasMask).toBe(mask);
    expect(quotes.at(-1)?.mode).toBe('auto');
  });

  it('режим подбора уходит в котировку у модели «Авто»', async () => {
    const t = thread();
    __applyThreads(S, P, state(t));
    setPanelChoice(P, { mode: 'photoreal' });
    await launchThread(P, S, t, { kind: 'prompt', prompt: 'сделай небо розовым' });
    expect(quotes.at(-1)?.mode).toBe('photoreal');
  });

  it('кнопка низа с «Убрать фон»: один вариант, без промпта, тем же launchThread', async () => {
    const t = thread();
    __applyThreads(S, P, state(t));
    setPanelChoice(P, { op: 'removeBackground' });
    await panelRun(P, S, t, true);
    expect(quotes.at(-1)).toMatchObject({ op: 'removeBackground', count: 1 });
    expect(started).toBe(1);
    expect(toasts).toEqual([]);
  });

  it('недоступная операция не запускается', async () => {
    const t = thread();
    __applyThreads(S, P, state(t));
    setPanelChoice(P, { op: 'inpaint' });
    expect(await launchThread(P, S, t, { kind: 'prompt', prompt: 'кот' })).toBe(false);
    expect(started).toBe(0);
    expect(toasts).toEqual(['Отметьте место кистью в редакторе картинки']);
  });

  it('операция с промптом: кнопка низа отправляет само поле ввода режима «Картинка»', async () => {
    const t = thread();
    __applyThreads(S, P, state(t));
    const asked: string[] = [];
    const off = registerComposerSubmit(S, mode => asked.push(mode));
    await panelRun(P, S, t, false);
    off();
    expect(asked).toEqual(['image']);
    expect(started).toBe(0);
  });
});
