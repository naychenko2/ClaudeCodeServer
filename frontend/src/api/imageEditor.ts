// API редактора картинок (ADR-017). Контракт — DTO и маршруты из
// backend/ClaudeHomeServer.Core/Services/ImageEditor (волна 1), база
// api/projects/{projectId}/image-editor. Enum'ы приходят строками в camelCase.
//
// Мок-режим: пока драйверов поставщиков нет, живой сервер отдаёт пустой каталог и
// 409 на запуск. Для работы экрана без драйверов в localStorage ставится
// `cc-image-editor-mock`: `fal` — в каталоге только fal, `all` — fal и Higgsfield.
// Мок повторяет контракт целиком, включая SignalR-события задачи.

import { request, readStoredToken } from '../lib/offline';
import { onMessage } from '../lib/signalr';
import type { Session } from '../types';

export type ImageEditOp = 'generate' | 'edit' | 'inpaint' | 'outpaint' | 'removeBackground' | 'upscale';
export type EditMode = 'auto' | 'fast' | 'precise' | 'photoreal';
export type MaskSupport = 'none' | 'native' | 'asReference';
export type ReferenceRole = 'character' | 'style' | 'object';
export type EditOutcome = 'ok' | 'failed' | 'insufficientCredits' | 'rejected' | 'cancelled' | 'unavailable';
export type EditStage = 'queued' | 'running' | 'downloading';
export type PriceUnit = 'usd' | 'credits';
export type ImageEditJobStatus =
  | 'queued' | 'running' | 'downloading' | 'completed' | 'failed' | 'cancelled' | 'interrupted';

export const AUTO_MODEL = 'auto';

export interface ImageEditCaps {
  ops: ImageEditOp[];
  mask: MaskSupport;
  maxReferences: number;
  maxCount: number;
  faceByReferences: boolean;
  separateMaskPass?: boolean;
  // Лимиты входа модели для автоуменьшения (ADR-018 §9); null — лимита нет
  maxInputSide?: number | null;
  maxInputMegapixels?: number | null;
  maxInputMb?: number | null;
}

export interface ImageEditPriceHint { amount: number; unit: string; per: string }

export interface ImageEditModel {
  id: string;
  label: string;
  modes?: EditMode[] | null;
  caps?: ImageEditCaps | null;
  priceHint?: ImageEditPriceHint | null;
}

export interface ImageEditProvider {
  key: string;
  label: string;
  priceUnit: string;
  models: ImageEditModel[];
}

export interface ImageEditCatalog {
  // provider = null — ни одного доступного поставщика
  default: { provider: string | null; model: string };
  providers: ImageEditProvider[];
  limits: { maxFileMb: number; maxReferences: number; maxCount: number };
  // Почему поставщиков нет; null — есть хотя бы один
  reason: ImageEditCatalogReason | null;
}

export type ImageEditCatalogReason = 'no_provider_configured' | 'subsystem_disabled';

export interface ImageEditQuoteRequest {
  provider: string;
  model: string;
  mode: EditMode;
  op: ImageEditOp;
  count: number;
  hasMask: boolean;
  references: number;
  hasCharacter: boolean;
  width?: number | null;
  height?: number | null;
  // Стрелки, рамки, подписи: без них сервер может выбрать модель без канала образцов
  hasAnnotations?: boolean;
  // Запрос просит стереть отмеченное кистью
  removal?: boolean;
}

export interface ImageEditEstimate {
  // null — «цена станет известна после запуска»
  amount: number | null;
  unit: string;
  approx: boolean;
  source: 'provider' | 'catalog' | 'history' | 'unknown';
}

export interface ImageEditQuote {
  quoteId: string;
  provider: string;
  model: string;
  estimate: ImageEditEstimate;
  expiresAt: string;
  expectedSeconds?: number | null;
}

export interface EditCost { amount: number; unit: string }

export interface ImageEditJob {
  jobId: string;
  projectId: string;
  status: ImageEditJobStatus;
  provider: string;
  model: string;
  variants: number[];
  cost?: EditCost | null;
  outcome?: EditOutcome | null;
  charged?: boolean | null;
  error?: string | null;
  queuePosition?: number | null;
  createdAt: string;
  // Чат картинки, из которого запущена задача (ADR-018 §2); null — запуск вне чата
  chatSessionId?: string | null;
  initiator?: ImageEditInitiator;
}

// Кто запустил задачу или написал промпт: человек в редакторе или агент чата картинки
export type ImageEditInitiator = 'human' | 'agent';

export interface ImageEditJobInput {
  quoteId: string;
  prompt: string;
  marks?: string;
  sourcePath?: string;
  source?: Blob;
  mask?: Blob;
  annotated?: Blob;
  // Подключённый персонаж проекта: сервер добавит его фото образцами с ролью Character
  characterSlug?: string;
  // Образцы с компьютера (байты) и из проекта (путь от корня) — у каждого своя роль
  references?: ImageEditUploadedReference[];
  referencePaths?: ImageEditProjectReference[];
  // Пропорции «Дорисовать за края»: 1:1, 16:9, 9:16
  aspectRatio?: string;
  // Вернуть размер оригинала после скачивания (ADR-018 §9); по умолчанию сервер — true
  matchSourceSize?: boolean;
  chatSessionId?: string;
}

