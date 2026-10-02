import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

// Окружение node: мок API включается ключом localStorage, таймеры и событие storage — через window
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
const { loadCatalog } = await import('./catalog');
const { __resetPrefs, ensurePrefs, getPrefs, prefsApi, setPrefs } = await import('./prefs');
const { __applyThreads, __resetThreadStore, setThreadMarks } = await import('./threadStore');
const { threadsApi } = await import('./threadsApi');
const { createDraft, pickByHuman, releaseFocus } = await import('./actions');
const { __resetImageModes, effectiveImageMode, getStoredImageMode, setImageMode } = await import('./modeState');
const { launchThread, useThreadLaunch } = await import('./useThreadLaunch');
const { __resetPanelChoice, setPanelChoice } = await import('../panel/panelOp');
import type { ImageEditQuoteRequest } from '../api';
import type { Mark } from '../marks';
import type { ImagePrefsChangedEvent, ProjectPrefs } from './prefs';
import type { ImageThread, ImageThreadsState } from './threadsApi';

const P = 'p1';
const S = 's1';
const thread = (patch: Partial<ImageThread> = {}): ImageThread => ({
  id: 't1', file: 'images/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null,
  currentStepId: null, settings: null, pendingJobId: null, createdAt: '2026-10-01T00:00:00Z', ...patch,
});
const DRAFT = thread({ id: 'd1', file: null, draftFolder: '' });
const state = (t: ImageThread | null, revision = 1, extra: ImageThread[] = []): ImageThreadsState =>
  ({ focus: t?.id ?? null, revision, threads: [...(t ? [t] : []), ...extra] });
const MASK: Mark = { type: 'mask', points: [[1, 1], [5, 5]], width: 4 };
const DEF: ProjectPrefs = { provider: null, model: null, count: 2, matchSourceSize: true, characterSlug: null };
// «Создать» — Soul (маску не умеет), «Править» — FLUX Fill (по тексту не рисует)
const MODES: ProjectPrefs = {
  ...DEF,
  create: { provider: 'higgsfield', model: 'soul_2', count: 1 },
  edit: { provider: 'fal', model: 'fal-ai/flux-pro/v1/fill', count: 3, op: 'edit', editMode: null, ratio: null },
};
const tick = () => new Promise(r => setTimeout(r, 0));

let emitPrefs: (e: ImagePrefsChangedEvent) => void = () => {};
let puts: ProjectPrefs[];

async function withPrefs(v: ProjectPrefs) {
  localStorage.setItem(`cc-image-prefs-migrated:${P}`, '1');
  vi.spyOn(prefsApi, 'get').mockResolvedValue(v);
  await ensurePrefs(P);
}

beforeEach(() => {
  storage.clear();
  storage.set('cc-image-editor-mock', 'all');
  setFlagLocal(FLAGS.imagePanelV5, true);
  __resetThreadStore();
  __resetPrefs();
  __resetPanelChoice();
  __resetImageModes();
  puts = [];
  vi.spyOn(prefsApi, 'subscribe').mockImplementation(h => { emitPrefs = h; return () => {}; });
  vi.spyOn(prefsApi, 'put').mockImplementation(async (_p, v) => { puts.push(v); return { ...getPrefs(P), ...v }; });
});
afterEach(() => {
  toasts.length = 0;
  setFlagLocal(FLAGS.imagePanelV5, false);
  vi.restoreAllMocks();
});

