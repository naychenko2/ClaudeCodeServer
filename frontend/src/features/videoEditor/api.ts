// Клиент REST модуля «Видео» (ADR-022, раздел «Контракты»): ручки VideoEditorController (проект,
// /projects/{projectId}/video-editor) и PersonalVideoEditorController (личный чат,
// /video-editor/chats/{sessionId}), плюс SignalR-события video_*. Типы — зеркало записей
// бэкенда (Contracts/*.cs): camelCase, даты — ISO UTC, null не выводится (поля необязательные),
// статусы — строки в нижнем регистре.
//
// Ручки фильма и «Сохранить сцену» (блок 2 бэкенда) описаны здесь же по контракту, но на момент
// написания живого сервера у них нет: фронт ходит в них через videoApi, а тесты и e2e — на моках.

import { onMessage, readStoredToken, request } from 'aihome_shell/kit';
import { chatBase, isPersonalScope, PERSONAL_SCOPE, videoBase } from './scope';

// ── Кадры и настройки сцены ──

export type FrameRef =
  // initiator — кто создал версию, на которую кадр перешёл (нет у старых данных)
  | { kind: 'image'; threadId: string; versionId: string; follow?: boolean; initiator?: 'human' | 'agent' }
  // fileName — человеческое имя загруженного «С компьютера» файла: подпись вместо пути рабочей папки
  | { kind: 'file'; path: string; fileName?: string };

export interface VideoSceneSettings {
  frameA?: FrameRef;
  frameB?: FrameRef;
  text: string;
  provider?: string;
  model?: string;
  durationSec?: number;
  aspect?: string;
  sound?: boolean;
  count?: number;
}

export interface VideoCost { currency: 'usd' | 'credits' | 'local'; amount: number }

export interface VideoInputsSnapshot { text: string; frameA?: string; frameB?: string }

export type VideoInitiator = 'human' | 'agent';

export interface VideoClipVersion {
  versionId: string;
  number: number;
  jobId: string;
  variant: number;
  provider: string;
  model: string;
  durationSec: number;
  sizeBytes: number;
  hasSound: boolean;
  license?: string;
  cost?: VideoCost;
  initiator: VideoInitiator;
  inputs: VideoInputsSnapshot;
  createdAt: string;
}

export type VideoLaunchStatus = 'running' | 'done' | 'failed' | 'cancelled' | 'interrupted';

export interface VideoLaunch {
  jobId: string;
  at: string;
  status: VideoLaunchStatus;
  interrupted: boolean;
  initiator: VideoInitiator;
  provider: string;
  model: string;
  count: number;
  prompt?: string;
  license?: string;
  error?: string;
}

export interface VideoSceneStale { text: boolean; frameA: boolean; frameB: boolean; versionId?: string }
export interface VideoSavedFile { versionId: string; path: string }
export interface VideoFilmRef { path: string; position: number }

export interface VideoScene {
  sceneId: string;
  name: string;
  folder: string;
  settings: VideoSceneSettings;
  versions: VideoClipVersion[];
  currentVersionId?: string;
  launches: VideoLaunch[];
  stale?: VideoSceneStale;
  savedFiles: VideoSavedFile[];
  filmRef?: VideoFilmRef;
  createdAt: string;
}

export interface VideoFocus { sceneId?: string; filmPath?: string }

export interface VideoThreadsState { focus: VideoFocus; revision: number; scenes: VideoScene[] }

export const EMPTY_THREADS: VideoThreadsState = { focus: {}, revision: 0, scenes: [] };

// ── Каталог и префы ──

export interface VideoModelInfo {
  id: string;
  label: string;
  durations: number[];
  aspects: string[];
  sound: boolean;
  lastFrame: boolean;
  license?: string;
}

export interface VideoProvider {
  key: string;
  label: string;
  priceUnit: string;
  // Недоступный остаётся в списке серым с причиной
  available: boolean;
  reason?: string;
  models: VideoModelInfo[];
}

export interface VideoCatalog { providers: VideoProvider[]; autoModelId: string; maxCount: number; autoProviders: string[] }

export interface VideoPrefs {
  provider?: string;
  model?: string;
  durationSec?: number;
  aspect?: string;
  sound?: boolean;
  count?: number;
}

export interface VideoState { threads: VideoThreadsState; catalog: VideoCatalog; prefs: VideoPrefs }

