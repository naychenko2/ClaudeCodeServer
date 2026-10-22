// API редактора картинок (ADR-017). Контракт — DTO и маршруты из
// backend/ClaudeHomeServer.Core/Services/ImageEditor (волна 1), база
// api/projects/{projectId}/image-editor, у личного чата — api/image-editor/chats/{sessionId}
// (scope.ts). Enum'ы приходят строками в camelCase.
//
// Мок-режим: пока драйверов поставщиков нет, живой сервер отдаёт пустой каталог и
// 409 на запуск. Для работы экрана без драйверов в localStorage ставится
// `cc-image-editor-mock`: `fal` — в каталоге только fal, `all` — fal, Higgsfield и локальные модели.
// Мок повторяет контракт целиком, включая SignalR-события задачи.

import { request, readStoredToken, onMessage } from 'aihome_shell/kit';
import type { MediaFormatState, MediaFormatWish } from 'aihome_shell/kit';
import type { LocalGpuQueueInfo } from '../../types/localGpuQueue';
import { mockFormatState, mockTierFactor } from './mockFormat';
import { isPersonalScope, projectBase, scopeBase, threadsBase } from './scope';
import { transformedSize } from './transforms';

export type ImageEditOp = 'generate' | 'edit' | 'inpaint' | 'outpaint' | 'removeBackground' | 'upscale' | 'enhanceFaces';
export type EditMode = 'auto' | 'fast' | 'precise' | 'photoreal';
export type MaskSupport = 'none' | 'native' | 'asReference';
export type ReferenceRole = 'character' | 'style' | 'object';
export type EditOutcome = 'ok' | 'failed' | 'insufficientCredits' | 'rejected' | 'cancelled' | 'unavailable';
export type EditStage = 'queued' | 'running' | 'downloading';
export type PriceUnit = 'usd' | 'credits' | 'free';
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
  // Сколько фото персонажа модель берёт в один запуск (ADR-027): local (Qwen-Image) — 1,
  // не задан — обычный лимит персонажа (photosPerRequest)
  maxCharacterPhotos?: number | null;
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
  // false — заведён, но сейчас не отвечает (лежит ComfyUI): виден с пометкой «не отвечает»,
  // выбрать можно, запуск получит provider_unavailable. Поля нет — доступен
  available?: boolean;
}

export interface ImageEditCatalog {
  // provider = null — ни одного доступного поставщика
  default: { provider: string | null; model: string };
  providers: ImageEditProvider[];
  // heavyFileMb — порог «тяжёлого файла» для диалога сохранения (ImageEditor:HeavyFileMb)
  limits: { maxFileMb: number; maxReferences: number; maxCount: number; heavyFileMb?: number };
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
  // Чат и ревизия контекста (ADR-023 §Д2.1): с ревизией сервер берёт образцы, персонажа и размер из стора
  sessionId?: string | null;
  contextRevision?: number | null;
  // Формат генерации (ADR-025): желание проекта; без него сервер берёт сохранённое, но общий кэш котировок
  // ключуется телом, поэтому выбранный формат уходит явно
  format?: MediaFormatWish | null;
  // «Подставить персонажа» (ADR-028, этап 3): цель подстановки; персонажи живут в faces,
  // верхнеуровневого characterSlug нет; с ней ContextRevision не едет
  faceTarget?: FaceTarget | null;
}

export interface ImageEditEstimate {
  // null — «цена станет известна после запуска»
  amount: number | null;
  unit: string;
  approx: boolean;
  source: 'provider' | 'catalog' | 'history' | 'unknown';
  // У поставщика без цены (unit = free): время запуска по замерам и длина общей очереди GPU
  etaSeconds?: number | null;
  queueLength?: number | null;
}