describe('режим «Создать / Править» на чат', () => {
  it('«Править» без картинки не действует; не выбирали — по картинке', () => {
    expect(effectiveImageMode('edit', false)).toBe('create');
    expect(effectiveImageMode(null, false)).toBe('create');
    expect(effectiveImageMode(null, true)).toBe('edit');
    expect(effectiveImageMode('create', true)).toBe('create');
  });

  it('туда-обратно: выбор человеком → «Править», черновик → «Создать», снова выбор → «Править»', async () => {
    const t = thread();
    __applyThreads(S, P, state(null, 1, [t]));
    vi.spyOn(threadsApi, 'focus').mockImplementation(async (_p, _s, id) => state(id ? t : null, 2, [t]));
    expect(await pickByHuman(P, S, t.id, false)).toBe(true);
    expect(getStoredImageMode(S)).toBe('edit');
    expect(localStorage.getItem(`cc-image-mode:${S}`)).toBe('edit');

    vi.spyOn(threadsApi, 'create').mockResolvedValue(state(DRAFT, 3, [t]));
    await createDraft(P, S, '');
    expect(getStoredImageMode(S)).toBe('create');
    // Черновик без файла «Править» не включает
    expect(effectiveImageMode(getStoredImageMode(S), false)).toBe('create');

    await pickByHuman(P, S, t.id, true);
    expect(getStoredImageMode(S)).toBe('edit');
  });

  it('снятие выбора человеком → «Создать»', async () => {
    const t = thread();
    __applyThreads(S, P, state(t));
    setImageMode(S, 'edit');
    // Файл без шагов — пустая нить: ✕ убирает её из ленты целиком
    vi.spyOn(threadsApi, 'remove').mockResolvedValue(state(null, 2));
    await releaseFocus(P, S, t);
    expect(getStoredImageMode(S)).toBe('create');
  });

  it('агент: выбор режим не меняет, снятие выбора → «Создать»', () => {
    const t = thread();
    __applyThreads(S, P, state(null));
    setImageMode(S, 'create');
    // Выбор агента приходит состоянием с сервера, мимо действий человека
    __applyThreads(S, P, state(t, 2));
    expect(getStoredImageMode(S)).toBe('create');
    setImageMode(S, 'edit');
    __applyThreads(S, P, state(null, 3, [t]));
    expect(getStoredImageMode(S)).toBe('create');
  });

  it('другая вкладка сменила режим — событие storage', () => {
    expect(getStoredImageMode(S)).toBeNull();
    win.dispatchEvent(Object.assign(new Event('storage'), { key: `cc-image-mode:${S}`, newValue: 'edit' }));
    expect(getStoredImageMode(S)).toBe('edit');
  });

  it('флаг выключен — режим не пишется', async () => {
    setFlagLocal(FLAGS.imagePanelV5, false);
    const t = thread();
    __applyThreads(S, P, state(t));
    await pickByHuman(P, S, t.id, true);
    expect(getStoredImageMode(S)).toBeNull();
    expect(localStorage.getItem(`cc-image-mode:${S}`)).toBeNull();
  });
});

