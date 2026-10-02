// Клиент REST модуля «Звук» (ADR-021 §2): ручки AudioEditorController (проект,
// /projects/{projectId}/audio-editor) и PersonalAudioEditorController (личный чат,
// /audio-editor/chats/{sessionId}), плюс SignalR-события audio_*. Типы — зеркало
// записей бэкенда (Controllers/AudioEditorDtos.cs, Jobs/AudioEditContracts.cs,
// Threads/AudioThread.cs); enum'ы приходят строками в camelCase, кроме статуса задачи
// и инициатора — у них свой конвертер без политики имён («Running», «Human»).

import { onMessage, readStoredToken, request } from 'aihome_shell/kit';
import { audioBase, chatBase, isPersonalScope } from './scope';

export type AudioMode = 'voice' | 'music' | 'process';

export type AudioOp =
  | 'speak' | 'designVoice' | 'cloneVoice' | 'convertVoice' | 'trainVoice' | 'dialogue'
  | 'song' | 'cover' | 'repaint' | 'outpaint' | 'extract' | 'lego' | 'complete' | 'sfx'
  | 'separate' | 'denoise' | 'upsample' | 'master' | 'transcribe' | 'toMidi' | 'align'
  | 'trim' | 'gainFade' | 'normalize' | 'mixStems' | 'concat';

export type AudioVoiceKind = 'preset' | 'description' | 'clone' | 'element' | 'rvc';
export type AudioOutcome = 'ok' | 'failed' | 'insufficientCredits' | 'rejected' | 'cancelled' | 'unavailable';
export type AudioStage = 'queued' | 'running' | 'downloading';
export type AudioLicenseKind = 'permissive' | 'nonCommercial' | 'copyleft' | 'watermark' | 'unknown';
export type AudioJobStatus = 'Queued' | 'Running' | 'Downloading' | 'Completed' | 'Failed' | 'Cancelled';
export type AudioInitiator = 'Human' | 'Agent';

// ── Каталог ──

export type AudioStemSet = 'vocals' | '4' | '6' | 'karaoke';

export interface AudioCaps {
  ops: AudioOp[];
  languages: string[];
  voiceKinds: AudioVoiceKind[];
  producesFiles: string[];
  license: { label: string; kind: AudioLicenseKind };
  priceUnit: string;
  maxTextChars?: number | null;
  minDurationSec?: number | null;
  maxDurationSec?: number | null;
  inputMaxSec?: number | null;
  languageNeutral?: boolean;
  heavyOps?: AudioOp[] | null;
  /** Что разделяет модель операции separate; у остальных моделей нет */
  stemSet?: AudioStemSet | null;
}

export interface AudioPriceHint { amount: number; unit: string; per: string }

export interface AudioModelInfo { id: string; label: string; caps: AudioCaps; priceHint?: AudioPriceHint | null }

export interface AudioProvider {
  key: string;
  label: string;
  priceUnit: string;
  // Недоступный остаётся в списке серым с причиной
  available: boolean;
  reason: string | null;
  models: AudioModelInfo[];
}

// autoProviders — порядок перебора «Авто», как его считает сервер (с флагом local-media-default локальные
// первыми); нет поля — старый сервер, берём порядок списка
export interface AudioCatalog { providers: AudioProvider[]; autoModelId: string; maxCount: number; autoProviders?: string[] }

// ── Префы и настройки нити ──

// Выбор человека для режима области; null у поля — режим его не задаёт
export interface AudioModePrefs {
  operation: AudioOp | null;
  provider: string | null;
  model: string | null;
  count: number | null;
  fields: Record<string, unknown> | null;
  // Входы операции (AudioOpInputs бэкенда); в params не уходят
  inputs?: AudioOpInputs | null;
}

export interface AudioPrefs { voice: AudioModePrefs | null; music: AudioModePrefs | null; process: AudioModePrefs | null }

// Последние настройки нити — старше префов режима (решение 2026-10-01: настройки на каждый файл)
export interface AudioThreadSettings {
  mode: AudioMode;
  operation: AudioOp | null;
  provider: string | null;
  model: string | null;
  fields: Record<string, unknown> | null;
  count?: number | null;
  // Входы операции. PUT заменяет их целиком: не переданы — сервер их стирает
  inputs?: AudioOpInputs | null;
}

// Входы операции в настройках нити и префах (белый список AudioOpInputs бэкенда): их только
// подставляет панель, запуск их не читает. Пути — от корня проекта; у личного чата путей нет
export interface AudioOpPieceRef { threadId?: string; versionId?: string; projectFile?: string }

