import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

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
const { __resetPrefs, ensurePrefs, prefsApi } = await import('../thread/prefs');
const {
  __applyThreads, __resetThreadStore, getEditor, isWholeImage, openEditor, setThreadMarks, setWholeImage,
} = await import('../thread/threadStore');
const { __resetImageModes, setImageMode } = await import('../thread/modeState');
const { launchThread } = await import('../thread/useThreadLaunch');
const {
  __resetPanelChoice, AI_GROUP, editOpOptions, editPickOf, editWhere, isNoAiTool, launchMarks, NO_AI_GROUP, setCreateRatio, setPanelChoice,
} = await import('./panelOp');
import type { ImageEditJobInput, ImageEditQuoteRequest } from '../api';
import type { Mark } from '../marks';
import type { ProjectPrefs } from '../thread/prefs';
import type { ImageThread, ImageThreadsState } from '../thread/threadsApi';

// Панель v5 (флаг image-panel-v5): список «Операция» режима «Править», «Где менять»,
// пропорции новой картинки и редактор на инструменте

const P = 'p1';
const S = 's1';
const thread = (patch: Partial<ImageThread> = {}): ImageThread => ({
  id: 't1', file: 'images/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null,
  currentStepId: null, settings: null, pendingJobId: null, createdAt: '2026-10-01T00:00:00Z', ...patch,
});
const state = (t: ImageThread): ImageThreadsState => ({ focus: t.id, revision: 1, threads: [t] });
const MASK: Mark = { type: 'mask', points: [[1, 1], [5, 5]], width: 4 };
const ARROW = { type: 'arrow', x1: 1, y1: 1, x2: 5, y2: 5 } as unknown as Mark;
const PREFS: ProjectPrefs = {
  provider: null, model: null, count: 2, matchSourceSize: true, characterSlug: null,
  create: { provider: 'local', model: 'qwen-image-2.1', count: 1 },
  edit: { provider: 'local', model: 'qwen-image-2.1', count: 1, op: 'edit', editMode: null, ratio: null },
};

let quotes: ImageEditQuoteRequest[];
let jobs: ImageEditJobInput[];

beforeEach(async () => {
  storage.clear();
  storage.set('cc-image-editor-mock', 'all');
  setFlagLocal(FLAGS.imagePanelV5, true);
  __resetThreadStore();
  __resetPrefs();
  __resetPanelChoice();
  __resetImageModes();
  vi.spyOn(prefsApi, 'subscribe').mockImplementation(() => () => {});
  vi.spyOn(prefsApi, 'put').mockImplementation(async (_p, v) => v);
  localStorage.setItem(`cc-image-prefs-migrated:${P}`, '1');
  vi.spyOn(prefsApi, 'get').mockResolvedValue(PREFS);
  await ensurePrefs(P);
  quotes = [];
  jobs = [];
  const api = imageEditorApi();
  const quote = api.quote.bind(api);
  vi.spyOn(api, 'quote').mockImplementation((p, req) => { quotes.push(req); return quote(p, req); });
  vi.spyOn(api, 'startJob').mockImplementation(async (_p, input) => { jobs.push(input); return { jobId: 'j1' } as never; });
  vi.stubGlobal('fetch', async () => ({ blob: async () => new Blob(['x']) }));
});
afterEach(() => {
  toasts.length = 0;
  setFlagLocal(FLAGS.imagePanelV5, false);
  vi.restoreAllMocks();
});