// ── Котировка и задачи ──

export interface VideoQuoteRequest {
  sessionId: string;
  sceneId: string;
  provider?: string;
  model?: string;
  count?: number;
  durationSec?: number;
  aspect?: string;
  sound?: boolean;
}

export interface VideoPrice {
  amount?: number;
  unit: 'usd' | 'credits' | 'free';
  approx: boolean;
  source: string;
  eta?: number;
  queueLength?: number;
}

export interface VideoQuote {
  quoteId: string;
  provider: string;
  model: string;
  count: number;
  durationSec: number;
  price: VideoPrice;
  license: string;
  heavy: boolean;
  expiresAt: string;
}

export interface RetryQuote { provider: string; model: string; quote: VideoQuote; reason: string }

export interface VideoLaunchRequest { quoteId: string; sessionId: string; sceneId: string; params?: Record<string, unknown>; seed?: number }

export interface VideoLaunchResult { jobId?: string; errorCode?: string; error?: string; retry?: RetryQuote }

export type VideoJobStatus = 'queued' | 'running' | 'downloading' | 'completed' | 'failed' | 'cancelled';

export interface VideoJob {
  jobId: string;
  scopeKey: string;
  status: VideoJobStatus;
  provider: string;
  model: string;
  count: number;
  variants: number[];
  cost?: VideoCost;
  outcome?: string;
  charged?: boolean;
  error?: string;
  queuePosition?: number;
  etaSeconds?: number;
  createdAt: string;
  chatSessionId?: string;
  sceneId?: string;
  initiator: VideoInitiator;
  license: string;
}

// ── Фильм (блок 2) ──

export type FilmCutType = 'butt' | 'dissolve' | 'fade';

export interface FilmSceneSnapshot {
  text: string;
  frameA?: string;
  frameB?: string;
  provider?: string;
  model?: string;
  durationSec?: number;
}

export interface FilmItem { file: string; trim: [number, number]; scene?: FilmSceneSnapshot }
export interface FilmCut { type: FilmCutType; sec: number }
export interface FilmMusic { file: string; volume: number; fadeOut: number }
export interface FilmBuild { file: string; sourceHash: string; at: string }

export interface FilmDocument {
  schema: number;
  aspect: string;
  items: FilmItem[];
  cuts: FilmCut[];
  music?: FilmMusic;
  builds: FilmBuild[];
}

export interface FilmSummary { path: string; name: string; itemCount: number; durationSec: number; stale: boolean; valid: boolean }

export interface FilmSpent { usd: number; credits: number; gpuSeconds: number }

// «✦ Claude» (claude), «● обновлена» (updated), «переснять» (stale)
export interface FilmItemMark { index: number; claude: boolean; updated: boolean; stale: boolean }

export type FilmBuildState = 'waiting' | 'running' | 'done' | 'failed' | 'cancelled';

export interface FilmBuildStatus { state: FilmBuildState; progress: number; file?: string; error?: string; startedAt?: string }

export interface FilmState {
  path: string;
  revision: string;
  document: FilmDocument;
  spent: FilmSpent;
  marks: FilmItemMark[];
  build?: FilmBuildStatus;
  // Фильм изменён после последней сборки (или не собирался, а строки есть) — признак сервера, тот же, что у списка
  stale?: boolean;
}

export type FilmPatchOp =
  | { op: 'add'; file: string; index?: number; scene?: FilmSceneSnapshot }
  | { op: 'remove'; index: number }
  | { op: 'move'; from: number; to: number }
  | { op: 'cut'; index: number; cutType: FilmCutType; sec?: number }
  | { op: 'trim'; index: number; trim: [number, number] }
  | { op: 'music'; music: FilmMusic | null };

export interface FilmPatch { expectedRevision: string; ops: FilmPatchOp[] }

// Длина заготовки музыки: durationSec — длина фильма, actualDurationSec — что придёт на самом деле (модели музыки
// не короче minDurationSec); durationNote — причина расхождения; styleText — стиль по текстам сцен
export interface FilmMusicDraft {
  threadId: string;
  durationSec?: number;
  minDurationSec?: number;
  actualDurationSec?: number;
  durationNote?: string | null;
  styleText?: string | null;
}

