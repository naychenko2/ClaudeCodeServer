// API редактора картинок (ADR-016). Контракт — DTO и маршруты из
// backend/ClaudeHomeServer.Core/Services/ImageEditor (волна 1), база
// api/projects/{projectId}/image-editor. Enum'ы приходят строками в camelCase.
//
// Мок-режим: пока драйверов поставщиков нет, живой сервер отдаёт пустой каталог и
// 409 на запуск. Для работы экрана без драйверов в localStorage ставится
// `cc-image-editor-mock`: `fal` — в каталоге только fal, `all` — fal и Higgsfield.
// Мок повторяет контракт целиком, включая SignalR-события задачи.

import { request, readStoredToken } from '../lib/offline';
import { onMessage } from '../lib/signalr';

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
}

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
}

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
}

// Персонаж — папка characters/<slug>/ в проекте (ADR-016, раздел 10)
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

export interface ImageEditSaveRequest {
  jobId: string;
  variant: number;
  sourcePath?: string;
  folder?: string;
  fileName?: string;
}

// SignalR-события задачи (группа владельца, событие message)
export type ImageEditEvent =
  | { type: 'image_edit_progress'; jobId: string; projectId: string; stage: EditStage; queuePosition?: number | null }
  | { type: 'image_edit_completed'; jobId: string; projectId: string; variants: number[]; cost?: EditCost | null }
  | { type: 'image_edit_failed'; jobId: string; projectId: string; outcome: EditOutcome; charged?: boolean | null; error?: string | null; retryQuote?: ImageEditQuote | null };

// Коды ошибок ручек: { error, code }
export type ImageEditErrorCode =
  | 'provider_unavailable' | 'invalid_request' | 'quote_not_found'
  | 'job_not_found' | 'too_many_jobs' | 'image_editor_unavailable';

export function imageEditErrorCode(e: unknown): ImageEditErrorCode | null {
  const body = (e as { body?: { code?: unknown } } | null)?.body;
  return typeof body?.code === 'string' ? body.code as ImageEditErrorCode : null;
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

const IMAGE_EDIT_EVENTS = new Set(['image_edit_progress', 'image_edit_completed', 'image_edit_failed']);

const liveApi: ImageEditorApi = {
  catalog: projectId => request<ImageEditCatalog>(`${base(projectId)}/catalog`, { live: true }),
  quote: (projectId, req) =>
    request<ImageEditQuote>(`${base(projectId)}/quote`, { method: 'POST', body: JSON.stringify(req), timeoutMs: 60_000 }),
  startJob: (projectId, input) => {
    const form = new FormData();
    form.append('quoteId', input.quoteId);
    form.append('prompt', input.prompt);
    if (input.marks) form.append('marks', input.marks);
    if (input.sourcePath) form.append('sourcePath', input.sourcePath);
    if (input.source) form.append('source', input.source, 'source');
    if (input.mask) form.append('mask', input.mask, 'mask.png');
    if (input.annotated) form.append('annotated', input.annotated, 'annotated.png');
    if (input.characterSlug) form.append('characterSlug', input.characterSlug);
    return request<{ jobId: string }>(`${base(projectId)}/jobs`, { method: 'POST', body: form, timeoutMs: 120_000 });
  },
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

function createMockApi(mode: 'fal' | 'all'): ImageEditorApi {
  const providers = mode === 'all' ? MOCK_PROVIDERS : MOCK_PROVIDERS.filter(p => p.key === 'fal');
  const listeners = new Set<(e: ImageEditEvent) => void>();
  const emit = (e: ImageEditEvent) => listeners.forEach(fn => fn(e));
  const quotes = new Map<string, ImageEditQuote & { count: number }>();
  const jobs = new Map<string, { job: ImageEditJob; timers: number[] }>();
  let characters: { character: ImageEditCharacter; urls: Map<string, string> }[] = [];
  const delay = <T,>(v: T, ms = 150) => new Promise<T>(r => setTimeout(() => r(v), ms));
  let seq = 0;

  const resolveModel = (p: ImageEditProvider, model: string) =>
    model === AUTO_MODEL ? p.models[1] : (p.models.find(m => m.id === model) ?? p.models[1]);

  return {
    catalog: () => delay<ImageEditCatalog>({
      default: { provider: providers.at(-1)?.key ?? null, model: AUTO_MODEL },
      providers,
      limits: { maxFileMb: 20, maxReferences: 6, maxCount: 4 },
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
      };
      const entry = { job, timers: [] as number[] };
      jobs.set(jobId, entry);
      // «сломать» генерацию можно словом в запросе — так проверяется экран ошибки
      const failKind: EditOutcome | null = /кредит/i.test(input.prompt) ? 'insufficientCredits'
        : /ошибк/i.test(input.prompt) ? 'failed' : null;
      const total = (q.expectedSeconds ?? 8) * 1000;
      const at = (ms: number, fn: () => void) => entry.timers.push(window.setTimeout(fn, ms));
      at(200, () => { job.status = 'running'; emit({ type: 'image_edit_progress', jobId, projectId, stage: 'running' }); });
      if (failKind) {
        at(total / 2, () => {
          Object.assign(job, { status: 'failed', outcome: failKind, charged: false,
            error: failKind === 'failed' ? 'Сервис не ответил' : 'Не хватает кредитов' });
          emit({ type: 'image_edit_failed', jobId, projectId, outcome: failKind, charged: false, error: job.error });
        });
      } else {
        at(total * 0.85, () => { job.status = 'downloading'; emit({ type: 'image_edit_progress', jobId, projectId, stage: 'downloading' }); });
        at(total, () => {
          const cost = q.estimate.amount != null ? { amount: q.estimate.amount, unit: q.estimate.unit } : null;
          Object.assign(job, { status: 'completed', outcome: 'ok', charged: true, cost,
            variants: Array.from({ length: q.count }, (_, i) => i) });
          emit({ type: 'image_edit_completed', jobId, projectId, variants: job.variants, cost });
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
      if (!jobs.has(req.jobId)) throw Object.assign(new Error('Задача не найдена'), { status: 404, body: { code: 'job_not_found' } });
      const path = req.sourcePath
        ? req.sourcePath.replace(/(?:\.v\d+)?\.(\w+)$/, '.v2.png')
        : `${req.folder ? `${req.folder}/` : ''}${req.fileName ?? 'новая-картинка.png'}`;
      return delay({ path });
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
