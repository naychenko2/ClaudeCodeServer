import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// Запуск, цена, «Чем» и параметры действий картинки: тонкий слой над существующими котировкой и запуском.
// Окружение node: мок API включается ключом localStorage
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

vi.mock('../marks', async orig => ({
  ...(await orig<typeof import('../marks')>()),
  exportMask: async () => new Blob(['mask']),
  exportAnnotated: async () => new Blob(['annotated']),
}));
vi.stubGlobal('Image', class { onload: (() => void) | null = null; onerror: (() => void) | null = null; set src(_v: string) { queueMicrotask(() => this.onload?.()); } });

const { imageEditorApi } = await import('../api');
const { __resetPrefs, getPrefs, prefsApi } = await import('../thread/prefs');
const { __applyThreads, __resetThreadStore, setThreadMarks } = await import('../thread/threadStore');
const { __applyChatContext, __resetChatContextStore } = await import('../../../lib/chatContext/store');
const { threadsApi } = await import('../thread/threadsApi');
const { loadCatalog } = await import('../thread/catalog');
const { __resetExecutorCache } = await import('./executors');
const { imageKindApi } = await import('./kind');
import type { ChatContextDto } from '../../../lib/chatContext/types';
import type { ImageThread, ImageThreadsState } from '../thread/threadsApi';

const P = 'p1';
const S = 's1';
const CTX = { projectId: P, sessionId: S, isMobile: false };
const thread = (patch: Partial<ImageThread> = {}): ImageThread => ({
  id: 't1', file: 'images/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null,
  currentStepId: null, settings: null, pendingJobId: null, createdAt: '2026-10-01T00:00:00Z', ...patch,
});
const state = (t: ImageThread): ImageThreadsState => ({ focus: t.id, revision: 1, threads: [t] });
const context = (revision: number): ChatContextDto => ({
  revision, refs: [],
  primary: { id: 'i1', kind: 'image', ref: { threadId: 't1' }, by: 'human', addedAt: '2026-10-01T00:00:00Z', label: 'hero.png', version: null, thumb: null, missing: false, role: null },
});

let started: { op?: unknown; contextRevision?: number; aspectRatio?: string; threadId?: string; marks?: string; mask?: boolean; annotated?: boolean }[];
let quoted: { op: string; count: number; hasMask: boolean; hasAnnotations?: boolean; sessionId?: string | null; contextRevision?: number | null }[];

beforeEach(async () => {
  storage.clear();
  storage.set('cc-image-editor-mock', 'all');
  __resetThreadStore(); __resetPrefs(); __resetChatContextStore(); __resetExecutorCache();
  started = []; quoted = [];
  localStorage.setItem(`cc-image-prefs-migrated:${P}`, '1');
  vi.spyOn(prefsApi, 'get').mockResolvedValue({ provider: null, model: null, count: 2, matchSourceSize: true, characterSlug: null });
  vi.spyOn(threadsApi, 'subscribe').mockImplementation(() => () => {});
  vi.spyOn(prefsApi, 'subscribe').mockImplementation(() => () => {});
  vi.spyOn(prefsApi, 'put').mockImplementation(async (_p, v) => ({ ...getPrefs(P), ...v }));
  const api = imageEditorApi();
  const realQuote = api.quote.bind(api);
  vi.spyOn(api, 'quote').mockImplementation(async (p, req) => { quoted.push(req); return realQuote(p, req); });
  vi.spyOn(api, 'startJob').mockImplementation(async (_p, req) => {
    started.push({ contextRevision: req.contextRevision, aspectRatio: req.aspectRatio, threadId: req.threadId, marks: req.marks, mask: !!req.mask, annotated: !!req.annotated });
    return { jobId: 'j1' } as never;
  });
  vi.stubGlobal('fetch', vi.fn(async () => ({ blob: async () => new Blob([]) })));
  __applyThreads(S, P, state(thread()));
  __applyChatContext(S, context(7));
  await loadCatalog(P);
});
afterEach(() => { vi.restoreAllMocks(); });