export interface SaveSceneRequest { sessionId: string; sceneId: string; versionId?: string; folder?: string; filmPath?: string; fileName?: string }
export interface SaveSceneResult { path: string; framePaths: string[]; addedToFilm: boolean }

// ── События SignalR (Contracts/VideoEditorEvents.cs); базовый sessionId — чат события ──

interface VideoEventBase { sessionId: string; scopeKey: string }

export interface VideoThreadChangedEvent extends VideoEventBase { type: 'video_thread_changed'; revision: number; state: VideoThreadsState }
export interface VideoFilmChangedEvent extends VideoEventBase { type: 'video_film_changed'; path: string; state: FilmState }
export interface VideoProgressEvent extends VideoEventBase {
  type: 'video_edit_progress';
  jobId: string;
  sceneId: string;
  stage: 'queued' | 'running' | 'downloading';
  queuePosition?: number;
  etaSeconds?: number;
  variant: number;
  count: number;
  initiator: VideoInitiator;
}
export interface VideoCompletedEvent extends VideoEventBase {
  type: 'video_edit_completed';
  jobId: string;
  sceneId: string;
  variants: number[];
  cost?: VideoCost;
  error?: string;
  initiator: VideoInitiator;
}
export interface VideoFailedEvent extends VideoEventBase {
  type: 'video_edit_failed';
  jobId: string;
  sceneId: string;
  charged?: boolean;
  error?: string;
  retryQuote?: RetryQuote;
  initiator: VideoInitiator;
}

export type VideoEvent =
  | VideoThreadChangedEvent | VideoFilmChangedEvent | VideoProgressEvent | VideoCompletedEvent | VideoFailedEvent;

export const VIDEO_EVENTS: ReadonlySet<string> = new Set([
  'video_thread_changed', 'video_film_changed', 'video_edit_progress', 'video_edit_completed', 'video_edit_failed',
]);

// ── Коды ошибок (VideoEditorErrors) ──

export const ERR = {
  nameTaken: 'name_taken',
  revisionConflict: 'revision_conflict',
  dspUnavailable: 'dsp_unavailable',
  personalNoFilms: 'personal_scope_no_films',
  localPersonal: 'local_unavailable_personal',
  projectLocal: 'project_local_unsupported',
  outsideFolders: 'outside_allowed_folders',
  filmInvalid: 'film_invalid',
  filmSchema: 'film_schema_unsupported',
  providerUnavailable: 'provider_unavailable',
  quoteNotFound: 'quote_not_found',
  tooManyJobs: 'too_many_jobs',
  heavyBusy: 'heavy_busy',
  frameUnavailable: 'frame_unavailable',
} as const;

// recordType карточек ленты (module_record, module «videoeditor»)
export const RECORD = {
  scene: 'video_scene',
  launchVersions: 'video_launch_versions',
  saved: 'video_saved',
  filmBuilt: 'video_film_built',
  note: 'video_note',
} as const;

// ── Запросы ──

const json = <T>(url: string, body: unknown, method = 'POST', timeoutMs?: number) =>
  request<T>(url, { method, body: JSON.stringify(body), ...(timeoutMs ? { timeoutMs } : {}) });

const sceneUrl = (scope: string, sessionId: string, sceneId: string) =>
  `${chatBase(scope, sessionId)}/scenes/${encodeURIComponent(sceneId)}`;

const filmQuery = (path: string, sessionId: string) => new URLSearchParams({ path, sessionId });

const withToken = (url: string, extra: Record<string, string> = {}) => {
  const q = new URLSearchParams(extra);
  const token = readStoredToken();
  if (token) q.set('access_token', token);
  const qs = q.toString();
  return qs ? `${url}?${qs}` : url;
};

