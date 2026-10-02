import { beforeEach, describe, expect, it, vi } from 'vitest';

// Окружение node — ни localStorage, ни window нет; мокаем минимально и копим события показа панели
const ls = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => ls.get(k) ?? null,
  setItem: (k: string, v: string) => { ls.set(k, String(v)); },
  removeItem: (k: string) => { ls.delete(k); },
  clear: () => ls.clear(),
  key: () => null,
  length: 0,
} as Storage;
const dispatched: { type: string; detail: unknown }[] = [];
(globalThis as unknown as { window: Pick<Window, 'dispatchEvent'> }).window = {
  dispatchEvent: (e: Event) => { dispatched.push({ type: e.type, detail: (e as CustomEvent).detail }); return true; },
};

import { __resetComposerStrips, getActiveStrip } from '../../../lib/composerStrips';
import { __resetAgentPicks, getAgentPick } from '../../../lib/genPanelFollow';
import { __resetGenPanelOpen, holdGenPanelOpen } from '../../../lib/genPanelOpen';
import { REVEAL_PANEL_EVENT } from '../../../lib/subsystems/registryCore';
import { videoApi } from '../api';
import { CATALOG, film, FILM_PATH, PREFS, scene, threads } from '../mocks';
import { selectFilmByHuman, selectSceneByHuman } from '../scene/actions';
import {
  __applyThreads, __resetVideoStore, __setFilm, ensureVideoThreads, getFailure, getFilm, getFocusedScene, getJobsOf, handleEvent, mutate,
  patchFilm, sceneDraftKey, VIDEO_PANEL, VIDEO_STRIP,
} from './videoStore';

const AVAIL = ['git', VIDEO_STRIP];
const reveals = () => dispatched.filter(d => d.type === REVEAL_PANEL_EVENT).map(d => d.detail);
const changed = (revision: number, st = threads(revision, [scene('s1'), scene('s2')])) =>
  ({ type: 'video_thread_changed' as const, sessionId: 'c1', scopeKey: 'p1', revision, state: st });

beforeEach(() => {
  ls.clear();
  dispatched.length = 0;
  __resetVideoStore();
  __resetComposerStrips();
  __resetAgentPicks();
  __resetGenPanelOpen();
  vi.restoreAllMocks();
});

describe('стор «Видео»: события → состояние', () => {
  it('загрузка берёт сцены, каталог и префы; фокус сцены просит полосу «Видео»', async () => {
    vi.spyOn(videoApi, 'state').mockResolvedValue({ threads: threads(3, [scene('s1')], { sceneId: 's1' }), catalog: CATALOG, prefs: PREFS });
    await ensureVideoThreads('p1', 'c1');
    expect(getFocusedScene('c1')?.sceneId).toBe('s1');
    expect(getActiveStrip('c1', AVAIL)).toBe(VIDEO_STRIP);
  });

  it('старая ревизия события пропускается', () => {
    __applyThreads('c1', 'p1', threads(5, [scene('s1')], { sceneId: 's1' }));
    handleEvent(changed(4, threads(4, [scene('s1')], {})));
    expect(getFocusedScene('c1')?.sceneId).toBe('s1');
  });

  it('ход съёмки: progress копится, failed уносит задачу и оставляет отказ с соседом', () => {
    handleEvent({ type: 'video_edit_progress', sessionId: 'c1', scopeKey: 'p1', jobId: 'j1', sceneId: 's1', stage: 'queued', queuePosition: 2, variant: 1, count: 2, initiator: 'human' });
    expect(getJobsOf('c1', 's1')).toHaveLength(1);
    handleEvent({ type: 'video_edit_failed', sessionId: 'c1', scopeKey: 'p1', jobId: 'j1', sceneId: 's1', error: 'fal недоступен', initiator: 'human' });
    expect(getJobsOf('c1', 's1')).toHaveLength(0);
    expect(getFailure('c1', 's1')?.text).toBe('fal недоступен');
  });
});