export interface ImageEditQuote {
  quoteId: string;
  provider: string;
  model: string;
  estimate: ImageEditEstimate;
  expiresAt: string;
  expectedSeconds?: number | null;
  // Число проходов подстановки (ADR-028, этап 3): N = число лиц в faceTarget; у обычной правки поля нет
  facePasses?: number | null;
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
  // Нить основного чата (ADR-019), если задача запущена в нить
  threadId?: string | null;
  // Оценка котировки, по которой запущена задача
  estimate?: ImageEditEstimate | null;
  // Полоса прогресса: номер текущего прогона (с 1), их число, ETA прогона и сколько он уже
  // идёт по часам бэкенда (только в running). Поставщики без этих сведений отдают null
  run?: number | null;
  runs?: number | null;
  etaSeconds?: number | null;
  runElapsedSeconds?: number | null;
  // Снимок очереди GPU ComfyUI (шаг 28): живой, только отображение; null — поставщик не знает
  // или задача вне локальных моделей
  gpu?: LocalGpuQueueInfo | null;
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
  // Явный список фото персонажа (имена файлов манифеста, до photosPerRequest): «Заменить» в
  // блоке «Фото персонажа» (ADR-027, шаг 18); без него сервер сам подбирает/берёт по порядку
  characterPhotos?: string[];
  // Образцы с компьютера (байты) и из проекта (путь от корня) — у каждого своя роль
  references?: ImageEditUploadedReference[];
  referencePaths?: ImageEditProjectReference[];
  // Пропорции «Дорисовать за края»: 1:1, 16:9, 9:16
  aspectRatio?: string;
  // Вернуть размер оригинала после скачивания (ADR-018 §9); по умолчанию сервер — true
  matchSourceSize?: boolean;
  chatSessionId?: string;
  // Нить картинки основного чата (ADR-019): задача станет pendingJobId нити
  sessionId?: string;
  threadId?: string;
  // Шаг нити, от которого идёт правка (currentStepId); байты шага — в source
  baseStepId?: string;
  // Версия нити, от которой правка (не передана — текущая); без source сервер сам берёт её картинку
  versionId?: string;
  // Ревизия контекста чата (ADR-023 §Д2.1): сервер берёт входы из стора, не совпала — 409 context_changed
  contextRevision?: number;
  // «Подставить персонажа» (ADR-028, этап 3): цель подстановки; персонажи живут в faces,
  // верхнеуровневый characterSlug при подстановке не едет; с ней contextRevision/mask/annotated
  // и референсы контекста в тело не кладутся (сборка — buildQuoteBody/buildJobBody)
  faceTarget?: FaceTarget | null;
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
  // «Подставить персонажа» (ADR-028, этап 3): персонажи живут в faceTarget.faces, поэтому
  // верхнеуровневого characterSlug рядом с faceTarget не едет (иначе сервер видит двусмысленный вход)
  if (input.characterSlug && !input.faceTarget) form.append('characterSlug', input.characterSlug);
  for (const f of input.characterPhotos ?? []) form.append('characterPhotos', f);
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
  if (input.sessionId) form.append('sessionId', input.sessionId);
  if (input.threadId) form.append('threadId', input.threadId);
  if (input.baseStepId) form.append('baseStepId', input.baseStepId);
  if (input.versionId) form.append('versionId', input.versionId);
  if (input.contextRevision != null) form.append('contextRevision', String(input.contextRevision));
  // «Подставить персонажа» (ADR-028, этап 3): цель подстановки — объект
  // {versionId, facesKey, faces: [{face, characterSlug}]}, уходит JSON-строкой (как photoLabels);
  // сервер десериализует в FaceTarget
  if (input.faceTarget) form.append('faceTarget', JSON.stringify(input.faceTarget));
  return form;
}

// Персонаж — папка characters/<slug>/ в проекте (ADR-017, раздел 10)
// Метки фото (ADR-027 §1): angle/pitch/emotion — словари бэкенда, labelSource — кто разметил.
// pitch/emotion/labelSource в JSON отсутствуют (а не null), когда метки нет или флаг выключен.
export interface ImageEditCharacterPhoto {
  file: string;
  primary?: boolean | null;
  angle?: string | null;
  pitch?: string | null;
  emotion?: string | null;
  labelSource?: string | null;
}

export interface ImageEditCharacter {
  slug: string;
  name: string;
  description?: string | null;
  // Папка персонажа от корня проекта: characters/<slug>
  path: string;
  photos: ImageEditCharacterPhoto[];
  // LoRA для локальной модели; null — не задана
  local?: { lora: string; trigger: string; strength: number } | null;
  createdAt: string;
  // Сколько фото уходит в один запрос (лимит окна выбора); только при включённом character-smart-photos
  photosPerRequest?: number | null;
}

// trained — дата обучения своих LoRA (полное имя → ISO), если бэкенд её отдаёт
export interface ImageEditLoras { available: boolean; items: string[]; trained?: Record<string, string> }

// Метка одного фото (ADR-027 §1): null в поле — не менять. Ключ — имя файла из манифеста.
export interface CharacterPhotoLabelEdit { angle?: string | null; pitch?: string | null; emotion?: string | null }

export interface ImageEditCharacterInput {
  name: string;
  description?: string;
  // Имена уже лежащих фото, которые убрать (только при правке)
  removePhotos?: string[];
  photos: Blob[];
  // не задано — LoRA не трогаем; 'remove' — снять
  lora?: { name: string; trigger: string; strength: number } | 'remove';
  // Метки фото (только при включённом character-smart-photos): имя файла → {angle, pitch, emotion}
  photoLabels?: Record<string, CharacterPhotoLabelEdit>;
}

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
  // Нить основного чата (ADR-019): карточка переходит на сохранённый файл
  sessionId?: string | null;
  threadId?: string | null;
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
  | { type: 'resize'; width?: number | null; height?: number | null; percent?: number | null; lockAspect?: boolean }
  // Холст width×height не меньше картинки, картинка без масштабирования; left/top null — по центру.
  // color «#RRGGBB»/«#RRGGBBAA», null — прозрачно (у JPEG сервер зальёт белым)
  | { type: 'pad'; width: number; height: number; color?: string | null; left?: number | null; top?: number | null };

