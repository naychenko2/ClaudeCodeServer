// Стор «Видео» по чатам (ADR-022): нити сцен с сервера (GET state + video_thread_changed),
// каталог и префы области, ход съёмки (video_edit_progress → completed / failed), фильмы
// проекта (кеш FilmState + video_film_changed) и локальное состояние экрана.

import { useEffect, useSyncExternalStore } from 'react';
import { onReconnected, refreshChatContext, showToast } from 'aihome_shell/kit';
import {
  conflictState, EMPTY_THREADS, errorText, filmConflict, videoApi,
  type FilmBuildStatus, type FilmPatchOp, type FrameRef, type FilmState, type FilmSummary, type RetryQuote, type VideoCatalog, type VideoEvent,
  type VideoPrefs, type VideoScene, type VideoSceneSettings, type VideoThreadsState,
} from '../api';
import { isPersonalScope } from '../scope';

export const VIDEO_PANEL = 'videoEditor';
// Ключ элемента для черновиков и выбора: панель + сцена («videoEditor:{sceneId}»)
export const sceneDraftKey = (sceneId: string) => `${VIDEO_PANEL}:${sceneId}`;
export const filmTarget = (path: string) => path;

interface Entry { scope: string; state: VideoThreadsState; loaded: boolean; loading: boolean }

// Ход съёмки по последнему video_edit_progress; уходит с completed / failed
export interface JobProgress {
  jobId: string;
  sessionId: string | null;
  sceneId: string;
  stage: 'queued' | 'running' | 'downloading';
  queuePosition: number | null;
  etaSeconds: number | null;
  variant: number;
  count: number;
}

// permanent — отказ сервера 4xx: повторять бессмысленно, причина показана; retryAt/fails — повтор сбоя сети
// с нарастающей паузой
interface FilmEntry {
  state: FilmState | null; loading: boolean; error: string | null; code: string | null;
  permanent?: boolean; retryAt?: number; fails?: number;
}

const _entries = new Map<string, Entry>();
const _catalogs = new Map<string, VideoCatalog>();
const _prefs = new Map<string, VideoPrefs>();
const _jobs = new Map<string, JobProgress>();
const _films = new Map<string, FilmEntry>();
const _filmLists = new Map<string, FilmSummary[]>();
// Кадры, которые поменял агент (video_scene_set): метка «✦ Claude» на кадре, пока человек его не тронул.
// Живёт в сторе сессии: сервер «кто правил кадр» не хранит. sessionId → sceneId → слоты
type Slot = 'A' | 'B';
const _agentEdits = new Map<string, Map<string, Set<Slot>>>();
// Любая своя мутация нитей в полёте: событие, пришедшее за это время, — эхо, а не правка агента
const _mut = new Map<string, number>();
// Свои мутации в полёте по чатам: событие, пришедшее раньше ответа, фокус «агентом» не считается
const _own = new Map<string, number>();
let _version = 0;
const _listeners = new Set<() => void>();
let _unsub: (() => void) | null = null;

function emit() {
  _version++;
  _listeners.forEach(fn => fn());
}

function subscribe(fn: () => void) {
  _listeners.add(fn);
  ensureLive();
  return () => { _listeners.delete(fn); };
}
const getVersion = () => _version;
// Подписка вне React: вид контекста хода будит хост при смене сцен, каталога и фильмов
export const subscribeVideoStore = subscribe;

function apply(sessionId: string, scope: string, state: VideoThreadsState) {
  const e = _entries.get(sessionId);
  // Событие старше того, что уже знаем, — пропускаем
  if (e?.loaded && e.state.revision > state.revision) return;
  _entries.set(sessionId, { scope, state, loaded: true, loading: false });
  emit();
}

// «video/утро/утро.film» → «утро»
export const filmName = (path: string) => {
  const base = path.split('/').pop() ?? path;
  return base.replace(/\.film$/i, '') || base;
};

const filmKey = (sessionId: string, path: string) => `${sessionId}\n${path}`;

