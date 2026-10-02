import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// Полоса и поле ввода панели v5 (флаг image-panel-v5, шаг И2): режим поля без выбранной
// картинки, отправка без нити, «↻ Ещё N», снятие выбора с «Вернуть», выбор из «Что править?».
// Окружение node: мок API включается ключом localStorage, события — через window
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
const reveals: string[] = [];
win.addEventListener('cc-reveal-panel', e => reveals.push((e as CustomEvent<{ key: string }>).detail.key));

const { setFlagLocal, FLAGS } = await import('../../../lib/featureFlags');
const { imageEditorApi } = await import('../api');
const { __resetPrefs, ensurePrefs, getPrefs, prefsApi } = await import('../thread/prefs');
const { __applyThreads, __resetThreadStore, getFocusedThread, getThreadMarks, setThreadMarks } = await import('../thread/threadStore');
const { threadsApi } = await import('../thread/threadsApi');
const {
  editThreadByHuman, imageReleaseUndo, IMAGE_RELEASE_TEXT, releaseFocus, setImageModeByHuman, undoImageRelease,
} = await import('../thread/actions');
const { __resetImageModes, getStoredImageMode, setImageMode } = await import('../thread/modeState');
const { __resetPanelChoice } = await import('../panel/panelOp');
const { imageMode } = await import('./imageMode');
import type { Mark } from '../marks';
import type { ProjectPrefs } from '../thread/prefs';
import type { ImageThread, ImageThreadLaunch, ImageThreadsState } from '../thread/threadsApi';

const P = 'p1';
const S = 's1';
const CTX = { projectId: P, sessionId: S };
const thread = (patch: Partial<ImageThread> = {}): ImageThread => ({
  id: 't1', file: 'images/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null,
  currentStepId: null, settings: null, pendingJobId: null, createdAt: '2026-10-01T00:00:00Z', ...patch,
});
const DRAFT = thread({ id: 'd1', file: null, draftFolder: '' });
// Картинка с версией от ИИ — не «пустая»: при снятии выбора нить остаётся в ленте
const ver = (id: string, number: number, steps: string[]) =>
  ({ id, number, jobId: number ? 'j0' : null, variant: null, baseVersionId: null, baseStepId: null, steps, currentStepId: steps[0] ?? null, createdAt: '2026-10-01T09:00:00Z' });
const EDITED: Partial<ImageThread> = { versions: [ver('origin', 0, []), ver('v1', 1, ['st1'])], currentVersionId: 'v1' };
const launch = (prompt: string | null, at = '2026-10-01T10:00:00Z'): ImageThreadLaunch =>
  ({ jobId: `j-${at}`, baseVersionId: null, baseStepId: null, at, status: 'done', initiator: 'human', prompt });
const state = (t: ImageThread | null, revision = 1, extra: ImageThread[] = []): ImageThreadsState =>
  ({ focus: t?.id ?? null, revision, threads: [...(t ? [t] : []), ...extra] });
const MASK: Mark = { type: 'mask', points: [[1, 1], [5, 5]], width: 4 };
const PREFS: ProjectPrefs = { provider: null, model: null, count: 2, matchSourceSize: true, characterSlug: null };

let started: { prompt?: string; threadId?: string }[];
let order: string[];

beforeEach(async () => {
  storage.clear();
  storage.set('cc-image-editor-mock', 'all');
  setFlagLocal(FLAGS.imagePanelV5, true);
  __resetThreadStore();
  __resetPrefs();
  __resetPanelChoice();
  __resetImageModes();
  imageReleaseUndo.dismiss();
  started = [];
  order = [];
  localStorage.setItem(`cc-image-prefs-migrated:${P}`, '1');
  vi.spyOn(prefsApi, 'get').mockResolvedValue(PREFS);
  vi.spyOn(prefsApi, 'subscribe').mockImplementation(() => () => {});
  vi.spyOn(prefsApi, 'put').mockImplementation(async (_p, v) => ({ ...getPrefs(P), ...v }));
  await ensurePrefs(P);
  const api = imageEditorApi();
  vi.spyOn(api, 'startJob').mockImplementation(async (_p, req) => {
    order.push('start');
    started.push({ prompt: req.prompt, threadId: req.threadId });
    return { jobId: 'j1' } as never;
  });
  // Картинки исходника в node нет — источник правки отдаём пустым Blob
  vi.stubGlobal('fetch', vi.fn(async () => ({ blob: async () => new Blob([]) })));
});
afterEach(() => {
  toasts.length = 0;
  reveals.length = 0;
  setFlagLocal(FLAGS.imagePanelV5, false);
  vi.restoreAllMocks();
});