// scope — ключ области (videoScope), sessionId — чат: личной области он нужен в каждом маршруте
export const videoApi = {
  state: (scope: string, sessionId: string) =>
    request<VideoState>(`${chatBase(scope, sessionId)}/state`, { live: true }),
  catalog: (scope: string, sessionId: string | null) =>
    request<VideoCatalog>(`${videoBase(scope, sessionId)}/catalog`, { live: true }),
  prefs: (scope: string, sessionId: string | null) =>
    request<VideoPrefs>(`${videoBase(scope, sessionId)}/prefs`, { live: true }),
  putPrefs: (scope: string, sessionId: string | null, prefs: VideoPrefs) =>
    json<VideoPrefs>(`${videoBase(scope, sessionId)}/prefs`, prefs, 'PUT'),
  quote: (scope: string, sessionId: string | null, req: VideoQuoteRequest) =>
    json<VideoQuote>(`${videoBase(scope, sessionId)}/quote`, req, 'POST', 60_000),
  // 202 с jobId либо тело с errorCode и retry (409 provider_unavailable) — разбирает вызывающий
  startJob: (scope: string, sessionId: string | null, req: VideoLaunchRequest) =>
    json<VideoLaunchResult>(`${videoBase(scope, sessionId)}/jobs`, req, 'POST', 120_000),
  getJob: (scope: string, sessionId: string | null, jobId: string) =>
    request<VideoJob>(`${videoBase(scope, sessionId)}/jobs/${encodeURIComponent(jobId)}`, { live: true }),
  cancelJob: (scope: string, sessionId: string | null, jobId: string) =>
    request<VideoJob>(`${videoBase(scope, sessionId)}/jobs/${encodeURIComponent(jobId)}`, { method: 'DELETE' }),

  scenes: (scope: string, sessionId: string) =>
    request<VideoThreadsState>(`${chatBase(scope, sessionId)}/scenes`, { live: true }),
  addScene: (scope: string, sessionId: string, body: { folder?: string; settings?: VideoSceneSettings; name?: string; revision: number }) =>
    json<VideoThreadsState>(`${chatBase(scope, sessionId)}/scenes`, body),
  focus: (scope: string, sessionId: string, focus: VideoFocus, revision: number) =>
    json<VideoThreadsState>(`${chatBase(scope, sessionId)}/scenes/focus`, { focus, revision }, 'PUT'),
  removeScene: (scope: string, sessionId: string, sceneId: string, revision: number) =>
    request<VideoThreadsState>(`${sceneUrl(scope, sessionId, sceneId)}?revision=${revision}`, { method: 'DELETE' }),
  settings: (scope: string, sessionId: string, sceneId: string, settings: VideoSceneSettings, revision: number) =>
    json<VideoThreadsState>(`${sceneUrl(scope, sessionId, sceneId)}/settings`, { settings, revision }, 'PUT'),
  current: (scope: string, sessionId: string, sceneId: string, versionId: string, revision: number) =>
    json<VideoThreadsState>(`${sceneUrl(scope, sessionId, sceneId)}/current`, { versionId, revision }, 'PUT'),
  // URL клипа для <video src>: токен через ?access_token=, тег заголовков не шлёт
  versionFileUrl: (scope: string, sessionId: string, sceneId: string, versionId: string, download = false) =>
    withToken(`/api${sceneUrl(scope, sessionId, sceneId)}/versions/${encodeURIComponent(versionId)}/file`, download ? { download: 'true' } : {}),
  // Сохранить в проект — только у проекта: у личного чата ручки нет, отказ до запроса (блок 2)
  save: (scope: string, sessionId: string, sceneId: string, req: Omit<SaveSceneRequest, 'sessionId' | 'sceneId'>) => {
    if (isPersonalScope(scope)) throw new Error('У личного чата нет проекта — клип можно только скачать');
    return json<SaveSceneResult>(`${sceneUrl(scope, sessionId, sceneId)}/save`, { ...req, sessionId, sceneId });
  },

  // Фильмы — ручки проекта, не чата (FilmController); личный чат — personal_scope_no_films
  films: (scope: string, sessionId: string) =>
    request<FilmSummary[]>(`${videoBase(scope, sessionId)}/films`, { live: true }),
  filmState: (scope: string, sessionId: string, path: string) =>
    request<FilmState>(`${videoBase(scope, sessionId)}/films/state?${new URLSearchParams({ path })}`, { live: true }),
  // sessionId в query — чат-вызыватель: без него бэкенд не пишет строку ленты у человека
  patchFilm: (scope: string, sessionId: string, path: string, patch: FilmPatch) =>
    json<FilmState>(`${videoBase(scope, sessionId)}/films?${filmQuery(path, sessionId)}`, patch, 'PATCH'),
  buildFilm: (scope: string, sessionId: string, path: string) =>
    json<FilmBuildStatus>(`${videoBase(scope, sessionId)}/films/build?${filmQuery(path, sessionId)}`, {}),
  buildStatus: (scope: string, sessionId: string, path: string) =>
    request<FilmBuildStatus>(`${videoBase(scope, sessionId)}/films/build?${filmQuery(path, sessionId)}`, { live: true }),
  cancelBuild: (scope: string, sessionId: string, path: string) =>
    request<FilmBuildStatus>(`${videoBase(scope, sessionId)}/films/build?${filmQuery(path, sessionId)}`, { method: 'DELETE' }),
  // Загрузка кадра «С компьютера» в личном чате: файл ложится в рабочую папку чата, ответ — FrameRef
  uploadFrame: (sessionId: string, file: File) => {
    const form = new FormData();
    form.append('file', file);
    return request<FrameRef>(`${videoBase(PERSONAL_SCOPE, sessionId)}/frames/upload`, { method: 'POST', body: form, timeoutMs: 120_000 });
  },
  // «Новый фильм»: пустой .film, занятое имя — 409 name_taken. Только проектные
  createFilm: (scope: string, sessionId: string, path: string, aspect?: string) =>
    json<FilmState>(`${videoBase(scope, sessionId)}/films`, { path, ...(aspect ? { aspect } : {}) }),
  // Превью кадра личного чата: байты из рабочей папки (путь кадра — frames/<id>.<ext>)
  frameFileUrl: (sessionId: string, path: string) =>
    withToken(`/api${videoBase(PERSONAL_SCOPE, sessionId)}/frames/${encodeURIComponent(path.replace(/^frames\//, ''))}`),
  // «Сочинить под фильм…»: сервер заводит черновик звука в чате и ждёт его первую версию — она встанет
  // музыкой фильма сама (FilmMusicComposer), и при запуске человеком, и агентом
  composeMusic: (scope: string, sessionId: string, path: string) =>
    json<FilmMusicDraft>(`${videoBase(scope, sessionId)}/films/music?${new URLSearchParams({ path })}`, { sessionId }),

  subscribe: (handler: (e: VideoEvent) => void) => onMessage(msg => {
    const m = msg as unknown as { type?: string };
    if (m.type && VIDEO_EVENTS.has(m.type)) handler(m as unknown as VideoEvent);
  }),
};