export interface AudioOpInputs {
  language?: string;
  referencePath?: string;
  startSec?: number;
  endSec?: number;
  // voice:<slug> из библиотеки «Голоса»
  voice?: string;
  pieces?: AudioOpPieceRef[];
  joint?: AudioJoint;
  joints?: (AudioJoint | null)[];
  dialogue?: { text: string; voice?: string }[];
}

// ── Нити ──

export interface AudioVersionFile { role: string; path: string }

export interface AudioThreadVersion {
  // 'origin' у исходника
  id: string;
  number: number;
  jobId: string | null;
  variant: number | null;
  baseVersionId: string | null;
  files: AudioVersionFile[];
  license: string | null;
  createdAt: string;
}

export type AudioLaunchStatus = 'running' | 'done' | 'failed' | 'cancelled' | 'interrupted';

export interface AudioThreadLaunch {
  jobId: string;
  baseVersionId: string | null;
  at: string;
  status: AudioLaunchStatus;
  initiator: 'human' | 'agent';
  prompt: string | null;
  license: string | null;
}

export interface AudioThread {
  id: string;
  // Путь от корня проекта; null — черновик «Новый звук»
  file: string | null;
  lineage: string[];
  draftFolder: string | null;
  createdAt: string;
  versions: AudioThreadVersion[];
  currentVersionId: string | null;
  launches: AudioThreadLaunch[];
  settings: AudioThreadSettings | null;
  name?: string | null;
}

export interface AudioThreadEvent { at: string; kind: string; text: string; threadId?: string | null; jobId?: string | null }

export interface AudioThreadsState {
  focus: string | null;
  revision: number;
  threads: AudioThread[];
  events?: AudioThreadEvent[];
}

export const EMPTY_THREADS: AudioThreadsState = { focus: null, revision: 0, threads: [] };

export interface AudioState { threads: AudioThreadsState; catalog: AudioCatalog; prefs: AudioPrefs }

// ── Котировка и задачи ──

export interface AudioQuoteRequest {
  mode: AudioMode;
  operation?: AudioOp | null;
  provider?: string | null;
  model?: string | null;
  count?: number | null;
  voiceKind?: AudioVoiceKind | null;
  sessionId?: string | null;
  threadId?: string | null;
  // text, prompt, lyrics, durationSec — то, от чего зависит цена: запуск обязан прийти с теми же
  text?: string | null;
  prompt?: string | null;
  lyrics?: string | null;
  durationSec?: number | null;
  fields?: Record<string, unknown> | null;
}

export interface AudioPrice {
  amount: number | null;
  unit: string;
  approx: boolean;
  source: string;
  eta: number | null;
  queueLength: number | null;
}

export interface AudioQuote {
  quoteId: string;
  mode: AudioMode;
  op: AudioOp;
  provider: string;
  model: string;
  count: number;
  voiceKind: AudioVoiceKind | null;
  price: AudioPrice;
  license: string;
  heavy: boolean;
  expiresAt: string;
  // Котировка пересоздания клона: slug голоса
  recreateVoice?: string | null;
}

export interface AudioJobInput {
  quoteId: string;
  sessionId?: string | null;
  threadId?: string | null;
  baseVersionId?: string | null;
  text?: string | null;
  prompt?: string | null;
  lyrics?: string | null;
  language?: string | null;
  durationSec?: number | null;
  startSec?: number | null;
  endSec?: number | null;
  params?: Record<string, unknown> | null;
  seed?: number | null;
  reference?: File | null;
  referencePath?: string | null;
  clips?: File[];
  clipPaths?: string[];
  voiceModelPath?: string | null;
  voiceIndexPath?: string | null;
  // Голос из библиотеки значением voice:<slug>
  voice?: string | null;
}

export interface AudioCost { amount: number; unit: string }

export interface AudioJob {
  jobId: string;
  scopeKey: string;
  status: AudioJobStatus;
  provider: string;
  model: string;
  op: AudioOp;
  count: number;
  variants: { variant: number; files: AudioVersionFile[] }[];
  cost: AudioCost | null;
  outcome: AudioOutcome | null;
  charged: boolean | null;
  error: string | null;
  queuePosition: number | null;
  etaSeconds: number | null;
  createdAt: string;
  chatSessionId: string | null;
  threadId: string | null;
  initiator: AudioInitiator;
  license: string;
}

// ── Схема «Дополнительно» (Contracts/AudioParamSchema.cs) ──

export type AudioParamType = 'number' | 'integer' | 'boolean' | 'string' | 'array' | 'object' | 'any';