export interface ImageEditUploadedReference { file: Blob; name: string; role: ReferenceRole }
export interface ImageEditProjectReference { path: string; role: ReferenceRole }

// multipart запуска задачи. Роли образцов — параллельные списки к файлам и к путям
// (StartJobForm на сервере): referenceRoles[i] — роль references[i], и так же для путей
export function jobForm(input: ImageEditJobInput): FormData {
  const form = new FormData();
  form.append('quoteId', input.quoteId);
  form.append('prompt', input.prompt);
  if (input.marks) form.append('marks', input.marks);
  if (input.sourcePath) form.append('sourcePath', input.sourcePath);
  if (input.source) form.append('source', input.source, 'source');
  if (input.mask) form.append('mask', input.mask, 'mask.png');
  if (input.annotated) form.append('annotated', input.annotated, 'annotated.png');
  if (input.characterSlug) form.append('characterSlug', input.characterSlug);
  for (const r of input.references ?? []) {
    form.append('references', r.file, r.name);
    form.append('referenceRoles', r.role);
  }
  for (const r of input.referencePaths ?? []) {
    form.append('referencePaths', r.path);
    form.append('referencePathRoles', r.role);
  }
  if (input.aspectRatio) form.append('aspectRatio', input.aspectRatio);
  if (input.matchSourceSize != null) form.append('matchSourceSize', String(input.matchSourceSize));
  if (input.chatSessionId) form.append('chatSessionId', input.chatSessionId);
  return form;
}

// Персонаж — папка characters/<slug>/ в проекте (ADR-017, раздел 10)
export interface ImageEditCharacterPhoto { file: string; primary?: boolean | null; angle?: string | null }

export interface ImageEditCharacter {
  slug: string;
  name: string;
  description?: string | null;
  // Папка персонажа от корня проекта: characters/<slug>
  path: string;
  photos: ImageEditCharacterPhoto[];
  createdAt: string;
}

export interface ImageEditCharacterInput {
  name: string;
  description?: string;
  // Имена уже лежащих фото, которые убрать (только при правке)
  removePhotos?: string[];
  photos: Blob[];
}

// «Обсудить с Claude»: текст сообщения собирает фронт, сервер создаёт (или
// переиспользует) чат проекта с размеченной копией во вложении и дописывает путь
// к папке персонажа
export interface ImageEditDiscussInput {
  text: string;
  annotated: Blob;
  sourcePath?: string;
  characterSlug?: string;
  sessionId?: string;
}

export interface ImageEditDiscussResult { sessionId: string; created: boolean; attachments: string[] }

// next-version — hero.v2.png рядом с исходником; as — «Сохранить как…» (ADR-018 §5)
export type ImageEditSaveMode = 'next-version' | 'as';

// Источник — вариант задачи (jobId + variant) или шаг истории (stepId).
// mode не задан — прежнее поведение (next-version)
export interface ImageEditSaveRequest {
  jobId?: string | null;
  variant: number;
  sourcePath?: string;
  folder?: string;
  fileName?: string;
  mode?: ImageEditSaveMode | null;
  stepId?: string | null;
  // Чат картинки переезжает на новый файл вместе с редактором
  chatSessionId?: string | null;
  // Перекодирование при записи; null — формат результата как есть
  encode?: ImageEncodeSpec | null;
}

// GET …/save/check: path — итоговый путь с расширением по формату;
// suggestion — ближайшее свободное имя, null — если имя свободно
export interface SaveCheckRequest { folder?: string; name: string; format?: ImageEncodeFormat }
export interface SaveCheckResponse { path: string; taken: boolean; suggestion: string | null }

// ── Правки без ИИ: POST …/transform[?dryRun=true] (ADR-018 §9) ─────────────────

export type ImageEncodeFormat = 'png' | 'jpeg' | 'webp';

// quality 40–100, у PNG игнорируется; null — качество по умолчанию формата
export interface ImageEncodeSpec { format: ImageEncodeFormat; quality?: number | null }

// Прямоугольник в долях от размеров картинки (0..1), начало — левый верхний угол
export interface ImageFractionRect { x: number; y: number; width: number; height: number }

export type ImageFlipAxis = 'horizontal' | 'vertical';

// Дискриминатор type — первым полем объекта (так его ждёт System.Text.Json)
export type ImageTransformOp =
  | { type: 'autoOrient' }
  | { type: 'crop'; rect: ImageFractionRect }
  // по часовой
  | { type: 'rotate'; degrees: 90 | 180 | 270 }
  | { type: 'flip'; axis: ImageFlipAxis }
  // либо width/height в px, либо percent; lockAspect — недостающую сторону досчитать
  | { type: 'resize'; width?: number | null; height?: number | null; percent?: number | null; lockAspect?: boolean };

// База правки: файл проекта или шаг истории сеанса — ровно одно из двух
export type ImageTransformBase = { path: string; stepId?: null } | { stepId: string; path?: null };

export interface ImageTransformRequest {
  base: ImageTransformBase;
  ops: ImageTransformOp[];
  encode?: ImageEncodeSpec | null;
}

// stepId = null при dryRun: шаг не записан, посчитан только вес
export interface ImageTransformResponse { stepId: string | null; width: number; height: number; bytes: number }

// ── Чат картинки: …/image-editor/chats (ADR-018 §1) ────────────────────────────