describe('запуск действия картинки', () => {
  it('«Изменить»: ревизия контекста уходит и в котировку, и в запуск; в нить основного объекта', async () => {
    const h = await imageKindApi.launch!(CTX, { op: 'edit', text: 'добавь шляпу', params: { variants: 3 }, contextRevision: 7 });
    expect(h.id).toBe('j1');
    expect(started).toHaveLength(1);
    expect(started[0]).toMatchObject({ contextRevision: 7, aspectRatio: undefined, threadId: 't1', marks: undefined, mask: false });
    expect(quoted).toHaveLength(1);
    expect(quoted[0]).toMatchObject({ op: 'edit', count: 3, sessionId: S, contextRevision: 7 });
  });

  it('«Дорисовать»: пропорции из вопроса уходят аспектом запуска', async () => {
    await imageKindApi.launch!(CTX, { op: 'outpaint', text: '', params: { aspect: '9:16', variants: 1 }, contextRevision: 8 });
    expect(started[0]).toMatchObject({ contextRevision: 8, aspectRatio: '9:16' });
    expect(quoted[0]).toMatchObject({ op: 'outpaint', contextRevision: 8 });
  });

  it('«Нарисовать» у черновика: «авто» пропорций не уходит, число вариантов — из параметров', async () => {
    __applyThreads(S, P, { focus: 'd1', revision: 2, threads: [thread({ id: 'd1', file: null, draftFolder: '' })] });
    __applyChatContext(S, { ...context(9), primary: { ...context(9).primary!, ref: { threadId: 'd1' } } });
    await imageKindApi.launch!(CTX, { op: 'generate', text: 'маяк', params: { variants: 2, aspect: 'авто' }, contextRevision: 9 });
    expect(started[0]).toMatchObject({ contextRevision: 9, aspectRatio: undefined, threadId: 'd1' });
    expect(quoted[0]).toMatchObject({ op: 'generate', count: 2 });
  });

  it('нить пропала (контекст сменился) — запуск отказывает, ничего не уходит', async () => {
    __applyChatContext(S, { ...context(10), primary: { ...context(10).primary!, ref: { threadId: 'нет' } } });
    await expect(imageKindApi.launch!(CTX, { op: 'edit', text: 'x', params: {}, contextRevision: 10 })).rejects.toThrow();
    expect(started).toHaveLength(0);
  });
});

const BRUSH = { type: 'mask', points: [[1, 1], [9, 9]], width: 4 } as const;
const ARROW = { type: 'arrow', x1: 1, y1: 1, x2: 9, y2: 9 } as const;
const SIZE = { w: 100, h: 100 };