// База правки: файл проекта, шаг истории сеанса или вариант задачи — ровно одно из трёх
export type ImageTransformBase =
  | { path: string; stepId?: null; jobId?: null }
  | { stepId: string; path?: null; jobId?: null }
  | { jobId: string; variant: number; path?: null; stepId?: null };

export interface ImageTransformRequest {
  base: ImageTransformBase;
  ops: ImageTransformOp[];
  encode?: ImageEncodeSpec | null;
}

// stepId = null при dryRun: шаг не записан, посчитан только вес
export interface ImageTransformResponse { stepId: string | null; width: number; height: number; bytes: number }

// Записи ленты чатов картинки v2 в history.json (StoredMessage, дискриминатор kind): новые
// не пишутся, но история архивных чатов читается (ADR-019 §6)
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
// threadId — нить основного чата (ADR-019), её чат — в том же chatSessionId
interface ImageEditEventOrigin { chatSessionId?: string | null; initiator?: ImageEditInitiator; threadId?: string | null }

export type ImageEditEvent = ImageEditEventOrigin & (
  | { type: 'image_edit_progress'; jobId: string; projectId: string; stage: EditStage; queuePosition?: number | null;
      run?: number | null; runs?: number | null; etaSeconds?: number | null; runElapsedSeconds?: number | null;
      // Снимок очереди GPU ComfyUI (шаг 28): живой, только отображение; null — поставщик не знает
      gpu?: LocalGpuQueueInfo | null }
  | { type: 'image_edit_completed'; jobId: string; projectId: string; variants: number[]; cost?: EditCost | null }
  | { type: 'image_edit_failed'; jobId: string; projectId: string; outcome: EditOutcome; charged?: boolean | null; error?: string | null; retryQuote?: ImageEditQuote | null });

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

// ── «Подставить персонажа» на лицо (этап 2 · шаг 10a) ─────────────────────────
// Лицо, найденное на картинке версии. n — номер с 1 слева направо; bbox — рамка
// [x1, y1, x2, y2] в пикселях картинки; small — мелкое лицо (короче 64 px), выбрать нельзя
export interface DetectedFace {
  n: number;
  bbox: [number, number, number, number];
  score: number;
  small: boolean;
}

// Ответ «найти лица»: геометрия и facesKey — ключ ревизии, по которому сервер сверит выбор
// лица при запуске (сменилась картинка — 409 faces_changed со свежим списком)
export interface FacesResponse {
  facesKey: string;
  width: number;
  height: number;
  faces: DetectedFace[];
}

// Назначение персонажа на лицо (ADR-028, этап 3): номер лица (с 1) и персонаж, которого ставят на него
export interface FaceAssignment {
  face: number;
  characterSlug: string;
}

// Цель подстановки персонажей (ADR-028, этап 3): версия, ключ ревизии лиц и назначения «лицо → персонаж».
// Уходит в котировку и запуск; вместе с ним ContextRevision/Mask/Annotated/референсы не едут
export interface FaceTarget {
  versionId: string;
  facesKey: string;
  faces: FaceAssignment[];
}

// Коды ошибок «найти лица»: различимо типизированы, чтобы экран назвал причину отказа
export type FacesErrorCode =
  | 'busy'                  // 503: видеокарта занята долгой задачей
  | 'provider_unavailable'  // локальные модели выключены / ComfyUI недоступен
  | 'faces_changed'         // 409: картинка изменилась, в теле свежий список лиц
  | 'version_not_found'
  | 'not_found'             // 404: личная область (ручки нет) или нить/версия не найдены
  | 'feature_disabled'      // флаг character-face-swap выключен
  | 'other';

export interface FacesError {
  code: FacesErrorCode;
  // 409 faces_changed: свежий список лиц (тот же формат) — экран обновляет рамки и сбрасывает выбор
  faces?: FacesResponse | null;
}

// Разбор отказа «найти лица» по коду/статусу. faces_changed (409) отдаёт свежий список
// в теле — прикладываем его; прочий отказ — код по body.code, иначе по статусу
export function facesError(e: unknown): FacesError {
  const err = e as { status?: number; body?: { code?: unknown; faces?: unknown } } | null;
  const code = typeof err?.body?.code === 'string' ? err.body.code : null;
  const faces = parseFacesResponse(err?.body?.faces);
  if (code === 'busy' || code === 'provider_unavailable' || code === 'faces_changed'
    || code === 'version_not_found' || code === 'not_found' || code === 'feature_disabled') {
    return { code, ...(code === 'faces_changed' && faces ? { faces } : null) };
  }
  if (code) return { code: 'other' };
  if (err?.status === 404) return { code: 'not_found' };
  if (err?.status === 503) return { code: 'busy' };
  return { code: 'other' };
}