describe('список «Операция» режима «Править»', () => {
  it('группы «С ИИ» и «Без ИИ · бесплатно»; «Улучшить лица» — только с локальными моделями', () => {
    expect(editOpOptions(true).map(o => `${o.group}:${o.label}`)).toEqual([
      `${AI_GROUP}:Изменить по тексту`, `${AI_GROUP}:Дорисовать за края`, `${AI_GROUP}:Убрать фон`,
      `${AI_GROUP}:Улучшить качество`, `${AI_GROUP}:Улучшить лица`,
      `${NO_AI_GROUP}:Обрезать`, `${NO_AI_GROUP}:Повернуть и отразить`, `${NO_AI_GROUP}:Размер и формат`,
    ]);
    expect(editOpOptions(false).map(o => o.value)).not.toContain('enhanceFaces');
    expect(editOpOptions(false).filter(o => o.group === NO_AI_GROUP).every(o => isNoAiTool(o.value))).toBe(true);
    expect(editOpOptions(true).filter(o => o.group === AI_GROUP).some(o => isNoAiTool(o.value))).toBe(false);
  });

  it('«По отмеченному» и прежнее «Авто» — это один пункт «Изменить по тексту»', () => {
    expect(editPickOf('inpaint')).toBe('edit');
    expect(editPickOf('auto')).toBe('edit');
    expect(editPickOf('upscale')).toBe('upscale');
  });

  it('правка без ИИ открывает редактор на своём инструменте', () => {
    openEditor(S, 't1', null, { tool: 'crop' });
    expect(getEditor()).toMatchObject({ threadId: 't1', tool: 'crop' });
    openEditor(S, 't1');
    expect(getEditor()).not.toHaveProperty('tool');
  });
});

describe('«Где менять»', () => {
  it('«Отмеченное» — только при закрашенном и без «Вся картинка»', () => {
    expect(editWhere(0, false)).toBe('whole');
    expect(editWhere(2, false)).toBe('marked');
    expect(editWhere(2, true)).toBe('whole');
  });

  it('при «Вся картинка» маска не уходит, стрелки остаются', () => {
    expect(launchMarks([MASK, ARROW], true)).toEqual([ARROW]);
    expect(launchMarks([MASK, ARROW], false)).toEqual([MASK, ARROW]);
  });

  it('новые отметки снимают «Вся картинка»', () => {
    setThreadMarks('t1', [MASK], { w: 100, h: 100 });
    setWholeImage('t1', true);
    expect(isWholeImage('t1')).toBe(true);
    setThreadMarks('t1', [MASK, MASK], { w: 100, h: 100 });
    expect(isWholeImage('t1')).toBe(false);
  });

  it('запуск: «Вся картинка» при отметках идёт правкой без маски, «Отмеченное» — инпейнтом', async () => {
    const t = thread();
    __applyThreads(S, P, state(t));
    setImageMode(S, 'edit');
    setThreadMarks(t.id, [MASK], { w: 100, h: 100 });
    setWholeImage(t.id, true);
    expect(await launchThread(P, S, t, { kind: 'prompt', prompt: 'небо розовое' })).toBe(true);
    expect(quotes.at(-1)).toMatchObject({ op: 'edit', hasMask: false });
    expect(jobs.at(-1)?.marks).toBeUndefined();
    // Отметки не ушли с запуском — остались до «Отмеченного»
    expect(toasts).toEqual([]);
    setWholeImage(t.id, false);
    await launchThread(P, S, t, { kind: 'prompt', prompt: 'небо розовое' });
    // Маску в node не нарисовать (нет canvas) — инпейнт и маску проверяем по котировке
    expect(quotes.at(-1)).toMatchObject({ op: 'inpaint', hasMask: true });
  });

  it('без флага «Вся картинка» запуск не меняет', async () => {
    setFlagLocal(FLAGS.imagePanelV5, false);
    const t = thread();
    __applyThreads(S, P, state(t));
    setThreadMarks(t.id, [MASK], { w: 100, h: 100 });
    setWholeImage(t.id, true);
    await launchThread(P, S, t, { kind: 'prompt', prompt: 'x' });
    expect(quotes.at(-1)).toMatchObject({ op: 'inpaint', hasMask: true });
  });
});

describe('пропорции новой картинки', () => {
  it('уходят только с генерацией в «Создать»', async () => {
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

  it('без флага «По тексту» прежняя: пропорций «Создать» не берёт', async () => {
    const t = thread();
    __applyThreads(S, P, state(t));
    setCreateRatio(P, '9:16');
    setFlagLocal(FLAGS.imagePanelV5, false);
    setPanelChoice(P, { op: 'generate' });
    await launchThread(P, S, t, { kind: 'prompt', prompt: 'кот' });
    expect(quotes.at(-1)?.op).toBe('generate');
    expect(jobs.at(-1)?.aspectRatio).toBeUndefined();
  });
});