describe('запуск чипа с отметками: маска и аннотации уходят в котировку и запуск', () => {
  it('кисть и стрелка: «Изменить» становится инпейнтом, котировка и запуск несут маску и аннотации, отметки уходят в задачу', async () => {
    setThreadMarks('t1', [BRUSH, ARROW], SIZE);
    await imageKindApi.launch!(CTX, { op: 'inpaint', text: 'убери', params: { variants: 1 }, contextRevision: 7 });
    expect(quoted[0]).toMatchObject({ op: 'inpaint', hasMask: true, hasAnnotations: true });
    expect(JSON.parse(started[0].marks!)).toHaveLength(2);
    expect(started[0]).toMatchObject({ mask: true, annotated: true });
  });

  it('только стрелка: маски нет, аннотации есть, операция — правка', async () => {
    setThreadMarks('t1', [ARROW], SIZE);
    await imageKindApi.launch!(CTX, { op: 'edit', text: 'сюда', params: { variants: 1 }, contextRevision: 7 });
    expect(quoted[0]).toMatchObject({ op: 'edit', hasMask: false, hasAnnotations: true });
    expect(JSON.parse(started[0].marks!)).toHaveLength(1);
    expect(started[0]).toMatchObject({ mask: false, annotated: true });
  });

  it('цена и запуск быстрой операции совпадают: стрелки «Увеличить» не нужны ни там, ни там', async () => {
    setThreadMarks('t1', [ARROW], SIZE);
    await imageKindApi.quote!(CTX, { op: 'upscale', text: '', params: {}, contextRevision: 7 });
    await imageKindApi.launch!(CTX, { op: 'upscale', text: '', params: {}, contextRevision: 7 });
    expect(quoted).toHaveLength(2);
    expect(quoted[0]).toMatchObject({ op: 'upscale', hasMask: false, hasAnnotations: false });
    expect(quoted[1]).toEqual(quoted[0]);
    expect(started[0].marks).toBeUndefined();
  });

  it('ключ цены меняется от отметок: priceSalt различает пустой холст, стрелку и кисть', () => {
    const salt = () => imageKindApi.priceSalt!(CTX, 'edit');
    const empty = salt();
    setThreadMarks('t1', [ARROW], SIZE);
    const arrow = salt();
    setThreadMarks('t1', [ARROW, BRUSH], SIZE);
    const both = salt();
    expect(new Set([empty, arrow, both]).size).toBe(3);
  });
});

describe('цена, «Чем» и параметры', () => {
  it('цена по op действия: подпись из котировки, ревизия в запросе', async () => {
    const q = await imageKindApi.quote!(CTX, { op: 'edit', text: 'x', params: { variants: 2 }, contextRevision: 7 });
    expect(q.price).toBeTruthy();
    expect(quoted[0]).toMatchObject({ op: 'edit', count: 2, sessionId: S, contextRevision: 7 });
  });

  it('«Чем»: строки под операцию выбранного действия, «Авто» выбран; модель не умеет — серая', () => {
    const m = imageKindApi.executors!(CTX, 'edit')!;
    expect(m.rows[0]).toMatchObject({ id: 'auto', group: 'auto' });
    expect(m.value).toBe('auto');
    // Тот же набор — одна и та же модель: хост зовёт на каждый рендер
    expect(imageKindApi.executors!(CTX, 'edit')).toBe(m);
    expect(imageKindApi.executors!(CTX, 'нет-такого')).toBeNull();
  });

  it('параметры: варианты у «Изменить» и «Дорисовать», у «Убрать фон» и «Увеличить» их нет; пропорции только у «Нарисовать»', () => {
    expect(imageKindApi.params!(CTX, 'edit').map(p => p.kind)).toEqual(['variants']);
    expect(imageKindApi.params!(CTX, 'outpaint').map(p => p.kind)).toEqual(['variants']);
    expect(imageKindApi.params!(CTX, 'removeBg')).toEqual([]);
    expect(imageKindApi.params!(CTX, 'upscale')).toEqual([]);
    __applyThreads(S, P, { focus: 'd1', revision: 2, threads: [thread({ id: 'd1', file: null, draftFolder: '' })] });
    __applyChatContext(S, { ...context(9), primary: { ...context(9).primary!, ref: { threadId: 'd1' } } });
    const draw = imageKindApi.params!(CTX, 'draw');
    expect(draw.map(p => p.kind)).toEqual(['variants', 'aspect']);
    expect(draw[1]).toMatchObject({ options: ['авто', '16:9', '9:16', '1:1'], value: 'авто' });
  });

  it('действия: у нити с файлом — пять чипов; нить не найдена — действий нет (остаётся «Чат | Картинка»)', () => {
    const primary = context(7).primary!;
    expect(imageKindApi.actions(CTX, { primary, refs: [] }).map(a => a.id)).toEqual(['edit', 'removeBg', 'upscale', 'outpaint', 'mark']);
    expect(imageKindApi.actions(CTX, { primary: { ...primary, ref: { threadId: 'нет' } }, refs: [] })).toEqual([]);
  });
});