// Тело 409 faces_changed: свежий список лиц в том же формате
function parseFacesResponse(v: unknown): FacesResponse | null {
  const r = v as FacesResponse | null | undefined;
  if (!r || typeof r !== 'object') return null;
  if (typeof r.facesKey !== 'string' || !Array.isArray(r.faces)) return null;
  return r;
}

// «Найти лица» (этап 2 · шаг 10a): POST {threadsBase}/{threadId}/versions/{versionId}/faces.
// Только проектная область: в личной области персонажей нет по построению → типизированный
// отказ not_found без сетевого запроса. Отказ — типизированный FacesError (facesError)
export function findFaces(scope: string, sessionId: string, threadId: string, versionId: string): Promise<FacesResponse> {
  if (isPersonalScope(scope)) {
    return Promise.reject(Object.assign(new Error('not_found'), { status: 404, body: { code: 'not_found' } }));
  }
  return request<FacesResponse>(
    `${threadsBase(scope, sessionId)}/${encodeURIComponent(threadId)}/versions/${encodeURIComponent(versionId)}/faces`,
    { method: 'POST' },
  );
}

export interface ImageEditorApi {
  catalog(projectId: string): Promise<ImageEditCatalog>;
  quote(projectId: string, req: ImageEditQuoteRequest): Promise<ImageEditQuote>;
  // Состояние чипа «Формат» (ADR-025): возможности модели, желание, действующий формат, ступени с ценой
  format(projectId: string, q: { provider: string; model: string; op: ImageEditOp }): Promise<MediaFormatState>;
  startJob(projectId: string, input: ImageEditJobInput): Promise<{ jobId: string }>;
  // Образец с диска человека в рабочую папку модуля; дальше он встаёт в контекст чата ref'ом {upload}
  uploadSample(projectId: string, file: Blob, name: string): Promise<{ uploadId: string }>;
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
  loras(projectId: string): Promise<ImageEditLoras>;
  characterPhotoUrl(projectId: string, slug: string, file: string): string;
  saveCheck(projectId: string, req: SaveCheckRequest): Promise<SaveCheckResponse>;
  // dryRun — только посчитать вес: шаг не пишется, stepId = null
  transform(projectId: string, req: ImageTransformRequest, opts?: { dryRun?: boolean }): Promise<ImageTransformResponse>;
  // Картинка шага истории для <img>: токен через ?access_token=, как у вариантов
  stepUrl(projectId: string, stepId: string): string;
}

const base = scopeBase;

const withToken = (url: string) => {
  const token = readStoredToken();
  return token ? `${url}?access_token=${encodeURIComponent(token)}` : url;
};

function characterForm(input: ImageEditCharacterInput): FormData {
  const form = new FormData();
  form.append('name', input.name);
  if (input.description !== undefined) form.append('description', input.description);
  input.removePhotos?.forEach(f => form.append('removePhotos', f));
  input.photos.forEach((b, i) => form.append('photos', b, b instanceof File ? b.name : `photo-${i + 1}.jpg`));
  // Метки фото уходят JSON-строкой {файл: {angle, pitch, emotion}} (ADR-027 §1); бэк читает её как PhotoLabels
  if (input.photoLabels) form.append('photoLabels', JSON.stringify(input.photoLabels));
  if (input.lora === 'remove') form.append('removeLora', 'true');
  else if (input.lora) {
    form.append('loraName', input.lora.name);
    form.append('loraTrigger', input.lora.trigger);
    form.append('loraStrength', input.lora.strength.toString());
  }
  return form;
}

const charBase = (projectId: string) => `${projectBase(projectId)}/characters`;

// ── Обучение LoRA персонажа (ADR-026 §7–8) ────────────────────────────────────
export type LoraRunStatus = 'training' | 'ready' | 'failed' | 'cancelled';
export type LoraTrainJobStatus = 'queued' | 'running';
export type TrainingPhotoRejectReason = 'format' | 'too_big' | 'too_small' | 'duplicate' | 'limit';

export interface TrainingPhoto { name: string; bytes: number; width: number; height: number }
export interface TrainingPhotosDto { items: TrainingPhoto[]; count: number; min: number; max: number; photosHash: string }
export interface TrainingPhotosAddResultDto {
  photos: TrainingPhotosDto;
  rejected: { fileName: string; reason: TrainingPhotoRejectReason }[];
}

