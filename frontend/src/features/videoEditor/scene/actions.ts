// Действия полосы, панели и композера «Видео»: настройки сцены (одна цепочка для всех), новая
// сцена, съёмка по котировке (деньги — только quote → job), снятие выбора, кадры и «Картинки».

import {
  api, clearGenDraft, createReleaseUndo, followSelection, requestStrip, revealWorkspacePanel, showToast,
} from 'aihome_shell/kit';
import {
  ERR, errorCode, errorText, retryOf, videoApi,
  type FrameRef, type SaveSceneResult, type VideoPrefs, type VideoQuote, type VideoScene, type VideoSceneSettings,
} from '../api';
import { isPersonalScope } from '../scope';
import { bindFrame, createImageThread, onFrameReady, type FrameBinding } from '../store/imageFrames';
import {
  ensureVideoThreads, filmTarget, focusFilm, focusScene, getCatalog, getFocusedScene, getPending, getPendingAny, getPrefs,
  getScopeOf, getThreadsState, mutate, sceneDraftKey, setFailure, setPending, setScopePrefs, VIDEO_PANEL, VIDEO_STRIP,
  type PendingSettings,
} from '../store/videoStore';
import { PERSONAL_LOCAL_REASON, resolveScene, settingsOf, type ResolvedScene } from './model';

const SAVE_DELAY = 500;
const CONTENT_KEYS = ['frameA', 'frameB', 'text'];
const _timers = new Map<string, ReturnType<typeof setTimeout>>();

// Настройки, которые видит человек: сцена → несохранённая правка → префы → умолчание
export function currentResolved(sessionId: string | null, scope: string, scene: VideoScene | null = getFocusedScene(sessionId)): ResolvedScene {
  const pend = getPending(sessionId, scene?.sceneId ?? null);
  return resolveScene(scene, pend?.patch ?? null, getPrefs(scope), getCatalog(scope));
}

// Правка поля копится в стоящей правке и уходит на сервер по таймеру (или сразу). Без сцены поля
// содержимого (кадры, текст) заводят новую сцену, остальное — выбор области (префы)
export function changeSettings(scope: string, sessionId: string, patch: Partial<VideoSceneSettings>, debounced = false): void {
  const sceneId = getFocusedScene(sessionId)?.sceneId ?? null;
  const prev = getPending(sessionId, sceneId);
  setPending(sessionId, { sceneId, patch: { ...(prev?.patch ?? {}), ...patch } });
  const t = _timers.get(sessionId);
  if (t) clearTimeout(t);
  if (debounced) _timers.set(sessionId, setTimeout(() => { void flushSettings(scope, sessionId); }, SAVE_DELAY));
  else { _timers.delete(sessionId); void flushSettings(scope, sessionId); }
}

function dropIf(sessionId: string, sent: PendingSettings) {
  const now = getPendingAny(sessionId);
  // Снимаем только то, что отправили: более свежая правка остаётся поверх
  if (now && now.sceneId === sent.sceneId && JSON.stringify(now.patch) === JSON.stringify(sent.patch)) setPending(sessionId, null);
}

function prefsPatch(p: Partial<VideoSceneSettings>): VideoPrefs {
  const o: VideoPrefs = {};
  if ('provider' in p) o.provider = p.provider;
  if ('model' in p) o.model = p.model;
  if ('durationSec' in p) o.durationSec = p.durationSec;
  if ('aspect' in p) o.aspect = p.aspect;
  if ('sound' in p) o.sound = p.sound;
  if ('count' in p) o.count = p.count;
  return o;
}

const _flushing = new Map<string, Promise<boolean>>();

// Отправить стоящую правку сейчас (смена сцены, закрытие панели, запуск). Отправки одного чата идут
// строго друг за другом: две записи с одной ревизией дали бы 409 на второй
export function flushSettings(scope: string, sessionId: string): Promise<boolean> {
  const t = _timers.get(sessionId);
  if (t) { clearTimeout(t); _timers.delete(sessionId); }
  const prev = _flushing.get(sessionId) ?? Promise.resolve(true);
  const next = prev.then(() => flushNow(scope, sessionId));
  _flushing.set(sessionId, next);
  void next.finally(() => { if (_flushing.get(sessionId) === next) _flushing.delete(sessionId); });
  return next;
}

