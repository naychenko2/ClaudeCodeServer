import { beforeEach, describe, expect, it, vi } from 'vitest';

// Запуск, цена, «Чем» и параметры действий «Видео»: тонкий слой над существующими котировкой, съёмкой и
// сборкой фильма. Окружение node: хранилища и window подставляем сами
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

// Связь кадров с «Картинками» слушает SignalR, которого в node нет
vi.mock('../store/imageFrames', async orig => ({ ...await orig<typeof import('../store/imageFrames')>(), onFrameReady: () => () => {} }));

const { videoApi } = await import('../api');
const { __applyThreads, __resetVideoStore, __setFilm, __setScopeData, getFilm } = await import('../store/videoStore');
const { __applyChatContext, __resetChatContextStore } = await import('../../../lib/chatContext/store');
const { __resetVideoExecutors, NO_AI_ROW } = await import('./executors');
const { videoKindApi } = await import('./kind');
const { CATALOG, film, FILM_PATH, PREFS, QUOTE, scene, threads, version } = await import('../mocks');
import type { ChatContextDto } from '../../../lib/chatContext/types';

const P = 'p1';
const S = 's1';
const CTX = { projectId: P, sessionId: S, isMobile: false };
const AT = '2026-10-03T00:00:00Z';

const primary = (kind: string, ref: Record<string, unknown>, label: string) =>
  ({ id: 'v1', kind, ref, by: 'human' as const, addedAt: AT, label, version: null, thumb: null, missing: false, role: null });
const sceneCtx = (revision: number): ChatContextDto => ({ revision, refs: [], primary: primary('video-scene', { sceneId: 'sc1' }, 'сцена 1') });
const filmCtx = (revision: number): ChatContextDto => ({ revision, refs: [], primary: primary('video-film', { filmPath: FILM_PATH }, 'утро') });

let quoted: Record<string, unknown>[];
let started: Record<string, unknown>[];
let builds: { path: string; revision: number | undefined }[];
let saved: Record<string, unknown>[];

beforeEach(() => {
  __resetVideoStore(); __resetChatContextStore(); __resetVideoExecutors();
  quoted = []; started = []; builds = []; saved = [];
  vi.spyOn(videoApi, 'subscribe').mockImplementation(() => () => {});
  vi.spyOn(videoApi, 'quote').mockImplementation(async (_s, _c, req) => { quoted.push({ ...req }); return QUOTE; });
  vi.spyOn(videoApi, 'startJob').mockImplementation(async (_s, _c, req) => { started.push({ ...req }); return { jobId: 'job1' }; });
  vi.spyOn(videoApi, 'buildFilm').mockImplementation(async (_s, _c, path, revision) => {
    builds.push({ path, revision });
    return { state: 'running', progress: 0.3 };
  });
  vi.spyOn(videoApi, 'settings').mockImplementation(async (_s, _c, _id, settings) => {
    saved.push({ ...settings });
    return threads(5, [scene('sc1')]);
  });
  __setScopeData(P, CATALOG, PREFS);
});

const applyScene = (revision = 7) => { __applyThreads(S, P, threads(4, [scene('sc1')], { sceneId: 'sc1' })); __applyChatContext(S, sceneCtx(revision)); };
const applyFilm = (revision = 7) => {
  __applyThreads(S, P, threads(4, []));
  __setFilm(S, FILM_PATH, film());
  __applyChatContext(S, filmCtx(revision));
};
const run = (op: string, text = '', params: Record<string, string | number> = {}, contextRevision = 7) =>
  videoKindApi.launch!(CTX, { op, text, params, contextRevision });