function applyFilm(sessionId: string, path: string, state: FilmState) {
  const prev = _films.get(filmKey(sessionId, path))?.state;
  // Событие не откатывает фильм на устаревшее состояние: ревизия — хеш, порядок по нему не определить,
  // поэтому событие с другой ревизией принимается всегда, а совпавшее с уже известным не будит подписчиков
  if (prev && prev.revision === state.revision && JSON.stringify(prev.build) === JSON.stringify(state.build)
    && JSON.stringify(prev.marks) === JSON.stringify(state.marks)) return;
  _films.set(filmKey(sessionId, path), { state, loading: false, error: null, code: null });
  emit();
}

// Событие фильма не привязано к чату (сервер шлёт владельцу scopeKey + path, без sessionId): фильм —
// файл проекта, его видят все чаты проекта. Кладём состояние в каждую запись этого фильма области
function applyFilmEvent(scopeKey: string, path: string, state: FilmState) {
  const tail = `\n${path}`;
  let touched = false;
  for (const k of [..._films.keys()]) {
    if (!k.endsWith(tail)) continue;
    const sid = k.slice(0, k.length - tail.length);
    const scope = _entries.get(sid)?.scope;
    if (scope && scopeKey && scope !== scopeKey) continue;
    applyFilm(sid, path, state);
    touched = true;
  }
  // Меню фильмов («устарел», число сцен) читает список, а не состояние: перечитываем его
  if (touched) _filmLists.forEach((_, sid) => { const scope = _entries.get(sid)?.scope; if (scope && (!scopeKey || scope === scopeKey)) void loadFilmList(scope, sid, true); });
}

// Событие модуля → состояние. Нити — только у уже показанного чата: чужой чат догрузится сам при входе
const frameSig = (f: unknown) => JSON.stringify(f ?? null);

// Версию image-кадра создал человек (правка в «Картинках») — кадр перешёл сам, метки «✦ Claude» нет;
// без initiator (старые данные) остаётся прежняя эвристика «событие без клика человека»
const byHuman = (f: FrameRef | undefined) => f?.kind === 'image' && f.initiator === 'human';

// Событие, которого человек не вызывал, поменяло кадр сцены — его поменял агент
export function noteAgentFrames(sessionId: string, prev: VideoThreadsState | null, next: VideoThreadsState) {
  if (!prev || (_mut.get(sessionId) ?? 0) > 0) return;
  for (const sc of next.scenes) {
    // Новая сцена с кадрами (video_new) — тоже правка агента: её завёл не мой клик
    const was = prev.scenes.find(x => x.sceneId === sc.sceneId)?.settings;
    const slots: Slot[] = [];
    if (frameSig(was?.frameA) !== frameSig(sc.settings.frameA) && sc.settings.frameA && !byHuman(sc.settings.frameA)) slots.push('A');
    if (frameSig(was?.frameB) !== frameSig(sc.settings.frameB) && sc.settings.frameB && !byHuman(sc.settings.frameB)) slots.push('B');
    if (!slots.length) continue;
    const byScene = _agentEdits.get(sessionId) ?? new Map<string, Set<Slot>>();
    const set = byScene.get(sc.sceneId) ?? new Set<Slot>();
    slots.forEach(x => set.add(x));
    byScene.set(sc.sceneId, set);
    _agentEdits.set(sessionId, byScene);
  }
}

export const getAgentFrames = (sessionId: string | null, sceneId: string | null | undefined): ReadonlySet<Slot> | null =>
  (sessionId && sceneId && _agentEdits.get(sessionId)?.get(sceneId)) || null;

// Человек тронул кадр — метка «✦ Claude» с него снимается
export function clearAgentFrame(sessionId: string, sceneId: string | null, slot: Slot) {
  const set = sceneId ? _agentEdits.get(sessionId)?.get(sceneId) : null;
  if (set?.delete(slot)) emit();
}