async function flushNow(scope: string, sessionId: string): Promise<boolean> {
  const pend = getPendingAny(sessionId);
  if (!pend) return true;
  await ensureVideoThreads(scope, sessionId);
  const scene = pend.sceneId ? getThreadsState(sessionId).scenes.find(s => s.sceneId === pend.sceneId) ?? null : null;
  if (scene) {
    // Правка видна человеком целиком: сохранённые настройки сцены плюс его поля
    const settings: VideoSceneSettings = { ...scene.settings, ...pend.patch };
    const ok = await mutate(scope, sessionId, rev => videoApi.settings(scope, sessionId, scene.sceneId, settings, rev));
    if (ok) dropIf(sessionId, pend);
    return ok;
  }
  if (Object.keys(pend.patch).some(k => CONTENT_KEYS.includes(k))) {
    const settings = settingsOf(resolveScene(null, pend.patch, getPrefs(scope), getCatalog(scope)));
    const ok = await mutate(scope, sessionId, rev => videoApi.addScene(scope, sessionId, { settings, revision: rev }), true);
    if (ok) dropIf(sessionId, pend);
    return ok;
  }
  // Выбор области: поставщик, модель, длительность, пропорции, звук, число вариантов
  try {
    setScopePrefs(scope, await videoApi.putPrefs(scope, sessionId, { ...getPrefs(scope), ...prefsPatch(pend.patch) }));
    dropIf(sessionId, pend);
    return true;
  } catch (e) {
    showToast(errorText(e), '', 'error');
    return false;
  }
}

// ── Новая сцена, выбор, снятие ──

export async function createScene(scope: string, sessionId: string, opts: { frameA?: FrameRef; name?: string } = {}): Promise<boolean> {
  await flushSettings(scope, sessionId);
  await ensureVideoThreads(scope, sessionId);
  const settings: VideoSceneSettings = {
    ...settingsOf(resolveScene(null, null, getPrefs(scope), getCatalog(scope))),
    ...(opts.frameA ? { frameA: opts.frameA } : {}),
  };
  return mutate(scope, sessionId, rev => videoApi.addScene(scope, sessionId, { settings, ...(opts.name ? { name: opts.name } : {}), revision: rev }), true);
}

// Кадр B прошлой сцены → кадр A новой (стык без скачка)
export const frameBOf = (s: VideoScene | undefined | null): FrameRef | null => s?.settings.frameB ?? null;

export interface ReleaseSnapshot { scope: string; sessionId: string; sceneId: string }
export const videoReleaseUndo = createReleaseUndo<ReleaseSnapshot>();

// ✕ «Снять выбор — новая сцена»: сцена остаётся в ленте, плашка «Вернуть» на 4 секунды
export async function releaseFocus(scope: string, sessionId: string, scene: VideoScene | null) {
  if (!scene) return;
  await flushSettings(scope, sessionId);
  if (!await focusScene(scope, sessionId, null)) return;
  videoReleaseUndo.release({ snapshot: { scope, sessionId, sceneId: scene.sceneId }, text: `${scene.name} снята — дальше снимаем новую сцену` }, true);
}

export async function undoRelease(): Promise<boolean> {
  const s = videoReleaseUndo.undo();
  return s ? selectSceneByHuman(s.scope, s.sessionId, s.sceneId) : false;
}

// Клик по карточке сцены в ленте: панель следует за выбором, только если открыта; вкладка — явная
export async function selectSceneByHuman(scope: string, sessionId: string, sceneId: string, focused = false): Promise<boolean> {
  if (!focused) await flushSettings(scope, sessionId);
  const ok = focused || await focusScene(scope, sessionId, sceneId);
  if (ok) followSelection(VIDEO_PANEL, sessionId, sceneDraftKey(sceneId), 'scene');
  return ok;
}