export interface ImageChatCreateRequest { sourcePath: string; personaId?: string | null }

// current — чат с currentPath == path; continued — чаты, у которых path в lineage
export interface ImageChatLookupResponse { current: Session | null; continued: Session[] }

export interface ImageChatPathRequest { path: string }

export interface ImageChatReference { path: string; role: ReferenceRole; label?: string | null }

export type ImageChatEventKind = 'launched' | 'completed' | 'failed' | 'cancelled' | 'saved';

// Запись журнала «с прошлого хода» для блока состояния хода
export interface ImageChatEvent { at: string; kind: ImageChatEventKind; text: string; jobId?: string | null }

// Состояние редактора чата картинки на сервере: GET/PUT …/chats/{sessionId}/state.
// revision растёт с каждой записью, запись со старой revision — 409.
// marks — marks.json как есть; canvasRevision — хеш файла, шага и пометок;
// lastSentRevision — ревизия, снимок которой уже ушёл в чат
export interface ImageChatState {
  prompt: string;
  promptAuthor: ImageEditInitiator;
  provider: string | null;
  model: string | null;
  mode: EditMode;
  count: number;
  references: ImageChatReference[];
  characterSlug: string | null;
  marks: unknown;
  canvasRevision: string | null;
  lastSentRevision: string | null;
  currentStepId: string | null;
  matchSourceSize: boolean;
  events: ImageChatEvent[];
  revision: number;
}

// field — имя поля ImageChatState в camelCase; from/to — значения для метки «✦ модель сменил Claude»
export interface ImageChatStateChange { field: keyof ImageChatState | string; from?: unknown; to?: unknown }

// Записи ленты чата картинки в history.json (StoredMessage, дискриминатор kind).
// Модель их не видит — о ручном запуске она узнаёт из блока состояния хода
export interface ImageLaunchStoredMessage {
  kind: 'image_launch';
  by: ImageEditInitiator;
  prompt: string;
  provider: string;
  model: string;
  count: number;
  estimate?: ImageEditEstimate | null;
  jobId: string;
  timestamp?: number | null;
}

export interface ImageFileMovedStoredMessage { kind: 'image_file_moved'; from: string; to: string; timestamp?: number | null }

// StoredUserMessage.imageSnapshot: приложен ли снимок холста к сообщению и на какой ревизии
export interface ImageSnapshot { revision: string; attached: boolean }

// SignalR-события задачи (группа владельца, событие message)
// chatSessionId и initiator — те же, что у ImageEditJob
interface ImageEditEventOrigin { chatSessionId?: string | null; initiator?: ImageEditInitiator }

export type ImageEditEvent = ImageEditEventOrigin & (
  | { type: 'image_edit_progress'; jobId: string; projectId: string; stage: EditStage; queuePosition?: number | null }
  | { type: 'image_edit_completed'; jobId: string; projectId: string; variants: number[]; cost?: EditCost | null }
  | { type: 'image_edit_failed'; jobId: string; projectId: string; outcome: EditOutcome; charged?: boolean | null; error?: string | null; retryQuote?: ImageEditQuote | null });

// Состояние редактора чата картинки сменилось (чаще всего его поменял агент).
// sessionId — чат: редактор применяет событие, только если открыт именно он
export interface ImageChatStateEvent {
  type: 'image_chat_state';
  sessionId: string;
  projectId: string;
  revision: number;
  state: ImageChatState;
  changedBy: ImageEditInitiator;
  changes: ImageChatStateChange[];
}

// Коды ошибок ручек: { error, code }
export type ImageEditErrorCode =
  | 'provider_unavailable' | 'invalid_request' | 'quote_not_found'
  | 'job_not_found' | 'character_not_found' | 'too_many_jobs' | 'image_editor_unavailable'
  // 409 «Сохранить как…»: имя занято, в теле ошибки есть suggestion
  | 'name_taken';

export function imageEditErrorCode(e: unknown): ImageEditErrorCode | null {
  const body = (e as { body?: { code?: unknown } } | null)?.body;
  return typeof body?.code === 'string' ? body.code as ImageEditErrorCode : null;
}

// Свободное имя из тела 409 name_taken
export function nameTakenSuggestion(e: unknown): string | null {
  const body = (e as { body?: { code?: unknown; suggestion?: unknown } } | null)?.body;
  return body?.code === 'name_taken' && typeof body.suggestion === 'string' ? body.suggestion : null;
}

// 409 на PUT состояния: запись со старой revision — перечитать состояние
export function isStaleRevision(e: unknown): boolean {
  return (e as { status?: unknown } | null)?.status === 409;
}