describe('режим «Картинка» поля ввода: доступность', () => {
  it('без флага — только при выбранной картинке, «Создать» не в счёт', () => {
    setFlagLocal(FLAGS.imagePanelV5, false);
    setImageMode(S, 'create');
    expect(imageMode.isAvailable(CTX)).toBe(false);
    __applyThreads(S, P, state(thread()));
    expect(imageMode.isAvailable(CTX)).toBe(true);
  });

  it('с флагом: без картинки — только если чат в «Создать»', () => {
    expect(imageMode.isAvailable(CTX)).toBe(false);
    setImageMode(S, 'edit');
    expect(imageMode.isAvailable(CTX)).toBe(false);
    setImageMode(S, 'create');
    expect(imageMode.isAvailable(CTX)).toBe(true);
  });

  it('с флагом: выбранная картинка — режим есть в любом режиме чата', () => {
    __applyThreads(S, P, state(thread()));
    setImageMode(S, 'edit');
    expect(imageMode.isAvailable(CTX)).toBe(true);
  });
});

describe('«↻ Ещё N» — emptySubmit', () => {
  const finished = () => thread({ id: 'n2', file: null, draftFolder: '', currentStepId: 'st1', launches: [launch('маяк на закате')] });

  it('«Создать», у выбранной картинки есть прошлый запуск — кнопка повторяет его в ту же нить', async () => {
    __applyThreads(S, P, state(finished()));
    setImageMode(S, 'create');
    const e = imageMode.emptySubmit!(CTX);
    expect(e).not.toBeNull();
    await e!.run();
    expect(started).toEqual([{ prompt: 'маяк на закате', threadId: 'n2' }]);
  });

  it('прошлого запуска нет — кнопки нет', () => {
    __applyThreads(S, P, state(thread({ id: 'n2', file: null, draftFolder: '', currentStepId: 'st1' })));
    setImageMode(S, 'create');
    expect(imageMode.emptySubmit!(CTX)).toBeNull();
  });

  it('«Править» — кнопки нет: повтор там правил бы картинку заново', () => {
    __applyThreads(S, P, state(finished()));
    setImageMode(S, 'edit');
    expect(imageMode.emptySubmit!(CTX)).toBeNull();
  });

  it('без флага — кнопки нет', () => {
    setFlagLocal(FLAGS.imagePanelV5, false);
    __applyThreads(S, P, state(finished()));
    expect(imageMode.emptySubmit!(CTX)).toBeNull();
  });
});

describe('отправка без выбранной картинки в «Создать»', () => {
  it('сначала черновик, потом запуск в него — id нити из свежего стора', async () => {
    __applyThreads(S, P, state(null));
    setImageMode(S, 'create');
    vi.spyOn(threadsApi, 'create').mockImplementation(async () => { order.push('create'); return state(DRAFT, 2); });
    await imageMode.onSubmit(CTX, 'маяк на закате');
    expect(order).toEqual(['create', 'start']);
    expect(started).toEqual([{ prompt: 'маяк на закате', threadId: 'd1' }]);
    // Черновик из поля ввода панель не открывает
    expect(reveals).toEqual([]);
  });

  it('черновик не создан — запуска нет, текст остаётся в поле (исключение)', async () => {
    __applyThreads(S, P, state(null));
    setImageMode(S, 'create');
    vi.spyOn(threadsApi, 'create').mockRejectedValue(new Error('сбой'));
    await expect(imageMode.onSubmit(CTX, 'маяк')).rejects.toThrow();
    expect(started).toEqual([]);
  });
});