export interface AudioParamField {
  key: string;
  type: AudioParamType;
  title?: string | null;
  description?: string | null;
  default?: unknown;
  min?: number | null;
  max?: number | null;
  maxLength?: number | null;
  enum?: (string | number)[] | null;
  items?: AudioParamField | null;
  fields?: AudioParamField[] | null;
  nullable?: boolean;
  format?: string | null;
  // false — движок параметр знает, но шов поставщика его пока не принимает
  passed?: boolean;
  notPassed?: string | null;
}

export interface AudioParamSchema {
  provider: string;
  model: string;
  source: string;
  fields: AudioParamField[];
  reserved: string[];
  stale?: boolean;
}

// ── Правка без ИИ и склейка (Controllers/AudioEditorDtos.cs) ──

export type AudioFileFormat = 'wav' | 'mp3' | 'flac' | 'ogg';

export interface AudioDspEditRequest {
  op: 'trim' | 'gainFade' | 'normalize' | 'convert';
  baseVersionId?: string | null;
  startSec?: number | null;
  endSec?: number | null;
  fadeInSec?: number | null;
  fadeOutSec?: number | null;
  gainDb?: number | null;
  targetLufs?: number | null;
  format?: AudioFileFormat | null;
  revision?: number | null;
}

export interface AudioDspVersion { threadId: string; versionId: string; number: number; jobId: string; state: AudioThreadsState }

export type AudioJointKind = 'butt' | 'pause' | 'crossfade';
export interface AudioJoint { kind: AudioJointKind; seconds: number }
export interface AudioConcatPiece { threadId?: string | null; versionId?: string | null; projectFile?: string | null }

export interface AudioConcatRequest {
  pieces: AudioConcatPiece[];
  joint?: AudioJoint | null;
  joints?: (AudioJoint | null)[] | null;
  normalizeLoudness?: boolean;
  name?: string | null;
  format?: AudioFileFormat | null;
}

export interface AudioConcatResult { threadId: string; versionId: string; jobId: string; name: string }

export interface AudioSaveRequest { versionId?: string | null; mode?: 'nextVersion' | 'as'; folder?: string | null; fileName?: string | null }
// path — главный файл, files — все записанные (стемы, субтитры) от корня проекта
export interface AudioSaveResult { path: string; files: string[] }

// ── Без ИИ: пики волны и сведение стемов (Engines/DspAudioEngine.cs) ──

// Пики 0…1 по точкам и длина файла, с
export interface AudioPeaks { peaks: number[]; seconds: number }

// role — «stem:<имя>»; muted — в сведение не идёт
export interface AudioMixStem { role: string; gainDb: number; muted?: boolean }

export interface AudioMixRequest { stems: AudioMixStem[]; baseVersionId?: string | null; format?: string | null; revision?: number | null }

export interface AudioDspVersion { threadId: string; versionId: string; number: number; jobId: string; state: AudioThreadsState }

// ── События SignalR (Jobs/AudioEditEvents.cs); базовый sessionId — чат события ──

interface AudioEventBase { sessionId: string; scopeKey: string }

export interface AudioProgressEvent extends AudioEventBase {
  type: 'audio_edit_progress';
  jobId: string;
  stage: AudioStage;
  queuePosition: number | null;
  etaSeconds: number | null;
  variant: number;
  count: number;
  chatSessionId: string | null;
  threadId: string | null;
  initiator: AudioInitiator;
}

export interface AudioCompletedEvent extends AudioEventBase {
  type: 'audio_edit_completed';
  jobId: string;
  variants: number[];
  cost: AudioCost | null;
  error: string | null;
  chatSessionId: string | null;
  threadId: string | null;
  initiator: AudioInitiator;
}

export interface AudioFailedEvent extends AudioEventBase {
  type: 'audio_edit_failed';
  jobId: string;
  outcome: AudioOutcome;
  charged: boolean | null;
  error: string | null;
  retryQuote: AudioQuote | null;
  chatSessionId: string | null;
  threadId: string | null;
  initiator: AudioInitiator;
}

export interface AudioThreadChangedEvent extends AudioEventBase {
  type: 'audio_thread_changed';
  revision: number;
  state: AudioThreadsState;
}

export interface AudioPrefsChangedEvent extends AudioEventBase {
  type: 'audio_prefs_changed';
  mode: AudioMode;
  prefs: AudioModePrefs;
}

export type AudioEvent =
  | AudioProgressEvent | AudioCompletedEvent | AudioFailedEvent | AudioThreadChangedEvent | AudioPrefsChangedEvent;

export const AUDIO_EVENTS: ReadonlySet<string> = new Set([
  'audio_edit_progress', 'audio_edit_completed', 'audio_edit_failed', 'audio_thread_changed', 'audio_prefs_changed',
]);