export interface ImageEditorApi {
  catalog(projectId: string): Promise<ImageEditCatalog>;
  quote(projectId: string, req: ImageEditQuoteRequest): Promise<ImageEditQuote>;
  startJob(projectId: string, input: ImageEditJobInput): Promise<{ jobId: string }>;
  getJob(projectId: string, jobId: string): Promise<ImageEditJob>;
  cancelJob(projectId: string, jobId: string): Promise<ImageEditJob>;
  // URL варианта для <img>: токен через ?access_token=, тег заголовков не шлёт
  variantUrl(projectId: string, jobId: string, n: number): string;
  save(projectId: string, req: ImageEditSaveRequest): Promise<{ path: string }>;
  subscribe(handler: (e: ImageEditEvent) => void): () => void;
  listCharacters(projectId: string): Promise<ImageEditCharacter[]>;
  createCharacter(projectId: string, input: ImageEditCharacterInput): Promise<ImageEditCharacter>;
  updateCharacter(projectId: string, slug: string, input: ImageEditCharacterInput): Promise<ImageEditCharacter>;
  deleteCharacter(projectId: string, slug: string): Promise<void>;
  characterPhotoUrl(projectId: string, slug: string, file: string): string;
  discuss(projectId: string, input: ImageEditDiscussInput): Promise<ImageEditDiscussResult>;
  saveCheck(projectId: string, req: SaveCheckRequest): Promise<SaveCheckResponse>;
  // dryRun — только посчитать вес: шаг не пишется, stepId = null
  transform(projectId: string, req: ImageTransformRequest, opts?: { dryRun?: boolean }): Promise<ImageTransformResponse>;
  createChat(projectId: string, req: ImageChatCreateRequest): Promise<Session>;
  findChats(projectId: string, path: string): Promise<ImageChatLookupResponse>;
  setChatPath(projectId: string, sessionId: string, req: ImageChatPathRequest): Promise<Session>;
  getChatState(projectId: string, sessionId: string): Promise<ImageChatState>;
  putChatState(projectId: string, sessionId: string, state: ImageChatState): Promise<ImageChatState>;
  subscribeChatState(handler: (e: ImageChatStateEvent) => void): () => void;
}

const base = (projectId: string) => `/projects/${encodeURIComponent(projectId)}/image-editor`;

const withToken = (url: string) => {
  const token = readStoredToken();
  return token ? `${url}?access_token=${encodeURIComponent(token)}` : url;
};

function characterForm(input: ImageEditCharacterInput): FormData {
  const form = new FormData();
  form.append('name', input.name);
  if (input.description) form.append('description', input.description);
  input.removePhotos?.forEach(f => form.append('removePhotos', f));
  input.photos.forEach((b, i) => form.append('photos', b, b instanceof File ? b.name : `photo-${i + 1}.jpg`));
  return form;
}

const charBase = (projectId: string) => `${base(projectId)}/characters`;
const chatBase = (projectId: string) => `${base(projectId)}/chats`;

const IMAGE_EDIT_EVENTS = new Set(['image_edit_progress', 'image_edit_completed', 'image_edit_failed']);

const liveApi: ImageEditorApi = {
  catalog: projectId => request<ImageEditCatalog>(`${base(projectId)}/catalog`, { live: true }),
  quote: (projectId, req) =>
    request<ImageEditQuote>(`${base(projectId)}/quote`, { method: 'POST', body: JSON.stringify(req), timeoutMs: 60_000 }),
  startJob: (projectId, input) =>
    request<{ jobId: string }>(`${base(projectId)}/jobs`, { method: 'POST', body: jobForm(input), timeoutMs: 120_000 }),
  getJob: (projectId, jobId) =>
    request<ImageEditJob>(`${base(projectId)}/jobs/${encodeURIComponent(jobId)}`, { live: true }),
  cancelJob: (projectId, jobId) =>
    request<ImageEditJob>(`${base(projectId)}/jobs/${encodeURIComponent(jobId)}`, { method: 'DELETE' }),
  variantUrl: (projectId, jobId, n) =>
    withToken(`/api${base(projectId)}/jobs/${encodeURIComponent(jobId)}/variants/${n}`),
  save: (projectId, req) =>
    request<{ path: string }>(`${base(projectId)}/save`, { method: 'POST', body: JSON.stringify(req) }),
  subscribe: handler => onMessage(msg => {
    const m = msg as unknown as { type?: string };
    if (m.type && IMAGE_EDIT_EVENTS.has(m.type)) handler(m as unknown as ImageEditEvent);
  }),
  listCharacters: projectId => request<ImageEditCharacter[]>(charBase(projectId), { live: true }),
  createCharacter: (projectId, input) =>
    request<ImageEditCharacter>(charBase(projectId), { method: 'POST', body: characterForm(input), timeoutMs: 120_000 }),
  updateCharacter: (projectId, slug, input) =>
    request<ImageEditCharacter>(`${charBase(projectId)}/${encodeURIComponent(slug)}`,
      { method: 'PUT', body: characterForm(input), timeoutMs: 120_000 }),
  deleteCharacter: (projectId, slug) =>
    request<void>(`${charBase(projectId)}/${encodeURIComponent(slug)}`, { method: 'DELETE' }),
  characterPhotoUrl: (projectId, slug, file) =>
    withToken(`/api${charBase(projectId)}/${encodeURIComponent(slug)}/photos/${encodeURIComponent(file)}`),
  discuss: (projectId, input) => {
    const form = new FormData();
    form.append('text', input.text);
    form.append('annotated', input.annotated, 'annotated.png');
    if (input.sourcePath) form.append('sourcePath', input.sourcePath);
    if (input.characterSlug) form.append('characterSlug', input.characterSlug);
    if (input.sessionId) form.append('sessionId', input.sessionId);
    return request<ImageEditDiscussResult>(`${base(projectId)}/discuss`, { method: 'POST', body: form, timeoutMs: 120_000 });
  },
  saveCheck: (projectId, req) => {
    const q = new URLSearchParams({ folder: req.folder ?? '', name: req.name });
    if (req.format) q.set('format', req.format);
    return request<SaveCheckResponse>(`${base(projectId)}/save/check?${q}`, { live: true });
  },
  transform: (projectId, req, opts) =>
    request<ImageTransformResponse>(`${base(projectId)}/transform${opts?.dryRun ? '?dryRun=true' : ''}`,
      { method: 'POST', body: JSON.stringify(req), timeoutMs: 60_000 }),
  createChat: (projectId, req) =>
    request<Session>(chatBase(projectId), { method: 'POST', body: JSON.stringify(req) }),
  findChats: (projectId, path) =>
    request<ImageChatLookupResponse>(`${chatBase(projectId)}?path=${encodeURIComponent(path)}`, { live: true }),
  setChatPath: (projectId, sessionId, req) =>
    request<Session>(`${chatBase(projectId)}/${encodeURIComponent(sessionId)}/path`, { method: 'PUT', body: JSON.stringify(req) }),
  getChatState: (projectId, sessionId) =>
    request<ImageChatState>(`${chatBase(projectId)}/${encodeURIComponent(sessionId)}/state`, { live: true }),
  putChatState: (projectId, sessionId, state) =>
    request<ImageChatState>(`${chatBase(projectId)}/${encodeURIComponent(sessionId)}/state`, { method: 'PUT', body: JSON.stringify(state) }),
  subscribeChatState: handler => onMessage(msg => {
    const m = msg as unknown as { type?: string };
    if (m.type === 'image_chat_state') handler(m as unknown as ImageChatStateEvent);
  }),
};