describe('запуск по режиму', () => {
  let quotes: ImageEditQuoteRequest[];
  let started: number;

  beforeEach(() => {
    quotes = [];
    started = 0;
    const api = imageEditorApi();
    const quote = api.quote.bind(api);
    vi.spyOn(api, 'quote').mockImplementation((p, req) => { quotes.push(req); return quote(p, req); });
    vi.spyOn(api, 'startJob').mockImplementation(async () => { started++; return { jobId: 'j1' } as never; });
    vi.stubGlobal('fetch', async () => ({ blob: async () => new Blob(['x']) }));
  });

  it('«Создать» при выбранной картинке с отметками рисует с нуля выбором «Создать»', async () => {
    await withPrefs(MODES);
    const t = thread();
    __applyThreads(S, P, state(t));
    setThreadMarks(t.id, [MASK], { w: 100, h: 100 });
    setImageMode(S, 'create');
    expect(await launchThread(P, S, t, { kind: 'prompt', prompt: 'кот' })).toBe(true);
    expect(quotes.at(-1)).toMatchObject({ provider: 'higgsfield', model: 'soul_2', op: 'generate', count: 1, hasMask: false });
    expect(toasts).toEqual([]);
  });

  it('«Править»: выбор «Править», «Изменить» с отметками уходит инпейнтом', async () => {
    await withPrefs(MODES);
    const t = thread();
    __applyThreads(S, P, state(t));
    setImageMode(S, 'edit');
    await launchThread(P, S, t, { kind: 'prompt', prompt: 'небо розовое' });
    expect(quotes.at(-1)).toMatchObject({ provider: 'fal', model: 'fal-ai/flux-pro/v1/fill', op: 'edit', count: 3 });
    setThreadMarks(t.id, [MASK], { w: 100, h: 100 });
    await launchThread(P, S, t, { kind: 'prompt', prompt: 'небо розовое' });
    // Маску в node не нарисовать (нет canvas) — операцию и маску проверяем по котировке
    expect(quotes.at(-1)).toMatchObject({ op: 'inpaint', hasMask: true });
    expect(started).toBeGreaterThanOrEqual(1);
  });

  it('у нити свои настройки — «Править» берёт их', async () => {
    await withPrefs(MODES);
    const t = thread({ settings: { provider: 'local', model: 'qwen-image-2.1', count: 2, matchSourceSize: true } });
    __applyThreads(S, P, state(t));
    setImageMode(S, 'edit');
    await launchThread(P, S, t, { kind: 'prompt', prompt: 'x' });
    expect(quotes.at(-1)).toMatchObject({ provider: 'local', model: 'qwen-image-2.1' });
  });

  it('модели режимов не серые: «Править» — FLUX Fill, «Создать» — Soul при отметках', async () => {
    await withPrefs(MODES);
    await loadCatalog(P);
    const t = thread();
    __applyThreads(S, P, state(t));
    setThreadMarks(t.id, [MASK], { w: 100, h: 100 });
    let L: ReturnType<typeof useThreadLaunch> | null = null;
    const Probe = () => { L = useThreadLaunch(P, S, t); return null; };
    setImageMode(S, 'edit');
    renderToStaticMarkup(createElement(Probe));
    expect(L!.model?.id).toBe('fal-ai/flux-pro/v1/fill');
    expect(L!.blocked).toBe('');
    expect(L!.op).toBe('inpaint');
    setImageMode(S, 'create');
    renderToStaticMarkup(createElement(Probe));
    expect(L!.model?.id).toBe('soul_2');
    expect(L!.blocked).toBe('');
    expect(L!.op).toBe('generate');
  });

  it('настройки в «Создать» пишутся только в выбор «Создать», нить не трогается', async () => {
    await withPrefs(MODES);
    await loadCatalog(P);
    const t = thread();
    __applyThreads(S, P, state(t));
    const settings = vi.spyOn(threadsApi, 'settings');
    let L: ReturnType<typeof useThreadLaunch> | null = null;
    const Probe = () => { L = useThreadLaunch(P, S, t); return null; };
    setImageMode(S, 'create');
    renderToStaticMarkup(createElement(Probe));
    L!.setSettings({ count: 4 });
    expect(getPrefs(P).create).toEqual({ provider: 'higgsfield', model: 'soul_2', count: 4 });
    expect(getPrefs(P).edit).toEqual(MODES.edit);
    expect(getPrefs(P).count).toBe(2);
    expect(settings).not.toHaveBeenCalled();
  });

  it('выбор операции панели под флагом живёт в «Править»', async () => {
    await withPrefs(MODES);
    setPanelChoice(P, { op: 'removeBackground', mode: 'photoreal' });
    expect(getPrefs(P).edit).toMatchObject({ op: 'removeBackground', editMode: 'photoreal' });
    setPanelChoice(P, { op: 'auto' });
    expect(getPrefs(P).edit?.op).toBe('edit');
  });
});

describe('эхо префов не затирает выбор режима', () => {
  it('PUT несёт режим, только если его правили здесь', async () => {
    await withPrefs(MODES);
    setPrefs(P, { characterSlug: 'masha' });
    await tick();
    expect(puts.at(-1)).not.toHaveProperty('create');
    expect(puts.at(-1)).not.toHaveProperty('edit');
    setPrefs(P, { edit: { ...MODES.edit!, op: 'upscale' } });
    await tick();
    expect(puts.at(-1)?.edit?.op).toBe('upscale');
    expect(puts.at(-1)).not.toHaveProperty('create');
  });

  it('событие сервера с другим выбором режима доходит до кэша', async () => {
    await withPrefs(MODES);
    emitPrefs({ type: 'image_prefs_changed', projectId: P, prefs: { ...MODES, edit: { ...MODES.edit!, op: 'upscale' } } });
    expect(getPrefs(P).edit?.op).toBe('upscale');
    emitPrefs({ type: 'image_prefs_changed', projectId: P, prefs: { ...MODES, create: { ...MODES.create!, count: 4 } } });
    expect(getPrefs(P).create?.count).toBe(4);
  });
});
