import { beforeEach, describe, expect, it, vi } from 'vitest';

// Редактор звука (2з-2): вход из вида контекста, правки без ИИ по порядку, выделение куска оживляет «Перегенерировать кусок».
// Окружение node: хранилища и window подставляем сами
const fakeStorage = (m: Map<string, string>) => ({
  getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => { m.set(k, v); },
  removeItem: (k: string) => { m.delete(k); }, clear: () => m.clear(), key: () => null, length: 0,
}) as Storage;
vi.stubGlobal('localStorage', fakeStorage(new Map()));
vi.stubGlobal('sessionStorage', fakeStorage(new Map()));
vi.stubGlobal('window', Object.assign(new EventTarget(), {
  setTimeout, clearTimeout, setInterval, clearInterval, innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));

const { audioApi } = await import('../api');
const { __applyThreads, __resetAudioStore, getEditor, setSelection, closeEditor } = await import('../thread/threadStore');
const { __applyChatContext, __resetChatContextStore } = await import('../../../lib/chatContext/store');
const { __resetExecutorState } = await import('../context/executors');
const { audioKindApi } = await import('../context/kind');
const { runTrimSteps } = await import('../panel/run');
const { DEFAULT_TRIM } = await import('../panel/inputs');
import type { AudioThread } from '../api';

const P = 'p1';
const S = 's1';
const CTX = { projectId: P, sessionId: S, isMobile: false };
const song = (): AudioThread => ({
  id: 't1', file: 'music/a.mp3', lineage: [], draftFolder: null, createdAt: '', currentVersionId: 'origin', launches: [],
  settings: { mode: 'music', operation: null, provider: null, model: null, fields: null },
  versions: [{ id: 'origin', number: 0, jobId: null, variant: null, baseVersionId: null, license: null, createdAt: '', files: [{ role: 'main', path: 'music/a.mp3' }] }],
});
const primary = { id: 'a1', kind: 'audio', ref: { threadId: 't1' }, by: 'human', addedAt: '', label: 'a.mp3', version: null, thumb: null, missing: false, role: null };

let edits: Record<string, unknown>[];

beforeEach(() => {
  __resetAudioStore(); __resetChatContextStore(); __resetExecutorState();
  edits = [];
  vi.spyOn(audioApi, 'subscribe').mockImplementation(() => () => {});
  vi.spyOn(audioApi, 'edit').mockImplementation(async (_s, _c, _t, req) => {
    edits.push({ ...req });
    return { threadId: 't1', versionId: `v${edits.length}`, number: edits.length, jobId: 'j', state: { focus: 't1', revision: 10 + edits.length, threads: [song()] } };
  });
  __applyThreads(S, P, { focus: 't1', revision: 3, threads: [song()] });
  __applyChatContext(S, { revision: 7, refs: [], primary } as never);
});

describe('вход в редактор', () => {
  it('«Редактор» из вида контекста открывает окно на версии основного объекта', () => {
    const e = audioKindApi.editor!(CTX, primary as never);
    expect(e).not.toBeNull();
    e!.open();
    expect(getEditor()).toEqual({ sessionId: S, threadId: 't1', versionId: 'origin' });
    closeEditor();
    expect(getEditor()).toBeNull();
  });
});

describe('правки без ИИ', () => {
  it('кусок, громкость с фейдами и нормализация — тремя правками по порядку, каждая от свежей ревизии', async () => {
    const ok = await runTrimSteps(P, S, 't1', { ...DEFAULT_TRIM, start: 1, end: 4, gainDb: -3, fadeOut: 2, normalize: true });
    expect(ok).toBe(true);
    expect(edits.map(e => e.op)).toEqual(['trim', 'gainFade', 'normalize']);
    expect(edits[0]).toMatchObject({ startSec: 1, endSec: 4, revision: 3 });
    expect(edits[1]).toMatchObject({ gainDb: -3, fadeOutSec: 2, revision: 11 });
    expect(edits[2].revision).toBe(12);
  });

  it('отказ первой правки обрывает остальные', async () => {
    vi.spyOn(audioApi, 'edit').mockRejectedValueOnce(new Error('нет'));
    expect(await runTrimSteps(P, S, 't1', { ...DEFAULT_TRIM, start: 1, end: 4, normalize: true })).toBe(false);
    expect(edits).toHaveLength(0);
  });
});

describe('выделение куска и «Перегенерировать кусок»', () => {
  const repaint = () => audioKindApi.actions!(CTX, { primary, refs: [] } as never).find(a => a.id === 'repaint');

  it('без выделения чип серый, с выделением на текущей версии — живой', () => {
    expect(repaint()?.disabledReason).toBeTruthy();
    setSelection(S, 't1', { start: 1, end: 3, versionId: 'origin' });
    expect(repaint()?.disabledReason).toBeUndefined();
  });

  it('выделение, снятое в редакторе, снова гасит чип', () => {
    setSelection(S, 't1', { start: 1, end: 3, versionId: 'origin' });
    setSelection(S, 't1', null);
    expect(repaint()?.disabledReason).toBeTruthy();
  });
});