// ── Мок ───────────────────────────────────────────────────────────────────────
// Цены и модели условные — как в макете docs/mockups/image-editor-v1.html.

const caps = (ops: ImageEditOp[], mask: MaskSupport): ImageEditCaps =>
  ({ ops, mask, maxReferences: 4, maxCount: 4, faceByReferences: mask !== 'native' });
const ALL_EDIT: ImageEditOp[] = ['generate', 'edit', 'inpaint', 'outpaint', 'removeBackground', 'upscale'];
const AUTO_ITEM: ImageEditModel = { id: AUTO_MODEL, label: 'Авто', modes: ['fast', 'precise', 'photoreal'] };

const MOCK_PROVIDERS: ImageEditProvider[] = [
  { key: 'fal', label: 'fal', priceUnit: 'usd', models: [
    AUTO_ITEM,
    { id: 'fal-ai/nano-banana-2/edit', label: 'Nano Banana 2', caps: caps(ALL_EDIT, 'asReference'), priceHint: { amount: 0.04, unit: 'usd', per: 'image' } },
    { id: 'fal-ai/flux-pro/kontext', label: 'FLUX Kontext', caps: caps(ALL_EDIT, 'none'), priceHint: { amount: 0.04, unit: 'usd', per: 'image' } },
    { id: 'fal-ai/flux-pro/v1/fill', label: 'FLUX Fill', caps: caps(['inpaint', 'outpaint', 'edit'], 'native'), priceHint: { amount: 0.05, unit: 'usd', per: 'image' } },
  ] },
  { key: 'higgsfield', label: 'Higgsfield', priceUnit: 'credits', models: [
    AUTO_ITEM,
    { id: 'gpt_image_2_5', label: 'GPT Image 2.5', caps: caps(ALL_EDIT, 'native'), priceHint: { amount: 2, unit: 'credits', per: 'image' } },
    { id: 'soul_2', label: 'Soul', caps: caps(['generate', 'edit'], 'none'), priceHint: { amount: 3, unit: 'credits', per: 'image' } },
    { id: 'nano_banana_2', label: 'Nano Banana', caps: caps(ALL_EDIT, 'none'), priceHint: { amount: 1, unit: 'credits', per: 'image' } },
  ] },
];

function mockMode(): 'fal' | 'all' | null {
  try {
    const v = localStorage.getItem('cc-image-editor-mock');
    return v === 'fal' || v === 'all' ? v : null;
  } catch {
    return null;
  }
}

// Исходник мока: файла проекта мок не читает, размеры условные
export const MOCK_SOURCE_SIZE = { width: 1600, height: 1200 };

const EXT: Record<ImageEncodeFormat, string> = { png: 'png', jpeg: 'jpg', webp: 'webp' };

// Правдоподобный вес: байт на пиксель по формату, у JPEG и WebP — от качества
export function mockEncodedBytes(width: number, height: number, encode?: ImageEncodeSpec | null): number {
  const format = encode?.format ?? 'png';
  const q = Math.min(100, Math.max(40, encode?.quality ?? (format === 'png' ? 100 : 85)));
  const perPixel = format === 'png' ? 1.6 : format === 'jpeg' ? 0.08 + (q - 40) * 0.009 : 0.05 + (q - 40) * 0.006;
  return Math.max(1024, Math.round(width * height * perPixel));
}

