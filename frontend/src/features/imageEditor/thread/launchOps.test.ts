import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// Запуск нити общим launchThread: операция по состоянию холста, режим подбора, «Вся картинка»,
// пропорции новой картинки. Окружение node: мок API включается ключом localStorage
const storage = new Map<string, string>([['cc-image-editor-mock', 'all']]);
const fakeStorage = (m: Map<string, string>) => ({
  getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => { m.set(k, v); },
  removeItem: (k: string) => { m.delete(k); }, clear: () => m.clear(), key: () => null, length: 0,
}) as Storage;
vi.stubGlobal('localStorage', fakeStorage(storage));
vi.stubGlobal('sessionStorage', fakeStorage(new Map()));
const win = Object.assign(new EventTarget(), {
  setTimeout, clearTimeout, setInterval, clearInterval, innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
});
vi.stubGlobal('window', win);
const toasts: string[] = [];
win.addEventListener('cc-local-toast', e => toasts.push((e as CustomEvent<{ title: string }>).detail.title));

const { setFlagLocal, FLAGS } = await import('../../../lib/featureFlags');
const { imageEditorApi } = await import('../api');
const { pickOp } = await import('../format');
const { __resetPrefs, ensurePrefs, prefsApi } = await import('./prefs');
const { __applyThreads, __resetThreadStore, isWholeImage, setThreadMarks, setWholeImage } = await import('./threadStore');
const { __resetImageModes, setImageMode } = await import('./modeState');
const { launchThread } = await import('./useThreadLaunch');
const { __resetPanelChoice, setCreateRatio, setPanelChoice } = await import('../context/ops');
import type { ImageEditJobInput, ImageEditQuoteRequest } from '../api';
import type { Mark } from '../marks';
import type { ProjectPrefs } from './prefs';
import type { ImageThread, ImageThreadsState } from './threadsApi';

const P = 'p1';
const S = 's1';
const thread = (patch: Partial<ImageThread> = {}): ImageThread => ({
  id: 't1', file: 'images/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null,
  currentStepId: null, settings: null, pendingJobId: null, createdAt: '2026-10-01T00:00:00Z', ...patch,
});
const state = (t: ImageThread): ImageThreadsState => ({ focus: t.id, revision: 1, threads: [t] });
const MASK: Mark = { type: 'mask', points: [[1, 1], [5, 5]], width: 4 };
const PREFS: ProjectPrefs = {
  provider: null, model: null, count: 2, matchSourceSize: true, characterSlug: null,
  create: { provider: 'local', model: 'qwen-image-2.1', count: 1 },
  edit: { provider: 'local', model: 'qwen-image-2.1', count: 1, op: 'edit', editMode: null, ratio: null },
};

let quotes: ImageEditQuoteRequest[];
let jobs: ImageEditJobInput[];
let started: number;

beforeEach(async () => {
  storage.clear();
  storage.set('cc-image-editor-mock', 'all');
  __resetThreadStore();
  __resetPrefs();
  __resetPanelChoice();
  __resetImageModes();
  quotes = [];
  jobs = [];
  started = 0;
  const api = imageEditorApi();
  const quote = api.quote.bind(api);
  vi.spyOn(api, 'quote').mockImplementation((p, req) => { quotes.push(req); return quote(p, req); });
  vi.spyOn(api, 'startJob').mockImplementation(async (_p, input) => { started++; jobs.push(input); return { jobId: 'j1' } as never; });
  vi.stubGlobal('fetch', async () => ({ blob: async () => new Blob(['x']) }));
});
afterEach(() => {
  toasts.length = 0;
  setFlagLocal(FLAGS.imagePanelV5, false);
  vi.restoreAllMocks();
});