describe('запуск съёмки по ревизии контекста', () => {
  it('котировка и запуск несут ревизию, пустой sceneId и параметры; кадры и сцена из тела не едут', async () => {
    applyScene();
    const h = await run('shoot', '', { variants: 2, duration: 6 });
    expect(h.id).toBe('job1');
    expect(quoted[0]).toMatchObject({ sessionId: S, sceneId: '', count: 2, durationSec: 6, provider: 'fal', model: 'veo-3.1', contextRevision: 7 });
    expect(quoted[0].frameA).toBeUndefined();
    expect(started[0]).toMatchObject({ quoteId: 'q-1', sessionId: S, sceneId: '', contextRevision: 7 });
    expect(started[0].params).toBeUndefined();
  });

  it('текст поля уходит просьбой в params.request, пустое поле — без params', async () => {
    applyScene();
    await run('shoot', '  добавь туман над долиной ');
    expect(started[0].params).toEqual({ request: 'добавь туман над долиной' });
    await run('shoot', '   ');
    expect(started[1].params).toBeUndefined();
  });

  it('без параметров хоста берутся настройки сцены', async () => {
    applyScene();
    await run('shoot');
    expect(quoted[0]).toMatchObject({ count: 2, durationSec: 8, aspect: '16:9' });
  });

  it('сцена пропала (контекст сменился) — запуск отказывает без запроса', async () => {
    __applyChatContext(S, sceneCtx(7));
    await expect(run('shoot')).rejects.toThrow(/недоступна/);
    expect(quoted).toHaveLength(0);
  });

  it('409 context_changed уходит хосту как есть, остальные отказы — текстом', async () => {
    applyScene();
    const conflict = Object.assign(new Error('conflict'), { status: 409, body: { error: 'context_changed', context: sceneCtx(8) } });
    vi.spyOn(videoApi, 'startJob').mockRejectedValueOnce(conflict);
    await expect(run('shoot')).rejects.toBe(conflict);
    vi.spyOn(videoApi, 'startJob').mockRejectedValueOnce(Object.assign(new Error('x'), { status: 409, body: { code: 'local_unavailable_personal', error: 'x' } }));
    await expect(run('shoot')).rejects.toThrow(/Локальные модели работают только в чате проекта/);
  });
});

describe('сборка фильма по ревизии контекста', () => {
  it('«Собрать»: путь из основного объекта и ревизия в запросе, статус кладётся в стор сразу', async () => {
    applyFilm();
    const h = await run('build');
    expect(h.id).toBe(`build:${FILM_PATH}`);
    expect(builds).toEqual([{ path: FILM_PATH, revision: 7 }]);
    expect(getFilm(S, FILM_PATH).state?.build).toEqual({ state: 'running', progress: 0.3 });
    expect(quoted).toHaveLength(0);
  });

  it('прогресс и итог читаются из состояния фильма', async () => {
    applyFilm();
    const h = await run('build');
    const events: object[] = [];
    h.watch(e => events.push(e));
    expect(events[0]).toEqual({ progress: 0.3 });
    __setFilm(S, FILM_PATH, film({ build: { state: 'done', progress: 1, file: 'video/утро/film.mp4' } }));
    expect(events.at(-1)).toMatchObject({ result: { summary: 'Фильм собран' } });
  });

  it('503 dsp_unavailable — понятный текст, а не общий отказ', async () => {
    applyFilm();
    vi.spyOn(videoApi, 'buildFilm').mockRejectedValueOnce(Object.assign(new Error('503'), { status: 503, body: { code: 'dsp_unavailable', error: 'x' } }));
    await expect(run('build')).rejects.toThrow(/Сборка фильмов на этом сервере выключена/);
  });
});