// ── Запросы ──

const json = <T>(url: string, body: unknown, method = 'POST', timeoutMs?: number) =>
  request<T>(url, { method, body: JSON.stringify(body), ...(timeoutMs ? { timeoutMs } : {}) });

const threadUrl = (scope: string, sessionId: string, threadId: string) =>
  `${chatBase(scope, sessionId)}/threads/${encodeURIComponent(threadId)}`;

export function jobForm(input: AudioJobInput): FormData {
  const form = new FormData();
  const put = (key: string, v: string | number | null | undefined) => {
    if (v !== null && v !== undefined && v !== '') form.append(key, String(v));
  };
  put('quoteId', input.quoteId);
  put('sessionId', input.sessionId);
  put('threadId', input.threadId);
  put('baseVersionId', input.baseVersionId);
  put('text', input.text);
  put('prompt', input.prompt);
  put('lyrics', input.lyrics);
  put('language', input.language);
  put('durationSec', input.durationSec);
  put('startSec', input.startSec);
  put('endSec', input.endSec);
  put('seed', input.seed);
  if (input.params) form.append('params', JSON.stringify(input.params));
  if (input.reference) form.append('reference', input.reference);
  put('referencePath', input.referencePath);
  input.clips?.forEach(f => form.append('clips', f));
  input.clipPaths?.forEach(p => form.append('clipPaths', p));
  put('voiceModelPath', input.voiceModelPath);
  put('voiceIndexPath', input.voiceIndexPath);
  put('voice', input.voice);
  return form;
}

// scope — ключ области (audioScope), sessionId — чат: личной области он нужен в каждом маршруте
export const audioApi = {
  state: (scope: string, sessionId: string) =>
    request<AudioState>(`${chatBase(scope, sessionId)}/state`, { live: true }),
  catalog: (scope: string, sessionId: string | null) =>
    request<AudioCatalog>(`${audioBase(scope, sessionId)}/catalog`, { live: true }),
  prefs: (scope: string, sessionId: string | null) =>
    request<AudioPrefs>(`${audioBase(scope, sessionId)}/prefs`, { live: true }),
  putPrefs: (scope: string, sessionId: string | null, mode: AudioMode, prefs: AudioModePrefs) =>
    json<AudioPrefs>(`${audioBase(scope, sessionId)}/prefs/${mode}`, prefs, 'PUT'),
  quote: (scope: string, sessionId: string | null, req: AudioQuoteRequest) =>
    json<AudioQuote>(`${audioBase(scope, sessionId)}/quote`, req, 'POST', 60_000),
  startJob: (scope: string, sessionId: string | null, input: AudioJobInput) =>
    request<{ jobId: string }>(`${audioBase(scope, sessionId)}/jobs`, { method: 'POST', body: jobForm(input), timeoutMs: 300_000 }),
  getJob: (scope: string, sessionId: string | null, jobId: string) =>
    request<AudioJob>(`${audioBase(scope, sessionId)}/jobs/${encodeURIComponent(jobId)}`, { live: true }),
  cancelJob: (scope: string, sessionId: string | null, jobId: string) =>
    request<AudioJob>(`${audioBase(scope, sessionId)}/jobs/${encodeURIComponent(jobId)}`, { method: 'DELETE' }),

  threads: (scope: string, sessionId: string) =>
    request<AudioThreadsState>(`${chatBase(scope, sessionId)}/threads`, { live: true }),
  // Ровно одно из file и draftFolder; mode — режим новой нити: её настройки — копия префов режима
  open: (scope: string, sessionId: string, body: { file?: string; draftFolder?: string; mode?: AudioMode; revision: number }) =>
    json<AudioThreadsState>(`${chatBase(scope, sessionId)}/threads`, body),
  focus: (scope: string, sessionId: string, threadId: string | null, revision: number) =>
    json<AudioThreadsState>(`${chatBase(scope, sessionId)}/threads/focus`, { threadId, revision }, 'PUT'),
  remove: (scope: string, sessionId: string, threadId: string, revision: number) =>
    request<AudioThreadsState>(`${threadUrl(scope, sessionId, threadId)}?revision=${revision}`, { method: 'DELETE' }),
  settings: (scope: string, sessionId: string, threadId: string, settings: AudioThreadSettings, revision: number) =>
    json<AudioThreadsState>(`${threadUrl(scope, sessionId, threadId)}/settings`, { settings, revision }, 'PUT'),
  current: (scope: string, sessionId: string, threadId: string, versionId: string, revision: number) =>
    json<AudioThreadsState>(`${threadUrl(scope, sessionId, threadId)}/current`, { versionId, revision }, 'PUT'),
  // Схема «Дополнительно»: ручка вне области, id модели fal содержит «/» — всё в query
  schema: (provider: string, model: string, op: AudioOp) =>
    request<AudioParamSchema>(`/audio-editor/schema?${new URLSearchParams({ provider, model, op })}`, { live: true }),
  edit: (scope: string, sessionId: string, threadId: string, req: AudioDspEditRequest) =>
    json<AudioDspVersion>(`${threadUrl(scope, sessionId, threadId)}/edit`, req, 'POST', 120_000),
  concat: (scope: string, sessionId: string, req: AudioConcatRequest) =>
    json<AudioConcatResult>(`${chatBase(scope, sessionId)}/concat`, req, 'POST', 300_000),
  // Сохранить в проект — только у проекта: у личного чата ручки нет, отказ до запроса
  save: (scope: string, sessionId: string, threadId: string, req: AudioSaveRequest) => {
    if (isPersonalScope(scope)) throw new Error('У личного чата нет проекта — версию можно только скачать');
    return json<AudioSaveResult>(`${threadUrl(scope, sessionId, threadId)}/save`, req);
  },
  // URL файла версии для <audio src>: токен через ?access_token=, тег заголовков не шлёт
  versionFileUrl: (scope: string, sessionId: string, threadId: string, versionId: string, role = 'main', download = false) => {
    const url = `/api${threadUrl(scope, sessionId, threadId)}/versions/${encodeURIComponent(versionId)}/files/${encodeURIComponent(role)}`;
    const q = new URLSearchParams();
    if (download) q.set('download', 'true');
    const token = readStoredToken();
    if (token) q.set('access_token', token);
    const qs = q.toString();
    return qs ? `${url}?${qs}` : url;
  },
  // role null — главный файл
  peaks: (scope: string, sessionId: string, threadId: string, versionId: string, points: number, role: string | null = null) => {
    const q = new URLSearchParams({ points: String(points) });
    if (role) q.set('role', role);
    return request<AudioPeaks>(`${threadUrl(scope, sessionId, threadId)}/versions/${encodeURIComponent(versionId)}/peaks?${q}`, { timeoutMs: 60_000 });
  },
  // Ждёт итог прямо в запросе: ffmpeg на хосте, без очереди
  mix: (scope: string, sessionId: string, threadId: string, req: AudioMixRequest) =>
    json<AudioDspVersion>(`${threadUrl(scope, sessionId, threadId)}/mix`, req, 'POST', 300_000),

  subscribe: (handler: (e: AudioEvent) => void) => onMessage(msg => {
    const m = msg as unknown as { type?: string };
    if (m.type && AUDIO_EVENTS.has(m.type)) handler(m as unknown as AudioEvent);
  }),
};