describe('без режима (флаг image-panel-v5 выключен)', () => {
  it.each([[false], [true]])('«Авто» уходит операцией pickOp (отметки %s)', async mask => {
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

  it('выбранная операция без промпта: один вариант', async () => {
    const t = thread();
    __applyThreads(S, P, state(t));
    setPanelChoice(P, { op: 'removeBackground' });
    await launchThread(P, S, t, { kind: 'prompt', prompt: '' });
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

  it('«Вся картинка» запуск не меняет', async () => {
    const t = thread();
    __applyThreads(S, P, state(t));
    setThreadMarks(t.id, [MASK], { w: 100, h: 100 });
    setWholeImage(t.id, true);
    await launchThread(P, S, t, { kind: 'prompt', prompt: 'x' });
    expect(quotes.at(-1)).toMatchObject({ op: 'inpaint', hasMask: true });
  });

  it('«По тексту» прежняя: пропорций «Создать» не берёт', async () => {
    const t = thread();
    __applyThreads(S, P, state(t));
    setCreateRatio(P, '9:16');
    setPanelChoice(P, { op: 'generate' });
    await launchThread(P, S, t, { kind: 'prompt', prompt: 'кот' });
    expect(quotes.at(-1)?.op).toBe('generate');
    expect(jobs.at(-1)?.aspectRatio).toBeUndefined();
  });
});

describe('с режимом (флаг image-panel-v5)', () => {
  beforeEach(async () => {
    setFlagLocal(FLAGS.imagePanelV5, true);
    vi.spyOn(prefsApi, 'subscribe').mockImplementation(() => () => {});
    vi.spyOn(prefsApi, 'put').mockImplementation(async (_p, v) => v);
    localStorage.setItem(`cc-image-prefs-migrated:${P}`, '1');
    vi.spyOn(prefsApi, 'get').mockResolvedValue(PREFS);
    await ensurePrefs(P);
  });

  it('новые отметки снимают «Вся картинка»', () => {
    setThreadMarks('t1', [MASK], { w: 100, h: 100 });
    setWholeImage('t1', true);
    expect(isWholeImage('t1')).toBe(true);
    setThreadMarks('t1', [MASK, MASK], { w: 100, h: 100 });
    expect(isWholeImage('t1')).toBe(false);
  });

  it('«Вся картинка» при отметках идёт правкой без маски, «Отмеченное» — инпейнтом', async () => {
    const t = thread();
    __applyThreads(S, P, state(t));
    setImageMode(S, 'edit');
    setThreadMarks(t.id, [MASK], { w: 100, h: 100 });
    setWholeImage(t.id, true);
    expect(await launchThread(P, S, t, { kind: 'prompt', prompt: 'небо розовое' })).toBe(true);
    expect(quotes.at(-1)).toMatchObject({ op: 'edit', hasMask: false });
    expect(jobs.at(-1)?.marks).toBeUndefined();
    expect(toasts).toEqual([]);
    setWholeImage(t.id, false);
    await launchThread(P, S, t, { kind: 'prompt', prompt: 'небо розовое' });
    expect(quotes.at(-1)).toMatchObject({ op: 'inpaint', hasMask: true });
  });

  it('пропорции новой картинки уходят только с генерацией в «Создать»', async () => {
    const t = thread();
    __applyThreads(S, P, state(t));
    setCreateRatio(P, '9:16');
    setImageMode(S, 'create');
    await launchThread(P, S, t, { kind: 'prompt', prompt: 'кот' });
    expect(quotes.at(-1)?.op).toBe('generate');
    expect(jobs.at(-1)?.aspectRatio).toBe('9:16');
    setImageMode(S, 'edit');
    await launchThread(P, S, t, { kind: 'prompt', prompt: 'кот' });
    expect(jobs.at(-1)?.aspectRatio).toBeUndefined();
    setImageMode(S, 'create');
    setCreateRatio(P, null);
    await launchThread(P, S, t, { kind: 'prompt', prompt: 'кот' });
    expect(jobs.at(-1)?.aspectRatio).toBeUndefined();
  });
});