// ── Разбор ошибок ──

interface ApiErr { status?: unknown; message?: unknown; body?: { code?: unknown; error?: unknown; state?: unknown; retry?: unknown; suggestion?: unknown } }

export const errorCode = (e: unknown): string | null => {
  const c = (e as ApiErr | null)?.body?.code;
  return typeof c === 'string' ? c : null;
};

// Актуальное состояние нитей из тела 409 revision_conflict — перечитывать не нужно
export function conflictState(e: unknown): VideoThreadsState | null {
  const err = e as ApiErr | null;
  if (err?.status !== 409) return null;
  const st = err.body?.state as VideoThreadsState | undefined;
  return st && Array.isArray(st.scenes) ? st : null;
}

// 409 revision_conflict у фильма: ревизия файла устарела, в теле — свежий FilmState (если сервер его приложил)
export function filmConflict(e: unknown): { state: FilmState | null } | null {
  const err = e as ApiErr | null;
  if (err?.status !== 409 || errorCode(e) !== ERR.revisionConflict) return null;
  const st = err.body?.state as FilmState | undefined;
  return { state: st && st.document ? st : null };
}

// Сосед при отказе поставщика (409 provider_unavailable): кнопка «Повторить через …»
export function retryOf(e: unknown): RetryQuote | null {
  const r = (e as ApiErr | null)?.body?.retry as RetryQuote | undefined;
  return r && r.quote && typeof r.quote.quoteId === 'string' ? r : null;
}

export const errorText = (e: unknown, fallback = 'Не удалось'): string => {
  const err = e as ApiErr | null;
  if (typeof err?.body?.error === 'string') return err.body.error;
  return typeof err?.message === 'string' && err.message ? err.message : fallback;
};

// Свободное имя из тела 409 name_taken
export function nameTakenSuggestion(e: unknown): string | null {
  const err = e as ApiErr | null;
  if (err?.status !== 409 || err.body?.code !== ERR.nameTaken) return null;
  return typeof err.body.suggestion === 'string' && err.body.suggestion ? err.body.suggestion : null;
}