export function handleEvent(ev: VideoEvent) {
  switch (ev.type) {
    case 'video_thread_changed': {
      const e = _entries.get(ev.sessionId);
      if (!e) return;
      noteAgentFrames(ev.sessionId, e.loaded ? e.state : null, ev.state);
      apply(ev.sessionId, ev.scopeKey, ev.state);
      return;
    }
    case 'video_film_changed':
      applyFilmEvent(ev.scopeKey, ev.path, ev.state);
      return;
    case 'video_edit_progress':
      _jobs.set(ev.jobId, {
        jobId: ev.jobId, sessionId: ev.sessionId, sceneId: ev.sceneId, stage: ev.stage,
        queuePosition: ev.queuePosition ?? null, etaSeconds: ev.etaSeconds ?? null, variant: ev.variant, count: ev.count,
      });
      emit();
      return;
    case 'video_edit_failed':
      _jobs.delete(ev.jobId);
      _failures.set(ev.sessionId, { sceneId: ev.sceneId, text: ev.error || 'Съёмка не получилась', retry: ev.retryQuote ?? null });
      emit();
      return;
    case 'video_edit_completed':
      if (_jobs.delete(ev.jobId)) emit();
      return;
  }
}

function ensureLive() {
  if (_unsub) return;
  const offEvents = videoApi.subscribe(handleEvent);
  // После обрыва события могли потеряться — перечитываем всё, что показано
  const offRe = onReconnected(() => {
    _jobs.clear();
    _entries.forEach((e, sid) => { if (e.loaded) void load(e.scope, sid, true); });
    _films.forEach((_, k) => {
      const [sid, path] = k.split('\n');
      const scope = _entries.get(sid)?.scope;
      if (scope && path) void loadFilm(scope, sid, path, true);
    });
  });
  _unsub = () => { offEvents(); offRe(); };
}

async function load(scope: string, sessionId: string, force = false) {
  const e = _entries.get(sessionId);
  if (e && (e.loading || (e.loaded && !force))) return;
  _entries.set(sessionId, { scope, state: e?.state ?? EMPTY_THREADS, loaded: e?.loaded ?? false, loading: true });
  try {
    const st = await videoApi.state(scope, sessionId);
    _catalogs.set(scope, st.catalog);
    _prefs.set(scope, st.prefs);
    apply(sessionId, scope, st.threads);
  } catch {
    // Модуль выключен или чат не свой — сцен просто нет
    _entries.set(sessionId, { scope, state: EMPTY_THREADS, loaded: true, loading: false });
    emit();
  }
}

// Состояние чата до первой мутации (вход в ещё не открытый чат): без него мутация ушла бы с ревизией 0
export async function ensureVideoThreads(scope: string, sessionId: string): Promise<void> {
  if (_entries.get(sessionId)?.loaded) return;
  await load(scope, sessionId);
}

export function getThreadsState(sessionId: string | null): VideoThreadsState {
  return (sessionId && _entries.get(sessionId)?.state) || EMPTY_THREADS;
}

export function getFocusedScene(sessionId: string | null): VideoScene | null {
  const st = getThreadsState(sessionId);
  return st.focus.sceneId ? st.scenes.find(s => s.sceneId === st.focus.sceneId) ?? null : null;
}

export const getFocusedFilmPath = (sessionId: string | null): string | null => getThreadsState(sessionId).focus.filmPath ?? null;

export const getCatalog = (scope: string): VideoCatalog | null => _catalogs.get(scope) ?? null;
export const getPrefs = (scope: string): VideoPrefs => _prefs.get(scope) ?? {};

// Ответ PUT prefs — сразу в стор
export function setScopePrefs(scope: string, prefs: VideoPrefs) {
  _prefs.set(scope, prefs);
  emit();
}

// Идущие задачи сцены (или чата, если сцена не задана) — для бейджа очереди GPU и прогресса
export function getJobsOf(sessionId: string | null, sceneId: string | null): JobProgress[] {
  if (!sessionId) return [];
  return [..._jobs.values()].filter(j => j.sessionId === sessionId && (sceneId === null || j.sceneId === sceneId));
}

export function useVideoThreads(scope: string, sessionId: string | null): VideoThreadsState {
  useSyncExternalStore(subscribe, getVersion, getVersion);
  useEffect(() => { if (sessionId) void load(scope, sessionId); }, [scope, sessionId]);
  return getThreadsState(sessionId);
}

// ── Редакторы «Сцена» и «Монтаж» (ADR-023, шаг 3ф-2): окно открыто в одном чате за раз ──