// Размеры после цепочки правок — как посчитает сервер
export function mockApplyOps(size: { width: number; height: number }, ops: ImageTransformOp[]) {
  let { width, height } = size;
  for (const op of ops) {
    if (op.type === 'crop') {
      width = Math.max(1, Math.round(width * op.rect.width));
      height = Math.max(1, Math.round(height * op.rect.height));
    } else if (op.type === 'rotate' && op.degrees !== 180) {
      [width, height] = [height, width];
    } else if (op.type === 'resize') {
      if (op.percent != null) {
        width = Math.max(1, Math.round(width * op.percent / 100));
        height = Math.max(1, Math.round(height * op.percent / 100));
      } else {
        const lock = op.lockAspect ?? true;
        const w = op.width ?? (lock && op.height ? Math.round(width * op.height / height) : width);
        const h = op.height ?? (lock && op.width ? Math.round(height * op.width / width) : height);
        [width, height] = [w, h];
      }
    }
  }
  return { width, height };
}

const mockError = (message: string, status: number, body?: Record<string, unknown>) =>
  Object.assign(new Error(message), { status, body });

export function createMockApi(mode: 'fal' | 'all'): ImageEditorApi {
  const providers = mode === 'all' ? MOCK_PROVIDERS : MOCK_PROVIDERS.filter(p => p.key === 'fal');
  const listeners = new Set<(e: ImageEditEvent) => void>();
  const emit = (e: ImageEditEvent) => listeners.forEach(fn => fn(e));
  const quotes = new Map<string, ImageEditQuote & { count: number }>();
  const jobs = new Map<string, { job: ImageEditJob; timers: number[] }>();
  let characters: { character: ImageEditCharacter; urls: Map<string, string> }[] = [];
  const delay = <T,>(v: T, ms = 150) => new Promise<T>(r => setTimeout(() => r(v), ms));
  let seq = 0;
  // Файлы, записанные «Сохранить как…»: повтор имени — 409 name_taken
  const savedPaths = new Set<string>();
  const steps = new Map<string, { width: number; height: number }>();
  const chats = new Map<string, Session>();
  const chatStates = new Map<string, ImageChatState>();

  // Путь «Сохранить как…»: расширение по формату, вписанное руками срезается.
  // Имя, оканчивающееся на taken, мок считает занятым — так проверяется 409
  const resolveAs = (folder: string | undefined, name: string, format: ImageEncodeFormat = 'png') => {
    const stem = name.trim().replace(/\.(png|jpe?g|webp)$/i, '');
    const dir = folder ? `${folder.replace(/\/+$/, '')}/` : '';
    const at = (s: string) => `${dir}${s}.${EXT[format]}`;
    const path = at(stem);
    const isTaken = (p: string) => savedPaths.has(p) || (p === path && /taken$/i.test(stem));
    if (!isTaken(path)) return { path, taken: false, suggestion: null };
    let n = 2;
    while (isTaken(at(`${stem}.v${n}`))) n++;
    return { path, taken: true, suggestion: at(`${stem}.v${n}`) };
  };

  const moveChat = (sessionId: string | null | undefined, to: string) => {
    const chat = sessionId ? chats.get(sessionId) : undefined;
    if (!chat?.imageChat || chat.imageChat.currentPath === to) return;
    chat.imageChat = { currentPath: to, lineage: [...chat.imageChat.lineage, chat.imageChat.currentPath] };
  };

  const emptyState = (): ImageChatState => ({
    prompt: '', promptAuthor: 'human', provider: null, model: null, mode: 'auto', count: 1,
    references: [], characterSlug: null, marks: null, canvasRevision: null, lastSentRevision: null,
    currentStepId: null, matchSourceSize: true, events: [], revision: 0,
  });

  const ownChat = (projectId: string, sessionId: string) => {
    const chat = chats.get(sessionId);
    if (!chat || chat.projectId !== projectId) throw mockError('Чат не найден', 404);
    return chat;
  };

  const resolveModel = (p: ImageEditProvider, model: string) =>
    model === AUTO_MODEL ? p.models[1] : (p.models.find(m => m.id === model) ?? p.models[1]);

  return {
    catalog: () => delay<ImageEditCatalog>({
      default: { provider: providers.at(-1)?.key ?? null, model: AUTO_MODEL },
      providers,
      limits: { maxFileMb: 20, maxReferences: 6, maxCount: 4 },
      reason: providers.length ? null : 'no_provider_configured',
    }),
    quote: async (_projectId, req) => {
      const p = providers.find(x => x.key === req.provider);
      if (!p) throw Object.assign(new Error('Поставщик рисования не настроен'), { status: 409, body: { code: 'provider_unavailable' } });
      const m = resolveModel(p, req.model);
      const per = m.priceHint?.amount ?? 0;
      const quote: ImageEditQuote = {
        quoteId: `q${++seq}`, provider: p.key, model: m.id,
        estimate: { amount: Math.round(per * req.count * 100) / 100, unit: p.priceUnit, approx: true, source: 'catalog' },
        expiresAt: new Date(Date.now() + 10 * 60_000).toISOString(),
        expectedSeconds: p.key === 'fal' ? 8 : 12,
      };
      quotes.set(quote.quoteId, { ...quote, count: req.count });
      return delay(quote, p.key === 'fal' ? 120 : 300);
    },
    startJob: async (projectId, input) => {
      const q = quotes.get(input.quoteId);
      if (!q) throw Object.assign(new Error('Котировка устарела'), { status: 404, body: { code: 'quote_not_found' } });
      const jobId = `j${++seq}`;
      const job: ImageEditJob = {
        jobId, projectId, status: 'queued', provider: q.provider, model: q.model,
        variants: [], createdAt: new Date().toISOString(),
        chatSessionId: input.chatSessionId ?? null, initiator: 'human',
      };
      const origin = { chatSessionId: job.chatSessionId, initiator: job.initiator };
      const entry = { job, timers: [] as number[] };
      jobs.set(jobId, entry);
      // «сломать» генерацию можно словом в запросе — так проверяется экран ошибки
      const failKind: EditOutcome | null = /кредит/i.test(input.prompt) ? 'insufficientCredits'
        : /ошибк/i.test(input.prompt) ? 'failed' : null;
      const total = (q.expectedSeconds ?? 8) * 1000;
      const at = (ms: number, fn: () => void) => entry.timers.push(window.setTimeout(fn, ms));
      at(200, () => { job.status = 'running'; emit({ type: 'image_edit_progress', jobId, projectId, stage: 'running', ...origin }); });
      if (failKind) {
        at(total / 2, () => {
          Object.assign(job, { status: 'failed', outcome: failKind, charged: false,
            error: failKind === 'failed' ? 'Сервис не ответил' : 'Не хватает кредитов' });
          emit({ type: 'image_edit_failed', jobId, projectId, outcome: failKind, charged: false, error: job.error, ...origin });
        });
      } else {
        at(total * 0.85, () => { job.status = 'downloading'; emit({ type: 'image_edit_progress', jobId, projectId, stage: 'downloading', ...origin }); });
        at(total, () => {
          const cost = q.estimate.amount != null ? { amount: q.estimate.amount, unit: q.estimate.unit } : null;
          Object.assign(job, { status: 'completed', outcome: 'ok', charged: true, cost,
            variants: Array.from({ length: q.count }, (_, i) => i) });
          emit({ type: 'image_edit_completed', jobId, projectId, variants: job.variants, cost, ...origin });
        });
      }
      return delay({ jobId });
    },
    getJob: async (_projectId, jobId) => {
      const e = jobs.get(jobId);
      if (!e) throw Object.assign(new Error('Задача не найдена'), { status: 404, body: { code: 'job_not_found' } });
      return delay({ ...e.job }, 50);
    },
    cancelJob: async (_projectId, jobId) => {
      const e = jobs.get(jobId);
      if (!e) throw Object.assign(new Error('Задача не найдена'), { status: 404, body: { code: 'job_not_found' } });
      e.timers.forEach(t => clearTimeout(t));
      Object.assign(e.job, { status: 'cancelled', outcome: 'cancelled', charged: false });
      return delay({ ...e.job }, 50);
    },
    // Мок-вариант — исходник проекта не нужен: рисуем плашку с номером в SVG
    variantUrl: (_projectId, jobId, n) => {
      const hue = (n * 70 + jobId.length * 20) % 360;
      const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 640 400"><rect width="640" height="400" fill="hsl(${hue},45%,70%)"/><text x="320" y="215" font-size="48" text-anchor="middle" fill="hsl(${hue},40%,25%)" font-family="sans-serif">Вариант ${n + 1}</text></svg>`;
      return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;
    },
    save: async (_projectId, req) => {
      const found = req.stepId ? steps.has(req.stepId) : req.jobId != null && jobs.has(req.jobId);
      if (!found) throw mockError('Задача не найдена', 404, { code: 'job_not_found' });
      let path: string;
      if (req.mode === 'as') {
        if (!req.fileName?.trim()) throw mockError('Укажите имя файла', 400, { code: 'invalid_request' });
        const r = resolveAs(req.folder, req.fileName, req.encode?.format);
        if (r.taken) throw mockError('Файл с таким именем уже есть', 409, { code: 'name_taken', suggestion: r.suggestion });
        path = r.path;
        savedPaths.add(path);
      } else {
        path = req.sourcePath
          ? req.sourcePath.replace(/(?:\.v\d+)?\.(\w+)$/, '.v2.png')
          : `${req.folder ? `${req.folder}/` : ''}${req.fileName ?? 'новая-картинка.png'}`;
      }
      moveChat(req.chatSessionId, path);
      return delay({ path });
    },
    saveCheck: async (_projectId, req) => delay(resolveAs(req.folder, req.name, req.format), 60),
    transform: async (_projectId, req, opts) => {
      if (req.ops.some(op => op.type === 'rotate' && ![90, 180, 270].includes(op.degrees)))
        throw mockError('Поворот только на 90, 180 или 270°', 400, { code: 'invalid_request' });
      const baseSize = req.base.stepId ? steps.get(req.base.stepId) : MOCK_SOURCE_SIZE;
      if (!baseSize) throw mockError('Шаг не найден', 404, { code: 'invalid_request' });
      const size = mockApplyOps(baseSize, req.ops);
      const bytes = mockEncodedBytes(size.width, size.height, req.encode);
      if (opts?.dryRun) return delay({ stepId: null, ...size, bytes }, 80);
      const stepId = `s${++seq}`;
      steps.set(stepId, size);
      return delay({ stepId, ...size, bytes }, 200);
    },
    createChat: async (projectId, req) => {
      const now = new Date().toISOString();
      const chat: Session = {
        id: `mock-image-chat-${++seq}`, projectId, personaId: req.personaId ?? undefined,
        name: `${req.sourcePath.split('/').pop()} · правка`, mode: 'default', status: 'finished',
        messageCount: 0, createdAt: now, updatedAt: now, origin: 'manual',
        imageChat: { currentPath: req.sourcePath, lineage: [] },
      };
      chats.set(chat.id, chat);
      return delay({ ...chat }, 200);
    },
    findChats: async (projectId, path) => {
      const own = [...chats.values()].filter(c => c.projectId === projectId && c.imageChat)
        .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
      return delay({
        current: own.find(c => c.imageChat!.currentPath === path) ?? null,
        continued: own.filter(c => c.imageChat!.lineage.includes(path)),
      });
    },
    // Привязка к другому файлу — настройка: updatedAt не двигается
    setChatPath: async (projectId, sessionId, req) => {
      const chat = ownChat(projectId, sessionId);
      if (!chat.imageChat) throw mockError('Это не чат картинки', 400, { code: 'invalid_request' });
      moveChat(sessionId, req.path);
      return delay({ ...chat });
    },
    getChatState: async (projectId, sessionId) => {
      ownChat(projectId, sessionId);
      return delay(chatStates.get(sessionId) ?? emptyState(), 60);
    },
    putChatState: async (projectId, sessionId, state) => {
      ownChat(projectId, sessionId);
      const current = chatStates.get(sessionId) ?? emptyState();
      if (state.revision !== current.revision) throw mockError('Состояние уже изменилось', 409);
      const next = { ...state, revision: current.revision + 1 };
      chatStates.set(sessionId, next);
      return delay(next, 60);
    },
    // Агента в моке нет, а своё состояние редактор знает сам — событий не бывает
    subscribeChatState: () => () => {},
    subscribe: handler => {
      listeners.add(handler);
      return () => { listeners.delete(handler); };
    },
    // Персонажи мока живут в памяти вкладки, фото — object URL загруженных файлов
    listCharacters: () => delay(characters.map(c => c.character)),
    createCharacter: async (_projectId, input) => {
      const taken = new Set(characters.map(c => c.character.slug));
      const root = characterSlug(input.name);
      let slug = root;
      for (let i = 2; taken.has(slug); i++) slug = `${root}-${i}`;
      const urls = new Map<string, string>();
      const character = mockCharacter(slug, input, [], urls);
      characters.push({ character, urls });
      return delay(character, 300);
    },
    updateCharacter: async (_projectId, slug, input) => {
      const e = characters.find(c => c.character.slug === slug);
      if (!e) throw Object.assign(new Error('Персонаж не найден'), { status: 404 });
      const kept = e.character.photos.filter(p => !input.removePhotos?.includes(p.file));
      e.character = mockCharacter(slug, input, kept, e.urls);
      return delay(e.character, 300);
    },
    deleteCharacter: async (_projectId, slug) => {
      characters = characters.filter(c => c.character.slug !== slug);
      return delay(undefined);
    },
    characterPhotoUrl: (_projectId, slug, file) =>
      characters.find(c => c.character.slug === slug)?.urls.get(file) ?? '',
    // Мок-чат не создаётся: панель ответа покажет пустое ожидание
    discuss: async (_projectId, input) =>
      delay({ sessionId: input.sessionId ?? `mock-chat-${++seq}`, created: !input.sessionId, attachments: [] }, 300),
  };
}

