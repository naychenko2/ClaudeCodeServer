import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// «Править» у картинки-файла ставит сам editFileByHuman, а не попутный noteImageMode из
// humanPick (замечание ревью И2): попутный вызов здесь заглушён, и «Вернуть» картинки-файла
// обязан вернуть «Править» своим setImageMode

const storage = new Map<string, string>([['cc-image-editor-mock', 'all']]);
const fakeStorage = (m: Map<string, string>) => ({
  getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => { m.set(k, v); },
  removeItem: (k: string) => { m.delete(k); }, clear: () => m.clear(), key: () => null, length: 0,
}) as Storage;
vi.stubGlobal('localStorage', fakeStorage(storage));
vi.stubGlobal('sessionStorage', fakeStorage(new Map()));
vi.stubGlobal('window', Object.assign(new EventTarget(), {
  setTimeout, clearTimeout, setInterval, clearInterval, innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));

vi.mock('./modeState', async orig => ({ ...(await orig<typeof import('./modeState')>()), noteImageMode: vi.fn() }));

const { setFlagLocal, FLAGS } = await import('../../../lib/featureFlags');
const { __applyThreads, __resetThreadStore } = await import('./threadStore');
const { threadsApi } = await import('./threadsApi');
const { editFileByHuman, imageReleaseUndo, releaseFocus, undoImageRelease } = await import('./actions');
const { __resetImageModes, getStoredImageMode, noteImageMode, setImageMode } = await import('./modeState');
import type { ImageThread, ImageThreadsState } from './threadsApi';

const P = 'p1';
const S = 's1';
const hero = (id: string): ImageThread => ({
  id, file: 'images/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null,
  currentStepId: null, settings: null, pendingJobId: null, createdAt: '2026-10-01T00:00:00Z',
});
const state = (t: ImageThread | null, revision: number): ImageThreadsState =>
  ({ focus: t?.id ?? null, revision, threads: t ? [t] : [] });

beforeEach(() => {
  storage.clear();
  storage.set('cc-image-editor-mock', 'all');
  setFlagLocal(FLAGS.imagePanelV5, true);
  __resetThreadStore();
  __resetImageModes();
});
afterEach(() => {
  imageReleaseUndo.undo();
  setFlagLocal(FLAGS.imagePanelV5, false);
  vi.restoreAllMocks();
});

describe('«Править» у картинки-файла — без попутного noteImageMode', () => {
  it('«Что править?» → файл проекта: режим «Править»', async () => {
    __applyThreads(S, P, state(null, 1));
    setImageMode(S, 'create');
    vi.spyOn(threadsApi, 'create').mockResolvedValue(state(hero('t1'), 2));
    expect(await editFileByHuman(P, S, 'images/hero.png', 'none')).toBe(true);
    expect(noteImageMode).toHaveBeenCalled();
    expect(getStoredImageMode(S)).toBe('edit');
  });

  it('«Вернуть» картинки-файла без правок: нить заводится заново, режим «Править»', async () => {
    const t = hero('t1');
    __applyThreads(S, P, state(t, 1));
    setImageMode(S, 'edit');
    // Файл без шагов — пустая нить: ✕ убирает её из ленты, «Вернуть» идёт по файлу
    vi.spyOn(threadsApi, 'remove').mockResolvedValue(state(null, 2));
    await releaseFocus(P, S, t);
    expect(getStoredImageMode(S)).toBe('create');
    expect(imageReleaseUndo.current()?.snapshot.file).toBe('images/hero.png');

    vi.spyOn(threadsApi, 'create').mockResolvedValue(state(hero('t2'), 3));
    expect(await undoImageRelease()).toBe(true);
    expect(getStoredImageMode(S)).toBe('edit');
  });
});