export type VideoEditorKind = 'scene' | 'film';
export interface VideoEditorOpen { sessionId: string; kind: VideoEditorKind }
let _editor: VideoEditorOpen | null = null;
export const getVideoEditor = (): VideoEditorOpen | null => _editor;
export function openVideoEditor(sessionId: string, kind: VideoEditorKind) {
  _editor = { sessionId, kind };
  emit();
}
export function closeVideoEditor() {
  if (!_editor) return;
  _editor = null;
  emit();
}

export function useVideoStoreVersion(): number {
  return useSyncExternalStore(subscribe, getVersion, getVersion);
}

// Мутация от текущей ревизии. 409 — применяем состояние из ответа и говорим об этом;
// false — мутация не прошла
export async function mutate(
  scope: string, sessionId: string,
  run: (revision: number) => Promise<VideoThreadsState>,
  // Мутация меняет фокус (выбор сцены, фильма, новая сцена): пока она в полёте, событие с тем же
  // фокусом — эхо своего клика, а не выбор агента
  ownFocus = false,
): Promise<boolean> {
  _mut.set(sessionId, (_mut.get(sessionId) ?? 0) + 1);
  if (ownFocus) _own.set(sessionId, (_own.get(sessionId) ?? 0) + 1);
  try {
    apply(sessionId, scope, await run(getThreadsState(sessionId).revision));
    // Смена фокуса «Видео» ставит сцену/фильм основным объектом на сервере: контекст чата не ждёт события рассылки
if (ownFocus) void refreshChatContext(sessionId);
    return true;
  } catch (e) {
    const st = conflictState(e);
    if (st) {
      apply(sessionId, scope, st);
      showToast('Сцены только что поменяли в другом месте — показываем свежее состояние', '', 'info');
    } else {
      showToast(errorText(e), '', 'error');
    }
    return false;
  } finally {
    const m = (_mut.get(sessionId) ?? 1) - 1;
    if (m <= 0) _mut.delete(sessionId);
    else _mut.set(sessionId, m);
    if (ownFocus) {
      const n = (_own.get(sessionId) ?? 1) - 1;
      if (n <= 0) _own.delete(sessionId);
      else _own.set(sessionId, n);
    }
  }
}

export const focusScene = (scope: string, sessionId: string, sceneId: string | null) =>
  mutate(scope, sessionId, rev => {
    const f = getThreadsState(sessionId).focus;
    return videoApi.focus(scope, sessionId, { ...(sceneId ? { sceneId } : {}), ...(f.filmPath ? { filmPath: f.filmPath } : {}) }, rev);
  }, true);

// Открыть фильм (путь .film) или закрыть (null): сцена в фокусе остаётся
export const focusFilm = (scope: string, sessionId: string, filmPath: string | null) =>
  mutate(scope, sessionId, rev => {
    const f = getThreadsState(sessionId).focus;
    return videoApi.focus(scope, sessionId, { ...(f.sceneId ? { sceneId: f.sceneId } : {}), ...(filmPath ? { filmPath } : {}) }, rev);
  }, true);

// ── Фильмы (блок 2) ──

const EMPTY_FILM: FilmEntry = { state: null, loading: false, error: null, code: null };

export const getFilm = (sessionId: string | null, path: string | null): FilmEntry =>
  (sessionId && path && _films.get(filmKey(sessionId, path))) || EMPTY_FILM;

const FILM_RETRY_BASE_MS = 2000;
const FILM_RETRY_MAX_MS = 60_000;
const _filmRetry = new Map<string, ReturnType<typeof setTimeout>>();

// 4xx (кроме «таймаут» и «слишком часто») — приговор запросу, а не сбой канала
const isPermanentFailure = (e: unknown) => {
  const st = (e as { status?: unknown } | null)?.status;
  return typeof st === 'number' && st >= 400 && st < 500 && st !== 408 && st !== 429;
};