describe('цена, «Чем» и параметры', () => {
  it('сборка идёт без ИИ: цена «бесплатно» без запроса', async () => {
    applyFilm();
    expect(await videoKindApi.quote!(CTX, { op: 'build', text: '', params: {}, contextRevision: 7 })).toMatchObject({ price: 'бесплатно' });
    expect(quoted).toHaveLength(0);
  });

  it('съёмка: цена из котировки по ревизии контекста', async () => {
    applyScene();
    const q = await videoKindApi.quote!(CTX, { op: 'shoot', text: '', params: { variants: 2 }, contextRevision: 7 });
    expect(q.price).toMatch(/3\.20/);
    expect(q.detail).toContain('Veo 3.1');
    expect(quoted[0]).toMatchObject({ contextRevision: 7, count: 2 });
  });

  it('«Чем» у «Собрать» — одна строка «Без ИИ»; у «Снять» — каталог, выбор пишется в настройки сцены', () => {
    applyFilm();
    expect(videoKindApi.executors!(CTX, 'build')!.rows).toEqual([NO_AI_ROW]);
    applyScene();
    const m = videoKindApi.executors!(CTX, 'shoot')!;
    expect(m.value).toBe('fal|veo-3.1');
    expect(m.rows.map(r => r.id)).toContain('local|minimax-h3');
    m.onChange('local|minimax-h3');
    return vi.waitFor(() => expect(saved[0]).toMatchObject({ provider: 'local', model: 'minimax-h3' }));
  });

  it('параметры «Снять»: варианты с потолком каталога и длительность модели; у «Собрать» их нет', () => {
    applyScene();
    expect(videoKindApi.params!(CTX, 'shoot')).toEqual([
      { kind: 'variants', min: 1, max: 4, value: 2 },
      { kind: 'duration', options: [4, 6, 8], value: 8 },
    ]);
    applyFilm();
    expect(videoKindApi.params!(CTX, 'build')).toEqual([]);
  });

  it('при «Авто» длительности — всё, что умеет хоть одна доступная модель', () => {
    __applyThreads(S, P, threads(4, [scene('sc1', { settings: { ...scene('sc1').settings, provider: undefined, model: undefined } })]));
    __applyChatContext(S, sceneCtx(7));
    __setScopeData(P, CATALOG, {});
    const [, duration] = videoKindApi.params!(CTX, 'shoot');
    expect(duration).toMatchObject({ kind: 'duration', options: [4, 5, 6, 8, 10] });
  });
});

describe('вид из сторов', () => {
  const ids = (ctxDto: ChatContextDto) => videoKindApi.actions(CTX, { primary: ctxDto.primary!, refs: ctxDto.refs }).map(a => a.id);

  it('сцена: клип есть — «Переснять»; кадры берутся из референсов, а не из настроек сцены', () => {
    __applyThreads(S, P, threads(4, [scene('sc1', { versions: [version(1)] })]));
    const dto = sceneCtx(7);
    const shoot = videoKindApi.actions(CTX, { primary: dto.primary!, refs: [] })[0];
    expect(shoot.label).toBe('Переснять');
    // В настройках сцены оба кадра есть, но референсов нет — «Снять» серая
    expect(shoot.disabledReason).toMatch(/Нужны оба кадра/);
    const ref = (role: string) => ({ id: role, kind: 'project-file', ref: { path: 'a.png' }, role, by: 'human' as const, addedAt: AT, label: 'a.png', version: null, thumb: null, missing: false, usedBy: ['shoot'] });
    const live = videoKindApi.actions(CTX, { primary: dto.primary!, refs: [ref('frame-a'), ref('frame-b')] })[0];
    expect(live.disabledReason).toBeUndefined();
  });

  it('сцена ещё грузится — действий нет', () => {
    expect(ids(sceneCtx(7))).toEqual([]);
  });

  it('фильм: «Пересобрать» после сборки, «Монтаж» — editor', () => {
    __applyThreads(S, P, threads(4, []));
    __setFilm(S, FILM_PATH, film({ document: { ...film().document, builds: [{ file: 'video/утро/f.mp4', sourceHash: 'h', at: AT }] } }));
    const a = videoKindApi.actions(CTX, { primary: filmCtx(7).primary!, refs: [] });
    expect(a.map(x => [x.id, x.label, x.kind])).toEqual([['build', 'Пересобрать', 'run'], ['montage', 'Монтаж', 'editor']]);
  });

  it('роли кадров принимает только сцена и только картинка или файл проекта', () => {
    const sc = sceneCtx(7).primary!;
    expect(videoKindApi.refRoles!(CTX, sc, 'image').map(r => r.role)).toEqual(['frame-a', 'frame-b']);
    expect(videoKindApi.refRoles!(CTX, sc, 'project-file').map(r => r.role)).toEqual(['frame-a', 'frame-b']);
    expect(videoKindApi.refRoles!(CTX, sc, 'audio')).toEqual([]);
    expect(videoKindApi.refRoles!(CTX, filmCtx(7).primary!, 'image')).toEqual([]);
  });
});