describe('снятие выбора человеком', () => {
  it('в «Править»: режим «Создать», плашка «Вернуть», тоста про «Чат» нет, отметки живы до конца плашки', async () => {
    const t = thread({ launches: [launch('x')], ...EDITED });
    __applyThreads(S, P, state(t));
    setImageMode(S, 'edit');
    setThreadMarks(t.id, [MASK], { w: 10, h: 10 });
    vi.spyOn(threadsApi, 'focus').mockImplementation(async (_p, _s, id) => state(id ? t : null, 2, id ? [] : [t]));
    await releaseFocus(P, S, t);
    expect(getStoredImageMode(S)).toBe('create');
    expect(imageMode.isAvailable(CTX)).toBe(true);
    expect(toasts).toEqual([]);
    expect(imageReleaseUndo.current()?.text).toBe(IMAGE_RELEASE_TEXT);
    expect(getThreadMarks(t.id).marks).toHaveLength(1);
    // «Вернуть»: та же картинка, «Править», отметки на месте
    expect(await undoImageRelease()).toBe(true);
    expect(getFocusedThread(S)?.id).toBe('t1');
    expect(getStoredImageMode(S)).toBe('edit');
    expect(getThreadMarks(t.id).marks).toHaveLength(1);
  });

  it('картинка-файл без правок уходит из ленты, «Вернуть» берёт её по файлу и переносит отметки', async () => {
    const t = thread({});
    __applyThreads(S, P, state(t));
    setImageMode(S, 'edit');
    setThreadMarks(t.id, [MASK], { w: 10, h: 10 });
    vi.spyOn(threadsApi, 'remove').mockResolvedValue(state(null, 2));
    await releaseFocus(P, S, t);
    expect(imageReleaseUndo.current()?.snapshot.file).toBe('images/hero.png');
    const reopened = thread({ id: 't2' });
    const create = vi.spyOn(threadsApi, 'create').mockResolvedValue(state(reopened, 3));
    expect(await undoImageRelease()).toBe(true);
    expect(create.mock.calls[0][2]).toMatchObject({ file: 'images/hero.png' });
    expect(getStoredImageMode(S)).toBe('edit');
    expect(getThreadMarks('t2').marks).toHaveLength(1);
    expect(getThreadMarks('t1').marks).toHaveLength(0);
  });

  it('плашка ушла без «Вернуть» — отметки снятой картинки гаснут', async () => {
    const t = thread(EDITED);
    __applyThreads(S, P, state(t));
    setImageMode(S, 'edit');
    setThreadMarks(t.id, [MASK], { w: 10, h: 10 });
    vi.spyOn(threadsApi, 'focus').mockResolvedValue(state(null, 2, [t]));
    await releaseFocus(P, S, t);
    expect(getThreadMarks(t.id).marks).toHaveLength(1);
    imageReleaseUndo.dismiss();
    expect(getThreadMarks(t.id).marks).toHaveLength(0);
  });

  it('в «Создать» — без плашки', async () => {
    const t = thread(EDITED);
    __applyThreads(S, P, state(t));
    setImageMode(S, 'create');
    vi.spyOn(threadsApi, 'focus').mockResolvedValue(state(null, 2, [t]));
    await releaseFocus(P, S, t);
    expect(imageReleaseUndo.current()).toBeNull();
  });

  it('без флага — прежний тост про «Чат» и отметки гаснут сразу', async () => {
    setFlagLocal(FLAGS.imagePanelV5, false);
    const t = thread(EDITED);
    __applyThreads(S, P, state(t));
    setThreadMarks(t.id, [MASK], { w: 10, h: 10 });
    vi.spyOn(threadsApi, 'focus').mockResolvedValue(state(null, 2, [t]));
    await releaseFocus(P, S, t);
    expect(toasts).toEqual(['Картинка больше не выбрана: режим «Чат»']);
    expect(imageReleaseUndo.current()).toBeNull();
    expect(getThreadMarks(t.id).marks).toHaveLength(0);
  });
});

describe('сегмент и меню «Что править?»', () => {
  it('выбор в меню: сначала картинка, потом «Править»; на телефоне (none) панель не открывается', async () => {
    const t = thread(EDITED);
    __applyThreads(S, P, state(null, 1, [t]));
    setImageMode(S, 'create');
    const calls: string[] = [];
    vi.spyOn(threadsApi, 'focus').mockImplementation(async () => { calls.push(`focus:${getStoredImageMode(S)}`); return state(t, 2); });
    expect(await editThreadByHuman(P, S, 't1', 'none')).toBe(true);
    expect(calls).toEqual(['focus:create']);
    expect(getStoredImageMode(S)).toBe('edit');
    expect(reveals).toEqual([]);
  });

  it('выбор в меню на 1440 (auto) открывает панель', async () => {
    const t = thread({ currentStepId: 'st1' });
    __applyThreads(S, P, state(null, 1, [t]));
    vi.spyOn(threadsApi, 'focus').mockResolvedValue(state(t, 2));
    await editThreadByHuman(P, S, 't1', 'auto');
    expect(reveals).toEqual(['images']);
  });

  it('«Создать» при выбранной картинке — черновик, картинка остаётся в ленте, панель не открывается', async () => {
    const t = thread({ currentStepId: 'st1' });
    __applyThreads(S, P, state(t));
    setImageMode(S, 'edit');
    const create = vi.spyOn(threadsApi, 'create').mockResolvedValue(state(DRAFT, 2, [t]));
    expect(await setImageModeByHuman(P, S, 'create')).toBe(true);
    expect(create).toHaveBeenCalledOnce();
    expect(getStoredImageMode(S)).toBe('create');
    expect(reveals).toEqual([]);
  });

  it('«Править» без картинки режим не меняет — его спрашивает меню', async () => {
    __applyThreads(S, P, state(null));
    setImageMode(S, 'create');
    expect(await setImageModeByHuman(P, S, 'edit')).toBe(false);
    expect(getStoredImageMode(S)).toBe('create');
  });
});