export async function loadFilm(scope: string, sessionId: string, path: string, force = false) {
  if (isPersonalScope(scope)) return;
  const k = filmKey(sessionId, path);
  const cur = _films.get(k);
  if (cur && (cur.loading || (cur.state && !force))) return;
  // Прошлая попытка кончилась отказом: хост зовёт загрузку на каждый рендер, поэтому без явной
  // пересинхронизации (force) отказ 4xx не повторяем вовсе, а сбой сети — не раньше срока
  if (cur?.error && !force && (cur.permanent || Date.now() < (cur.retryAt ?? 0))) return;
  clearTimeout(_filmRetry.get(k));
  _filmRetry.delete(k);
  _films.set(k, { state: cur?.state ?? null, loading: true, error: null, code: null, fails: cur?.fails });
  emit();
  try {
    const st = await videoApi.filmState(scope, sessionId, path);
    _films.set(k, { state: st, loading: false, error: null, code: null });
  } catch (e) {
    const code = (e as { body?: { code?: unknown } } | null)?.body?.code;
    const permanent = isPermanentFailure(e);
    const fails = (cur?.fails ?? 0) + 1;
    const wait = Math.min(FILM_RETRY_MAX_MS, FILM_RETRY_BASE_MS * 2 ** (fails - 1));
    _films.set(k, {
      state: cur?.state ?? null, loading: false, error: errorText(e, 'Фильм не прочитался'),
      code: typeof code === 'string' ? code : null, permanent, fails, retryAt: permanent ? undefined : Date.now() + wait,
    });
    if (!permanent) _filmRetry.set(k, setTimeout(() => { void loadFilm(scope, sessionId, path); }, wait));
  }
  emit();
}

export function useFilm(scope: string, sessionId: string | null, path: string | null): FilmEntry {
  useSyncExternalStore(subscribe, getVersion, getVersion);
  useEffect(() => { if (sessionId && path) void loadFilm(scope, sessionId, path); }, [scope, sessionId, path]);
  return getFilm(sessionId, path);
}

export const getFilmList = (sessionId: string | null): FilmSummary[] => (sessionId && _filmLists.get(sessionId)) || [];

export async function loadFilmList(scope: string, sessionId: string, force = false) {
  if (isPersonalScope(scope) || (!force && _filmLists.has(sessionId))) return;
  try {
    _filmLists.set(sessionId, await videoApi.films(scope, sessionId));
    emit();
  } catch {
    // Список фильмов не критичен: меню покажет пустой список
  }
}

export function useFilmList(scope: string, sessionId: string | null): FilmSummary[] {
  useSyncExternalStore(subscribe, getVersion, getVersion);
  useEffect(() => { if (sessionId) void loadFilmList(scope, sessionId); }, [scope, sessionId]);
  return getFilmList(sessionId);
}

// Правка фильма атомарным патчем под ревизией. 409 revision_conflict — перечитываем фильм и говорим
// об этом: чужая правка не затирается молча. false — не прошло
export async function patchFilm(scope: string, sessionId: string, path: string, ops: FilmPatchOp[]): Promise<boolean> {
  const cur = getFilm(sessionId, path).state;
  if (!cur) return false;
  try {
    const st = await videoApi.patchFilm(scope, sessionId, path, { expectedRevision: cur.revision, ops });
    _films.set(filmKey(sessionId, path), { state: st, loading: false, error: null, code: null });
    emit();
    return true;
  } catch (e) {
    const conflict = filmConflict(e);
    if (conflict) {
      if (conflict.state) {
        _films.set(filmKey(sessionId, path), { state: conflict.state, loading: false, error: null, code: null });
        emit();
      } else await loadFilm(scope, sessionId, path, true);
      showToast('Фильм только что поменяли в другом месте — показываем свежий', '', 'info');
    } else {
      showToast(errorText(e), '', 'error');
    }
    return false;
  }
}

// Статус сборки едет в FilmState.build (video_film_changed); ответ POST кладём сразу, не дожидаясь события
export function setFilmBuild(sessionId: string, path: string, build: FilmBuildStatus) {
  const k = filmKey(sessionId, path);
  const cur = _films.get(k);
  if (cur?.state) _films.set(k, { ...cur, state: { ...cur.state, build } });
  emit();
}