// Свободное имя из тела 409 name_taken у «Сохранить как…»; другой отказ — null
export function nameTakenSuggestion(e: unknown): string | null {
  const err = e as { status?: unknown; body?: { code?: unknown; suggestion?: unknown } } | null;
  if (err?.status !== 409 || err.body?.code !== 'name_taken') return null;
  return typeof err.body.suggestion === 'string' && err.body.suggestion ? err.body.suggestion : null;
}

// Актуальное состояние из тела 409 revision_conflict — перечитывать не нужно
export function conflictState(e: unknown): AudioThreadsState | null {
  const err = e as { status?: unknown; body?: { state?: unknown } } | null;
  if (err?.status !== 409) return null;
  const st = err.body?.state as AudioThreadsState | undefined;
  return st && Array.isArray(st.threads) ? st : null;
}

export const CLONE_REFUSAL_CODES: ReadonlySet<string> = new Set(['voice_clone_stale', 'voice_clone_missing']);

// Отказ запуска «клон MiniMax протух или не создан» (409): текст и котировка пересоздания; другой — null
export function cloneRefusal(e: unknown): { code: string; message: string; recreate: AudioQuote | null } | null {
  const err = e as { status?: unknown; message?: unknown; body?: { code?: unknown; error?: unknown; recreate?: unknown } } | null;
  const code = err?.body?.code;
  if (err?.status !== 409 || typeof code !== 'string' || !CLONE_REFUSAL_CODES.has(code)) return null;
  const message = typeof err.body?.error === 'string' ? err.body.error : typeof err.message === 'string' ? err.message : 'Клон голоса недоступен';
  const r = err.body?.recreate as AudioQuote | undefined;
  return { code, message, recreate: r && typeof r.quoteId === 'string' ? r : null };
}