// Строка «Фильм собран»: панель следует на вкладку «Фильм»
export async function selectFilmByHuman(scope: string, sessionId: string, path: string, focused = false): Promise<boolean> {
  const ok = focused || await focusFilm(scope, sessionId, path);
  if (ok) followSelection(VIDEO_PANEL, sessionId, filmTarget(path), 'film');
  return ok;
}

// Кнопки и ссылки («Переснять», «Открыть в панели») — явная просьба: открывают и закрытую панель
export function openScenePanel(sessionId: string | null) {
  revealWorkspacePanel(VIDEO_PANEL, 'scene', sessionId ? { sessionId } : {});
}

export function openFilmPanel(sessionId: string | null, path?: string) {
  revealWorkspacePanel(VIDEO_PANEL, 'film', { ...(sessionId ? { sessionId } : {}), ...(path ? { target: path } : {}) });
}

// Ярлык «Видео» (меню полос, «＋» композера): полоса и панель на «Сцене»
export function openVideoShortcut(sessionId: string | null) {
  if (sessionId) requestStrip(sessionId, VIDEO_STRIP);
  openScenePanel(sessionId);
}

// ── Съёмка ──

export interface RunInput { scope: string; sessionId: string; scene: VideoScene; r: ResolvedScene; quote: VideoQuote }

// Запуск строго по котировке: настройки уходят в сцену ДО запуска (сервер берёт текст и кадры из нити).
// Просьба из поля ввода добавляется к тексту сцены строкой (в запуске для неё поля нет)
// Съёмка: код local_unavailable_personal — человеку понятная причина, а не общий текст бэкенда
const runErrorText = (e: unknown) =>
  errorCode(e) === ERR.localPersonal ? PERSONAL_LOCAL_REASON : errorText(e, 'Съёмка не запустилась');

export async function runScene(i: RunInput, extraText = ''): Promise<boolean> {
  const { scope, sessionId, scene, quote } = i;
  if (extraText.trim()) {
    const base = i.r.text.trim();
    changeSettings(scope, sessionId, { text: base ? `${base}\n${extraText.trim()}` : extraText.trim() });
  }
  if (!await flushSettings(scope, sessionId)) return false;
  setFailure(sessionId, null);
  try {
    await videoApi.startJob(scope, sessionId, { quoteId: quote.quoteId, sessionId, sceneId: scene.sceneId });
    clearGenDraft(sceneDraftKey(scene.sceneId));
    return true;
  } catch (e) {
    setFailure(sessionId, { sceneId: scene.sceneId, text: runErrorText(e), retry: retryOf(e) });
    if (errorCode(e) === ERR.quoteNotFound) showToast('Цена устарела — она пересчитана, нажмите ещё раз', '', 'info');
    return false;
  }
}

export async function stopJob(scope: string, sessionId: string, jobId: string) {
  try { await videoApi.cancelJob(scope, sessionId, jobId); } catch (e) { showToast(errorText(e), '', 'error'); }
}

// «Продолжить от версии»
export const takeVersion = (scope: string, sessionId: string, sceneId: string, versionId: string) =>
  mutate(scope, sessionId, rev => videoApi.current(scope, sessionId, sceneId, versionId, rev), true);

// «Сохранить сцену» в проект (блок 2 бэкенда)
export async function saveScene(scope: string, sessionId: string, scene: VideoScene, versionId: string | undefined): Promise<SaveSceneResult | null> {
  try {
    const res = await videoApi.save(scope, sessionId, scene.sceneId, { ...(versionId ? { versionId } : {}), ...(scene.folder ? { folder: scene.folder } : {}) });
    showToast(`Сохранено: ${res.path}`, '', 'info');
    return res;
  } catch (e) {
    showToast(errorText(e, 'Не удалось сохранить сцену'), '', 'error');
    return null;
  }
}

export function downloadClip(scope: string, sessionId: string, scene: VideoScene, versionId: string) {
  const a = document.createElement('a');
  a.href = videoApi.versionFileUrl(scope, sessionId, scene.sceneId, versionId, true);
  a.download = '';
  document.body.appendChild(a);
  a.click();
  a.remove();
}

// ── Кадры и «Картинки» ──

export const framesFolder = (scene: VideoScene | null) => `${scene?.folder || 'video'}/кадры`;