// Отказ возвращается с кодом: 503 dsp_unavailable делает «Собрать» серым с причиной
export async function buildFilm(scope: string, sessionId: string, path: string): Promise<{ ok: boolean; code: string | null; text: string }> {
  try {
    setFilmBuild(sessionId, path, await videoApi.buildFilm(scope, sessionId, path));
    return { ok: true, code: null, text: '' };
  } catch (e) {
    const code = (e as { body?: { code?: unknown } } | null)?.body?.code;
    const text = errorText(e, 'Сборка не запустилась');
    if (code !== 'dsp_unavailable') showToast(text, '', 'error');
    return { ok: false, code: typeof code === 'string' ? code : null, text };
  }
}

export async function cancelBuild(scope: string, sessionId: string, path: string): Promise<void> {
  try {
    const b = await videoApi.cancelBuild(scope, sessionId, path);
    const k = filmKey(sessionId, path);
    const cur = _films.get(k);
    if (cur?.state) _films.set(k, { ...cur, state: { ...cur.state, build: b } });
    emit();
  } catch (e) {
    showToast(errorText(e), '', 'error');
  }
}

// ── Несохранённая правка настроек: общая для панели, полосы и композера ──
// sceneId null — сцены ещё нет (правка станет новой сценой)
export interface PendingSettings { sceneId: string | null; patch: Partial<VideoSceneSettings> }
const _pending = new Map<string, PendingSettings>();

export const getPending = (sessionId: string | null, sceneId: string | null): PendingSettings | null => {
  const p = sessionId ? _pending.get(sessionId) : null;
  return p && p.sceneId === sceneId ? p : null;
};

export const getPendingAny = (sessionId: string | null): PendingSettings | null => (sessionId && _pending.get(sessionId)) || null;

export function setPending(sessionId: string, p: PendingSettings | null) {
  if (p) _pending.set(sessionId, p);
  else if (!_pending.delete(sessionId)) return;
  emit();
}

export const getScopeOf = (sessionId: string | null): string | null => (sessionId && _entries.get(sessionId)?.scope) || null;

// Отказ последнего запуска сцены: текст и котировка соседа («Повторить через …») — решает человек
export interface LaunchFailure { sceneId: string; text: string; retry: RetryQuote | null }
const _failures = new Map<string, LaunchFailure>();
export const getFailure = (sessionId: string | null, sceneId: string | null): LaunchFailure | null => {
  const f = sessionId ? _failures.get(sessionId) : null;
  return f && f.sceneId === sceneId ? f : null;
};
export function setFailure(sessionId: string, f: LaunchFailure | null) {
  if (f) _failures.set(sessionId, f);
  else if (!_failures.delete(sessionId)) return;
  emit();
}

// Цена из последней котировки панели для сводки полосы («≈ $3.20»): полоса сама котировок не просит
const _priceHints = new Map<string, { sceneId: string; text: string }>();
export const getPriceHint = (sessionId: string | null, sceneId: string | null): string | null => {
  const h = sessionId ? _priceHints.get(sessionId) : null;
  return h && h.sceneId === sceneId ? h.text : null;
};
export function setPriceHint(sessionId: string, sceneId: string, text: string | null) {
  const cur = _priceHints.get(sessionId);
  if (!text) { if (cur && _priceHints.delete(sessionId)) emit(); return; }
  if (cur && cur.sceneId === sceneId && cur.text === text) return;
  _priceHints.set(sessionId, { sceneId, text });
  emit();
}

// Сброс — только для тестов
export function __resetVideoStore() {
  _priceHints.clear();
  _failures.clear();
  _pending.clear();
  _entries.clear();
  _catalogs.clear();
  _prefs.clear();
  _jobs.clear();
  _filmRetry.forEach(clearTimeout);
  _filmRetry.clear();
  _films.clear();
  _filmLists.clear();
  _own.clear();
  _mut.clear();
  _agentEdits.clear();
  emit();
}

// Для тестов: подставить состояние чата, как будто оно пришло с сервера
export function __applyThreads(sessionId: string, scope: string, state: VideoThreadsState) {
  apply(sessionId, scope, state);
}

export function __setScopeData(scope: string, catalog: VideoCatalog, prefs: VideoPrefs) {
  _catalogs.set(scope, catalog);
  _prefs.set(scope, prefs);
  emit();
}

export function __setFilm(sessionId: string, path: string, state: FilmState) {
  _films.set(filmKey(sessionId, path), { state, loading: false, error: null, code: null });
  emit();
}