// Имя папки персонажа: транслит имени, как в макете («Аня» → anya)
const TRANSLIT: Record<string, string> = {
  а: 'a', б: 'b', в: 'v', г: 'g', д: 'd', е: 'e', ё: 'e', ж: 'zh', з: 'z', и: 'i', й: 'y', к: 'k', л: 'l', м: 'm',
  н: 'n', о: 'o', п: 'p', р: 'r', с: 's', т: 't', у: 'u', ф: 'f', х: 'h', ц: 'ts', ч: 'ch', ш: 'sh', щ: 'sch',
  ъ: '', ы: 'y', ь: '', э: 'e', ю: 'yu', я: 'ya',
};

export function characterSlug(name: string): string {
  const s = name.trim().toLowerCase().split('')
    .map(ch => TRANSLIT[ch] ?? (/[a-z0-9]/.test(ch) ? ch : /[\s_-]/.test(ch) ? '-' : ''))
    .join('').replace(/-+/g, '-').replace(/^-|-$/g, '');
  return s || 'character';
}

function mockCharacter(slug: string, input: ImageEditCharacterInput, kept: ImageEditCharacterPhoto[], urls: Map<string, string>): ImageEditCharacter {
  const photos = [...kept];
  let n = 0;
  for (const blob of input.photos) {
    let file: string;
    do file = `face-${String(++n).padStart(2, '0')}.jpg`; while (photos.some(p => p.file === file));
    urls.set(file, URL.createObjectURL(blob));
    photos.push({ file });
  }
  photos.forEach((p, i) => { p.primary = i === 0; });
  return {
    slug, name: input.name.trim(), description: input.description?.trim() || null,
    path: `characters/${slug}`, photos, createdAt: new Date().toISOString(),
  };
}

let mockApi: { mode: 'fal' | 'all'; api: ImageEditorApi } | null = null;

// Точка входа экрана: живые ручки или мок по localStorage
export function imageEditorApi(): ImageEditorApi {
  const mode = mockMode();
  if (!mode) return liveApi;
  if (mockApi?.mode !== mode) mockApi = { mode, api: createMockApi(mode) };
  return mockApi.api;
}
