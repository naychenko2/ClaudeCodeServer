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

import { __resetGenPanelOpen, holdGenPanelOpen } from '../../../lib/genPanelOpen';
import { REVEAL_PANEL_EVENT } from '../../../lib/subsystems/registryCore';
import { videoApi } from '../api';
import { CATALOG, film, FILM_PATH, PREFS, scene, threads } from '../mocks';
import { saveTargetFor } from '../film/model';
import { startSceneSave } from '../feed/SceneCard';
import { saveScene, selectFilmByHuman, selectSceneByHuman } from '../scene/actions';
import {
  __applyThreads, __resetVideoStore, __setFilm, clearAgentFrame, ensureVideoThreads, getAgentFrames, getFailure, getFilm, getFocusedScene, getJobsOf, handleEvent, mutate,
  patchFilm, sceneDraftKey, VIDEO_PANEL,
} from './videoStore';

const reveals = () => dispatched.filter(d => d.type === REVEAL_PANEL_EVENT).map(d => d.detail);
const changed = (revision: number, st = threads(revision, [scene('s1'), scene('s2')])) =>
  ({ type: 'video_thread_changed' as const, sessionId: 'c1', scopeKey: 'p1', revision, state: st });

beforeEach(() => {
  ls.clear();
  dispatched.length = 0;
  __resetVideoStore();
  __resetGenPanelOpen();
  vi.restoreAllMocks();
});