describe('агент не двигает панель', () => {
  it('фокус сцены, сменённый событием, — подсказка noteAgentPick на вкладку «Сцена», без показа панели', () => {
    __applyThreads('c1', 'p1', threads(1, [scene('s1'), scene('s2')], { sceneId: 's1' }));
    holdGenPanelOpen('images', 'column');
    handleEvent(changed(2, threads(2, [scene('s1'), scene('s2')], { sceneId: 's2' })));
    expect(reveals()).toEqual([]);
    expect(getAgentPick('c1')).toMatchObject({ panelKey: VIDEO_PANEL, target: sceneDraftKey('s2'), label: 'Сцена 2', tab: 'scene' });
  });

  it('выбор агента не переключает полосу: на телефоне это закрыло бы шторку соседнего раздела', () => {
    __applyThreads('c1', 'p1', threads(1, [scene('s1')], {}));
    handleEvent(changed(2, threads(2, [scene('s1')], { sceneId: 's1' })));
    expect(getActiveStrip('c1', AVAIL)).not.toBe(VIDEO_STRIP);
    expect(getAgentPick('c1')?.tab).toBe('scene');
  });

  it('правка настроек в полёте не глушит выбор агента — глушит только свой выбор', async () => {
    __applyThreads('c1', 'p1', threads(1, [scene('s1'), scene('s2')], { sceneId: 's1' }));
    vi.spyOn(videoApi, 'settings').mockImplementation(async () => {
      handleEvent(changed(2, threads(2, [scene('s1'), scene('s2')], { sceneId: 's2' })));
      return threads(3, [scene('s1'), scene('s2')], { sceneId: 's2' });
    });
    await mutate('p1', 'c1', rev => videoApi.settings('p1', 'c1', 's1', scene('s1').settings, rev));
    expect(getAgentPick('c1')?.target).toBe(sceneDraftKey('s2'));
  });

  it('фильм, открытый агентом, — подсказка на вкладку «Фильм»', () => {
    __applyThreads('c1', 'p1', threads(1, [scene('s1')], { sceneId: 's1' }));
    handleEvent(changed(2, threads(2, [scene('s1')], { sceneId: 's1', filmPath: FILM_PATH })));
    expect(reveals()).toEqual([]);
    expect(getAgentPick('c1')).toMatchObject({ target: FILM_PATH, label: 'утро', tab: 'film' });
  });

  it('свой клик подсказки не ставит, даже если событие пришло раньше ответа', async () => {
    __applyThreads('c1', 'p1', threads(1, [scene('s1'), scene('s2')], { sceneId: 's1' }));
    vi.spyOn(videoApi, 'focus').mockImplementation(async () => {
      handleEvent(changed(2, threads(2, [scene('s1'), scene('s2')], { sceneId: 's2' })));
      return threads(2, [scene('s1'), scene('s2')], { sceneId: 's2' });
    });
    await selectSceneByHuman('p1', 'c1', 's2');
    expect(getAgentPick('c1')).toBeNull();
  });
});

describe('клик по карточке: панель следует за выбором с явной вкладкой', () => {
  it('открытая панель переключается на «Сцену» с ключом элемента', async () => {
    __applyThreads('c1', 'p1', threads(1, [scene('s1'), scene('s2')], { sceneId: 's1' }));
    vi.spyOn(videoApi, 'focus').mockResolvedValue(threads(2, [scene('s1'), scene('s2')], { sceneId: 's2' }));
    holdGenPanelOpen(VIDEO_PANEL, 'column');
    await selectSceneByHuman('p1', 'c1', 's2');
    expect(reveals()).toEqual([{ key: VIDEO_PANEL, tab: 'scene', sessionId: 'c1', target: sceneDraftKey('s2'), follow: true }]);
  });

  it('закрытая панель не открывается — выбор просто запомнен', async () => {
    __applyThreads('c1', 'p1', threads(1, [scene('s1'), scene('s2')], { sceneId: 's1' }));
    vi.spyOn(videoApi, 'focus').mockResolvedValue(threads(2, [scene('s1'), scene('s2')], { sceneId: 's2' }));
    await selectSceneByHuman('p1', 'c1', 's2');
    expect(reveals()).toEqual([]);
    expect(getFocusedScene('c1')?.sceneId).toBe('s2');
  });

  it('строка «Фильм собран» уводит на вкладку «Фильм»', async () => {
    __applyThreads('c1', 'p1', threads(1, [scene('s1')], {}));
    vi.spyOn(videoApi, 'focus').mockResolvedValue(threads(2, [scene('s1')], { filmPath: FILM_PATH }));
    holdGenPanelOpen('images', 'column');
    await selectFilmByHuman('p1', 'c1', FILM_PATH);
    expect(reveals()).toEqual([{ key: VIDEO_PANEL, tab: 'film', sessionId: 'c1', target: FILM_PATH, follow: true }]);
  });
});

describe('фильм: правка под ревизией', () => {
  it('409 revision_conflict — фильм перечитан и показан свежим', async () => {
    __setFilm('c1', FILM_PATH, film());
    const fresh = film({ revision: 'aa11' });
    vi.spyOn(videoApi, 'patchFilm').mockRejectedValue(Object.assign(new Error('конфликт'), { status: 409, body: { code: 'revision_conflict' } }));
    vi.spyOn(videoApi, 'filmState').mockResolvedValue(fresh);
    expect(await patchFilm('p1', 'c1', FILM_PATH, [{ op: 'move', from: 0, to: 1 }])).toBe(false);
    expect(getFilm('c1', FILM_PATH).state?.revision).toBe('aa11');
  });

  it('правка уходит с ревизией, ответ ложится в стор', async () => {
    __setFilm('c1', FILM_PATH, film());
    const spy = vi.spyOn(videoApi, 'patchFilm').mockResolvedValue(film({ revision: 'bb22' }));
    expect(await patchFilm('p1', 'c1', FILM_PATH, [{ op: 'cut', index: 1, cutType: 'fade', sec: 2 }])).toBe(true);
    expect(spy.mock.calls[0][3]).toEqual({ expectedRevision: '9f2c', ops: [{ op: 'cut', index: 1, cutType: 'fade', sec: 2 }] });
    expect(getFilm('c1', FILM_PATH).state?.revision).toBe('bb22');
  });
});