export interface LoraTrainCurrent {
  runId: string; jobId: string; status: LoraTrainJobStatus
  step?: number; totalSteps?: number; etaSeconds?: number; queuePosition?: number | null; startedAt?: string
}
export interface LoraTrainStateDto {
  enabled: boolean; available: boolean
  gpu: { busy: boolean; mine: boolean; busyUntil?: string }
  current: LoraTrainCurrent | null
  defaultTrigger: string; presets: { key: 'standard'; etaSeconds: number }[]; photosChanged: boolean
}
export interface LoraTrainStartRequest { preset: 'standard'; trigger?: string; confirm: true }
export interface LoraTrainStartResponse { runId: string; jobId: string; queuePosition: number; etaSeconds: number }

export type LoraRejectReason = 'no_face' | 'many_faces' | 'small_face' | 'unreadable';
export interface LoraVersionDto {
  runId: string; status: LoraRunStatus; title?: string | null; createdAt?: string; trainedAt?: string
  photosCount: number; photosKept?: number; trigger?: string; connected: boolean
  rejected?: { name: string; reason: LoraRejectReason }[]
  error?: { code: string; message: string }
}
export interface LoraVersionsDto { items: LoraVersionDto[]; own: { lora: string; trainedAt?: string | null } | null; limit: number; used: number }

// Занятость видеокарты на уровне инстанса (ADR-026 §7.4); jobId приходит только админу
export interface LoraActiveDto { busy: boolean; mine: boolean; busyUntil?: string; jobId?: string }

export interface LoraTrainApi {
  photos(projectId: string, slug: string): Promise<TrainingPhotosDto>;
  addPhotos(projectId: string, slug: string, files: Blob[]): Promise<TrainingPhotosAddResultDto>;
  deletePhoto(projectId: string, slug: string, name: string): Promise<void>;
  photoUrl(projectId: string, slug: string, name: string): string;
  trainState(projectId: string, slug: string): Promise<LoraTrainStateDto>;
  startTrain(projectId: string, slug: string, req: LoraTrainStartRequest): Promise<LoraTrainStartResponse>;
  cancelTrain(projectId: string, slug: string): Promise<void>;
  active(): Promise<LoraActiveDto>;
  cancelActive(): Promise<void>;
  versions(projectId: string, slug: string): Promise<LoraVersionsDto>;
  connectVersion(projectId: string, slug: string, runId: string): Promise<ImageEditCharacter>;
  renameVersion(projectId: string, slug: string, runId: string, title: string): Promise<LoraVersionDto>;
  deleteVersion(projectId: string, slug: string, runId: string): Promise<{ status: 'deleting'; disconnected: boolean }>;
}

const charUrl = (projectId: string, slug: string) => `${charBase(projectId)}/${encodeURIComponent(slug)}`;

export const loraTrainApi: LoraTrainApi = {
  photos: (projectId, slug) =>
    request<TrainingPhotosDto>(`${charUrl(projectId, slug)}/training-photos`, { live: true }),
  addPhotos: (projectId, slug, files) => {
    const form = new FormData();
    files.forEach((b, i) => form.append('files', b, b instanceof File ? b.name : `photo-${i + 1}.jpg`));
    return request<TrainingPhotosAddResultDto>(`${charUrl(projectId, slug)}/training-photos`,
      { method: 'POST', body: form, timeoutMs: 120_000 });
  },
  deletePhoto: (projectId, slug, name) =>
    request<void>(`${charUrl(projectId, slug)}/training-photos/${encodeURIComponent(name)}`, { method: 'DELETE' }),
  photoUrl: (projectId, slug, name) =>
    withToken(`/api${charUrl(projectId, slug)}/training-photos/${encodeURIComponent(name)}`),
  trainState: (projectId, slug) =>
    request<LoraTrainStateDto>(`${charUrl(projectId, slug)}/lora-train`, { live: true }),
  startTrain: (projectId, slug, req) =>
    request<LoraTrainStartResponse>(`${charUrl(projectId, slug)}/lora-train`,
      { method: 'POST', body: JSON.stringify(req) }),
  cancelTrain: (projectId, slug) =>
    request<void>(`${charUrl(projectId, slug)}/lora-train`, { method: 'DELETE' }),
  active: () => request<LoraActiveDto>('/image-editor/lora-train/active', { live: true }),
  cancelActive: () => request<void>('/image-editor/lora-train/active', { method: 'DELETE' }),
  versions: (projectId, slug) =>
    request<LoraVersionsDto>(`${charUrl(projectId, slug)}/lora-versions`, { live: true }),
  connectVersion: (projectId, slug, runId) =>
    request<ImageEditCharacter>(`${charUrl(projectId, slug)}/lora-versions/${encodeURIComponent(runId)}/connect`,
      { method: 'POST' }),
  renameVersion: (projectId, slug, runId, title) =>
    request<LoraVersionDto>(`${charUrl(projectId, slug)}/lora-versions/${encodeURIComponent(runId)}`,
      { method: 'PATCH', body: JSON.stringify({ title }) }),
  deleteVersion: (projectId, slug, runId) =>
    request<{ status: 'deleting'; disconnected: boolean }>(
      `${charUrl(projectId, slug)}/lora-versions/${encodeURIComponent(runId)}`, { method: 'DELETE' }),
};