describe('стор «Видео»: события → состояние', () => {
  it('загрузка берёт сцены, каталог и префы и выбирает сцену серверного фокуса', async () => {
    vi.spyOn(videoApi, 'state').mockResolvedValue({ threads: threads(3, [scene('s1')], { sceneId: 's1' }), catalog: CATALOG, prefs: PREFS });
    await ensureVideoThreads('p1', 'c1');
    expect(getFocusedScene('c1')?.sceneId).toBe('s1');
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

describe('клик по карточке: панель следует за выбором с явной вкладкой', () => {
  it('открытая панель переключается на «Сцену» с ключом элемента', async () => {
    __applyThreads('c1', 'p1', threads(1, [scene('s1'), scene('s2')], { sceneId: 's1' }));
    vi.spyOn(videoApi, 'focus').mockResolvedValue(threads(2, [scene('s1'), scene('s2')], { sceneId: 's2' }));
    holdGenPanelOpen(VIDEO_PANEL, 'column');
    await selectSceneByHuman('p1', 'c1', 's2');
    expect(reveals()).toEqual([{ key: 'chatContext', tab: 'scene', sessionId: 'c1', target: sceneDraftKey('s2'), follow: true }]);
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
    expect(reveals()).toEqual([{ key: 'chatContext', tab: 'film', sessionId: 'c1', target: FILM_PATH, follow: true }]);
  });
});

describe('фильм: событие без sessionId', () => {
  it('сервер шлёт scopeKey + path — состояние применяется без перезагрузки', () => {
    __applyThreads('c1', 'p1', threads(1, [scene('s1')]));
    __setFilm('c1', FILM_PATH, film());
    const building = film({ build: { state: 'waiting', progress: 0 } });
    handleEvent({ type: 'video_film_changed', scopeKey: 'p1', path: FILM_PATH, state: building } as never);
    expect(getFilm('c1', FILM_PATH).state?.build?.state).toBe('waiting');
    handleEvent({ type: 'video_film_changed', scopeKey: 'p1', path: FILM_PATH, state: film({ build: { state: 'running', progress: 0.4 } }) } as never);
    expect(getFilm('c1', FILM_PATH).state?.build?.progress).toBe(0.4);
  });

  it('фильм чужой области событием не трогается', () => {
    __applyThreads('c1', 'p1', threads(1, [scene('s1')]));
    __setFilm('c1', FILM_PATH, film());
    handleEvent({ type: 'video_film_changed', scopeKey: 'other', path: FILM_PATH, state: film({ revision: 'zz' }) } as never);
    expect(getFilm('c1', FILM_PATH).state?.revision).toBe('9f2c');
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

describe('B2: «Сохранить сцену» и «В фильм →» — открытый фильм старше папки сцены', () => {
  it('фильм открыт — уходит его полный путь, папка сцены игнорируется', () => {
    expect(saveTargetFor(FILM_PATH, scene('s1', { folder: '' }))).toEqual({ filmPath: FILM_PATH });
    expect(saveTargetFor(FILM_PATH, scene('s1', { folder: 'video/старая' }))).toEqual({ filmPath: FILM_PATH });
  });

  it('нет фильма: папка сцены, иначе null — тогда человека спрашивают, в какой фильм', () => {
    expect(saveTargetFor(null, scene('s1', { folder: 'video/утро' }))).toEqual({ folder: 'video/утро' });
    expect(saveTargetFor(null, scene('s1', { folder: '' }))).toBeNull();
  });

  it('saveScene отдаёт серверу путь открытого фильма (filmPath) без folder', async () => {
    const spy = vi.spyOn(videoApi, 'save').mockResolvedValue({ path: 'video/утро/scene-01.mp4', framePaths: [], addedToFilm: true });
    await saveScene('p1', 'c1', scene('s1', { folder: '' }), 'ver-1', saveTargetFor(FILM_PATH, scene('s1', { folder: '' })));
    expect(spy.mock.calls[0][3]).toEqual({ versionId: 'ver-1', filmPath: FILM_PATH });
  });
});

describe('B2: место вызова — карточка сцены', () => {
  const saved = { path: 'video/утро/scene-01.mp4', framePaths: [], addedToFilm: true };

  it('открыт фильм — в запрос уходит filmPath, а не папка сцены', async () => {
    __applyThreads('c1', 'p1', threads(1, [scene('s1', { folder: 'video/старая' })], { sceneId: 's1', filmPath: FILM_PATH }));
    const spy = vi.spyOn(videoApi, 'save').mockResolvedValue(saved);
    vi.spyOn(videoApi, 'films').mockResolvedValue([]);
    expect(startSceneSave('p1', 'c1', scene('s1', { folder: 'video/старая' }), 'ver-1', 'save')).toBe(true);
    await vi.waitFor(() => expect(spy).toHaveBeenCalled());
    expect(spy.mock.calls[0][3]).toEqual({ versionId: 'ver-1', filmPath: FILM_PATH });
  });

  it('фильм не открыт — папка сцены; нет и её — запрос не уходит, человека спросят', async () => {
    __applyThreads('c1', 'p1', threads(1, [scene('s1')], { sceneId: 's1' }));
    const spy = vi.spyOn(videoApi, 'save').mockResolvedValue(saved);
    vi.spyOn(videoApi, 'films').mockResolvedValue([]);
    expect(startSceneSave('p1', 'c1', scene('s1', { folder: 'video/утро' }), 'ver-1', 'save')).toBe(true);
    await vi.waitFor(() => expect(spy).toHaveBeenCalled());
    expect(spy.mock.calls[0][3]).toEqual({ versionId: 'ver-1', folder: 'video/утро' });
    expect(startSceneSave('p1', 'c1', scene('s1', { folder: '' }), 'ver-1', 'save')).toBe(false);
    expect(spy).toHaveBeenCalledTimes(1);
  });
});

describe('B21: кадр, поставленный агентом, — метка «✦ Claude»', () => {
  const withFrame = (path: string) => scene('s1', { settings: { ...scene('s1').settings, frameA: { kind: 'file', path } } });
  it('событие без своей мутации, сменившее кадр, ставит метку; человек её снимает', () => {
    __applyThreads('c1', 'p1', threads(1, [withFrame('a.png')], { sceneId: 's1' }));
    handleEvent(changed(2, threads(2, [withFrame('b.png')], { sceneId: 's1' })));
    expect(getAgentFrames('c1', 's1')?.has('A')).toBe(true);
    clearAgentFrame('c1', 's1', 'A');
    expect(getAgentFrames('c1', 's1')?.has('A')).toBe(false);
  });
  it('эхо своей мутации метку не ставит', async () => {
    __applyThreads('c1', 'p1', threads(1, [withFrame('a.png')], { sceneId: 's1' }));
    vi.spyOn(videoApi, 'settings').mockImplementation(async () => {
      handleEvent(changed(2, threads(2, [withFrame('b.png')], { sceneId: 's1' })));
      return threads(3, [withFrame('b.png')], { sceneId: 's1' });
    });
    await mutate('p1', 'c1', rev => videoApi.settings('p1', 'c1', 's1', withFrame('b.png').settings, rev));
    expect(getAgentFrames('c1', 's1')).toBeNull();
  });
  describe('initiator версии, на которую кадр перешёл', () => {
    const img = (versionId: string, initiator?: 'human' | 'agent') =>
      scene('s1', { settings: { ...scene('s1').settings, frameA: { kind: 'image', threadId: 't1', versionId, follow: true, ...(initiator ? { initiator } : {}) } } });
    const run = (initiator?: 'human' | 'agent') => {
      __applyThreads('c1', 'p1', threads(1, [img('v1')], { sceneId: 's1' }));
      handleEvent(changed(2, threads(2, [img('v2', initiator)], { sceneId: 's1' })));
      return getAgentFrames('c1', 's1')?.has('A') ?? false;
    };
    it('human — правка человека в «Картинках», метки нет', () => expect(run('human')).toBe(false));
    it('agent — метка есть', () => expect(run('agent')).toBe(true));
    it('undefined — прежняя эвристика, метка есть', () => expect(run(undefined)).toBe(true));
  });
});