// Причина отказа загрузки кадра: 413 — слишком большой, 400 invalid_request — не картинка
export function uploadErrorText(e: unknown): string {
  const err = e as { status?: unknown } | null;
  if (err?.status === 413) return 'Файл больше 20 МБ';
  if (err?.status === 400 && errorCode(e) === 'invalid_request') return 'Это не картинка';
  return errorText(e, 'Не удалось загрузить кадр');
}

// Файл с компьютера. Проектный чат: картинка проекта в video/…/кадры/, кадром становится файл проекта.
// Личный чат: файла проекта нет — сервер кладёт файл в рабочую папку чата и отдаёт готовый FrameRef
export async function uploadFrame(scope: string, sessionId: string | null, scene: VideoScene | null, file: File): Promise<FrameRef | null> {
  try {
    if (isPersonalScope(scope)) {
      if (!sessionId) return null;
      return await videoApi.uploadFrame(sessionId, file);
    }
    const dir = framesFolder(scene);
    await api.files.upload(scope, file, dir);
    return { kind: 'file', path: `${dir}/${file.name}` };
  } catch (e) {
    showToast(uploadErrorText(e), '', 'error');
    return null;
  }
}

async function ensureScene(scope: string, sessionId: string): Promise<VideoScene | null> {
  await flushSettings(scope, sessionId);
  let scene = getFocusedScene(sessionId);
  if (!scene && await createScene(scope, sessionId)) scene = getFocusedScene(sessionId);
  return scene;
}

function toImages(sessionId: string, scene: VideoScene, threadId: string, slot: 'A' | 'B') {
  bindFrame(sessionId, { sceneId: scene.sceneId, slot, threadId });
  revealWorkspacePanel('images', 'settings', {
    sessionId, preset: { thread: threadId },
    returnTo: { key: VIDEO_PANEL, tab: 'scene', target: scene.sceneId, label: `К сцене «${scene.name}» — панель «Видео»` },
  });
}

// «Нарисовать в «Картинках»»: черновик картинки заводится, панель «Картинки» открывается с возвратом;
// первая готовая версия сама станет кадром
export async function drawInImages(scope: string, sessionId: string, slot: 'A' | 'B'): Promise<void> {
  const scene = await ensureScene(scope, sessionId);
  if (!scene) return;
  try {
    const id = await createImageThread(scope, sessionId, { draftFolder: framesFolder(scene) });
    if (!id) { showToast('Не удалось завести картинку', '', 'error'); return; }
    toImages(sessionId, scene, id, slot);
  } catch (e) {
    showToast(errorText(e, 'Не удалось завести картинку'), '', 'error');
  }
}

// «Править кадр»: нить кадра — в «Картинки»; кадр-файл берётся в работу и после правки сам встаёт кадром
export async function editFrame(scope: string, sessionId: string, scene: VideoScene, slot: 'A' | 'B'): Promise<void> {
  const f = slot === 'A' ? scene.settings.frameA : scene.settings.frameB;
  if (!f) return;
  try {
    const id = f.kind === 'image' ? f.threadId : await createImageThread(scope, sessionId, { file: f.path });
    if (!id) { showToast('Не удалось открыть кадр в «Картинках»', '', 'error'); return; }
    toImages(sessionId, scene, id, slot);
  } catch (e) {
    showToast(errorText(e, 'Не удалось открыть кадр в «Картинках»'), '', 'error');
  }
}

let _wired = false;
// Нить, нарисованная для кадра, получила картинку — она становится кадром (и следует за новыми версиями)
export function wireFrameBinding() {
  if (_wired) return;
  _wired = true;
  onFrameReady((sessionId, b: FrameBinding, versionId) => {
    const scope = getScopeOf(sessionId);
    if (!scope) return;
    const frame: FrameRef = { kind: 'image', threadId: b.threadId, versionId, follow: true };
    void (async () => {
      if (getFocusedScene(sessionId)?.sceneId !== b.sceneId) await focusScene(scope, sessionId, b.sceneId);
      changeSettings(scope, sessionId, b.slot === 'A' ? { frameA: frame } : { frameB: frame });
    })();
  });
}