const IMAGE_EDIT_EVENTS = new Set(['image_edit_progress', 'image_edit_completed', 'image_edit_failed']);

const liveApi: ImageEditorApi = {
  catalog: projectId => request<ImageEditCatalog>(`${base(projectId)}/catalog`, { live: true }),
  quote: (projectId, req) =>
    request<ImageEditQuote>(`${base(projectId)}/quote`, { method: 'POST', body: JSON.stringify(req), timeoutMs: 60_000 }),
  format: (projectId, q) =>
    request<MediaFormatState>(`${base(projectId)}/format?${new URLSearchParams(q)}`, { live: true, timeoutMs: 60_000 }),
  startJob: (projectId, input) =>
    request<{ jobId: string }>(`${base(projectId)}/jobs`, { method: 'POST', body: jobForm(input), timeoutMs: 120_000 }),
  uploadSample: (projectId, file, name) => {
    const form = new FormData();
    form.append('file', file, name);
    return request<{ uploadId: string }>(`${base(projectId)}/uploads`, { method: 'POST', body: form, timeoutMs: 120_000 });
  },
  getJob: (projectId, jobId) =>
    request<ImageEditJob>(`${base(projectId)}/jobs/${encodeURIComponent(jobId)}`, { live: true }),
  cancelJob: (projectId, jobId) =>
    request<ImageEditJob>(`${base(projectId)}/jobs/${encodeURIComponent(jobId)}`, { method: 'DELETE' }),
  variantUrl: (projectId, jobId, n) =>
    withToken(`/api${base(projectId)}/jobs/${encodeURIComponent(jobId)}/variants/${n}`),
  save: (projectId, req) =>
    request<{ path: string }>(`${projectBase(projectId)}/save`, { method: 'POST', body: JSON.stringify(req) }),
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
  loras: projectId => request<ImageEditLoras>(`${base(projectId)}/loras`, { live: true }),
  characterPhotoUrl: (projectId, slug, file) =>
    withToken(`/api${charBase(projectId)}/${encodeURIComponent(slug)}/photos/${encodeURIComponent(file)}`),
  saveCheck: (projectId, req) => {
    const q = new URLSearchParams({ folder: req.folder ?? '', name: req.name });
    if (req.format) q.set('format', req.format);
    return request<SaveCheckResponse>(`${projectBase(projectId)}/save/check?${q}`, { live: true });
  },
  transform: (projectId, req, opts) =>
    request<ImageTransformResponse>(`${base(projectId)}/transform${opts?.dryRun ? '?dryRun=true' : ''}`,
      { method: 'POST', body: JSON.stringify(req), timeoutMs: 60_000 }),
  stepUrl: (projectId, stepId) => withToken(`/api${base(projectId)}/steps/${encodeURIComponent(stepId)}`),
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
  // Как LocalImageEditor на сервере: FLUX 3 Klein (умолчание), Qwen-Image без своего канала маски и FaceDetailer
  { key: 'local', label: 'Локальные модели', priceUnit: 'free', available: true, models: [
    AUTO_ITEM,
    { id: 'qwen-image-2.1-turbo', label: 'Qwen-Image 2.1 Turbo — быстро', caps: caps(['generate', 'edit', 'inpaint'], 'asReference'), priceHint: { amount: 0, unit: 'free', per: 'image' } },
    { id: 'flux3-klein', label: 'FLUX 3 Klein — текст и точная правка', caps: caps(['generate', 'edit', 'inpaint'], 'asReference'), priceHint: { amount: 0, unit: 'free', per: 'image' } },
    { id: 'qwen-image-2.1', label: 'Qwen-Image 2.1 — качественно', caps: caps(['generate', 'edit', 'inpaint'], 'asReference'), priceHint: { amount: 0, unit: 'free', per: 'image' } },
    { id: 'face-detailer', label: 'Улучшить лица', caps: { ...caps(['enhanceFaces'], 'none'), maxReferences: 0, maxCount: 1 }, priceHint: { amount: 0, unit: 'free', per: 'image' } },
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
  const r = transformedSize({ w: size.width, h: size.height }, ops);
  return { width: r.w, height: r.h };
}

const mockError = (message: string, status: number, body?: Record<string, unknown>) =>
  Object.assign(new Error(message), { status, body });

export function createMockApi(mode: 'fal' | 'all'): ImageEditorApi {
  const providers = mode === 'all' ? MOCK_PROVIDERS : MOCK_PROVIDERS.filter(p => p.key === 'fal');
  const listeners = new Set<(e: ImageEditEvent) => void>();
  const emit = (e: ImageEditEvent) => listeners.forEach(fn => fn(e));
  const quotes = new Map<string, ImageEditQuote & { count: number }>();
  const jobs = new Map<string, { job: ImageEditJob; timers: number[]; leaveQueue: () => void; runStartedAt?: number }>();
  let characters: { character: ImageEditCharacter; urls: Map<string, string> }[] = [];
  const delay = <T,>(v: T, ms = 150) => new Promise<T>(r => setTimeout(() => r(v), ms));
  let seq = 0;
  // Файлы, записанные «Сохранить как…»: повтор имени — 409 name_taken
  const savedPaths = new Set<string>();
  const steps = new Map<string, { width: number; height: number; format: ImageEncodeFormat }>();

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

  const resolveModel = (p: ImageEditProvider, model: string, op: ImageEditOp) =>
    model === AUTO_MODEL
      ? (p.models.find(m => m.caps?.ops.includes(op)) ?? p.models[1])
      : (p.models.find(m => m.id === model) ?? p.models[1]);
  // Очередь локальной видеокарты мока: растёт с каждым запуском и убывает с готовностью
  let localQueue = 0;

  return {
    catalog: () => delay<ImageEditCatalog>({
      // Умолчание мока — последний платный: локальные модели выбираются руками
      default: { provider: providers.filter(x => x.priceUnit !== 'free').at(-1)?.key ?? null, model: AUTO_MODEL },
      providers,
      limits: { maxFileMb: 20, maxReferences: 6, maxCount: 4, heavyFileMb: 5 },
      reason: providers.length ? null : 'no_provider_configured',
    }),
    format: async (projectId, q) => {
      // Желание мок читает из кэша префов устройства: запись префов оптимистична и пишет туда сразу
      let wish: MediaFormatWish | null = null;
      try { wish = (JSON.parse(localStorage.getItem(`cc-image-prefs:${projectId}`) ?? 'null') as { format?: MediaFormatWish | null } | null)?.format ?? null; } catch { /* мусор в хранилище */ }
      return delay(mockFormatState(q.model, q.op, wish), 80);
    },
    quote: async (_projectId, req) => {
      const p = providers.find(x => x.key === req.provider);
      if (!p) throw Object.assign(new Error('Поставщик рисования не настроен'), { status: 409, body: { code: 'provider_unavailable' } });
      const m = resolveModel(p, req.model, req.op);
      const per = m.priceHint?.amount ?? 0;
      const free = p.priceUnit === 'free';
      const seconds = free ? (req.op === 'enhanceFaces' ? 10 : 15 * req.count) : p.key === 'fal' ? 8 : 12;
      const quote: ImageEditQuote = {
        quoteId: `q${++seq}`, provider: p.key, model: m.id,
        estimate: free
          ? { amount: 0, unit: 'free', approx: false, source: 'provider', etaSeconds: seconds, queueLength: localQueue }
          : { amount: Math.round(per * req.count * mockTierFactor(m.id, req.op, req.format) * 100) / 100, unit: p.priceUnit, approx: true, source: 'catalog' },
        expiresAt: new Date(Date.now() + 10 * 60_000).toISOString(),
        expectedSeconds: seconds,
      };
      quotes.set(quote.quoteId, { ...quote, count: req.count });
      return delay(quote, p.key === 'fal' ? 120 : 300);
    },
    uploadSample: async () => delay({ uploadId: `up${++seq}` }),
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
      let queued = q.estimate.unit === 'free';
      if (queued) localQueue++;
      const leaveQueue = () => { if (queued) { queued = false; localQueue--; } };
      const entry: { job: ImageEditJob; timers: number[]; leaveQueue: () => void; runStartedAt?: number } =
        { job, timers: [], leaveQueue };
      jobs.set(jobId, entry);
      // «сломать» генерацию можно словом в запросе — так проверяется экран ошибки
      const failKind: EditOutcome | null = /кредит/i.test(input.prompt) ? 'insufficientCredits'
        : /ошибк/i.test(input.prompt) ? 'failed' : null;
      const total = (q.expectedSeconds ?? 8) * 1000;
      const at = (ms: number, fn: () => void) => entry.timers.push(window.setTimeout(fn, ms));
      // Локальные модели мока, как настоящие, рисуют варианты прогонами по одному, с ETA прогона
      const runs = q.estimate.unit === 'free' ? Math.max(1, q.count) : 1;
      const etaSeconds = Math.round((q.expectedSeconds ?? 8) / runs);
      job.estimate = q.estimate;
      for (let i = 0; i < runs; i++) {
        const run = i + 1;
        at(i === 0 ? 200 : (total * i) / runs, () => {
          Object.assign(job, { status: 'running', run, runs, etaSeconds });
          entry.runStartedAt = Date.now();
          emit({ type: 'image_edit_progress', jobId, projectId, stage: 'running', run, runs, etaSeconds, runElapsedSeconds: 0, ...origin });
        });
      }
      if (failKind) {
        at(total / 2, () => {
          leaveQueue();
          Object.assign(job, { status: 'failed', outcome: failKind, charged: false,
            error: failKind === 'failed' ? 'Сервис не ответил' : 'Не хватает кредитов' });
          emit({ type: 'image_edit_failed', jobId, projectId, outcome: failKind, charged: false, error: job.error, ...origin });
        });
      } else {
        at(total * 0.95, () => {
          job.status = 'downloading';
          emit({ type: 'image_edit_progress', jobId, projectId, stage: 'downloading', run: runs, runs, etaSeconds, ...origin });
        });
        at(total, () => {
          leaveQueue();
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
      // Как бэкенд: «прошло» считается в момент ответа и только в running
      const runElapsedSeconds = e.job.status === 'running' && e.runStartedAt != null
        ? Math.floor((Date.now() - e.runStartedAt) / 1000) : null;
      return delay({ ...e.job, runElapsedSeconds }, 50);
    },
    cancelJob: async (_projectId, jobId) => {
      const e = jobs.get(jobId);
      if (!e) throw Object.assign(new Error('Задача не найдена'), { status: 404, body: { code: 'job_not_found' } });
      e.timers.forEach(t => clearTimeout(t));
      e.leaveQueue();
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
      return delay({ path });
    },
    saveCheck: async (_projectId, req) => delay(resolveAs(req.folder, req.name, req.format), 60),
    transform: async (_projectId, req, opts) => {
      if (req.ops.some(op => op.type === 'rotate' && ![90, 180, 270].includes(op.degrees)))
        throw mockError('Поворот только на 90, 180 или 270°', 400, { code: 'invalid_request' });
      // Вариант мока — плашка 640×400 (variantUrl), файл проекта — MOCK_SOURCE_SIZE
      const baseSize: { width: number; height: number; format?: ImageEncodeFormat } | undefined = req.base.stepId
        ? steps.get(req.base.stepId) : req.base.jobId ? { width: 640, height: 400 } : MOCK_SOURCE_SIZE;
      if (!baseSize) throw mockError('Шаг не найден', 404, { code: 'invalid_request' });
      const size = mockApplyOps(baseSize, req.ops);
      const bytes = mockEncodedBytes(size.width, size.height, req.encode);
      if (opts?.dryRun) return delay({ stepId: null, width: size.width, height: size.height, bytes }, 80);
      const stepId = `s${++seq}`;
      steps.set(stepId, { ...size, format: req.encode?.format ?? baseSize.format ?? 'png' });
      return delay({ stepId, ...size, bytes }, 200);
    },
    // Мок-шаг: плашка в размерах шага — пиксели мок не правит, геометрию видно по рамке
    stepUrl: (_projectId, stepId) => {
      const st = steps.get(stepId) ?? { width: 640, height: 400, format: 'png' as const };
      const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${st.width} ${st.height}"><rect width="${st.width}" height="${st.height}" fill="hsl(200,35%,72%)"/><text x="${st.width / 2}" y="${st.height / 2}" font-size="${Math.round(Math.min(st.width, st.height) / 8)}" text-anchor="middle" dominant-baseline="middle" fill="hsl(200,40%,25%)" font-family="sans-serif">${st.width}×${st.height} ${st.format}</text></svg>`;
      return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;
    },
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
      e.character = mockCharacter(slug, input, kept, e.urls, e.character.local);
      return delay(e.character, 300);
    },
    deleteCharacter: async (_projectId, slug) => {
      characters = characters.filter(c => c.character.slug !== slug);
      return delay(undefined);
    },
    loras: () => delay({ available: true, items: ['anna_face_v1.safetensors', 'anna_face_v2.safetensors'] }),
    characterPhotoUrl: (_projectId, slug, file) =>
      characters.find(c => c.character.slug === slug)?.urls.get(file) ?? '',
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

function mockCharacter(slug: string, input: ImageEditCharacterInput, kept: ImageEditCharacterPhoto[], urls: Map<string, string>,
  prevLocal: ImageEditCharacter['local'] = null): ImageEditCharacter {
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
    local: input.lora === 'remove' ? null
      : input.lora ? { lora: input.lora.name, trigger: input.lora.trigger, strength: input.lora.strength } : prevLocal,
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
