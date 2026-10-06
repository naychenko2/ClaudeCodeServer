// Входы генерации из левой панели редактора v2: образцы с ролями, быстрые действия и
// история шагов. Чистые функции — их держат тесты editorInputs.test.ts.

import {
  AUTO_MODEL,
  type ImageEditCatalog, type ImageEditJobInput, type ImageEditModel, type ImageEditOp, type ImageEditPriceHint,
  type ImageEditProjectReference, type ImageEditProvider, type ImageEditUploadedReference, type ImageTransformBase,
  type ReferenceRole,
} from './api';
import { isFreeUnit, priceSum } from './format';

// ── Образцы ──

// Образец с компьютера несёт байты, из проекта — только путь: файл читает сервер
export type Sample =
  | { id: string; source: 'upload'; name: string; role: ReferenceRole; file: Blob; url: string }
  | { id: string; source: 'project'; name: string; role: ReferenceRole; path: string; url: string };

// [роль, полное название, короткое — под миниатюрой]
export const SAMPLE_ROLES: [ReferenceRole, string, string][] = [
  ['character', 'Персонаж — сохранить лицо', 'Лицо'],
  ['style', 'Стиль', 'Стиль'],
  ['object', 'Предмет', 'Предмет'],
];

// Потолок образцов: лимит сервера и, у явно выбранной модели, её собственный
export function maxSamples(serverLimit: number, modelLimit: number | null | undefined): number {
  return modelLimit != null ? Math.min(serverLimit, modelLimit) : serverLimit;
}

// Образцы во вход задачи: байты — в references, пути — в referencePaths; роль едет с каждым
export function samplesToJobInput(samples: Sample[]): {
  references: ImageEditUploadedReference[];
  referencePaths: ImageEditProjectReference[];
} {
  const references: ImageEditUploadedReference[] = [];
  const referencePaths: ImageEditProjectReference[] = [];
  for (const s of samples) {
    if (s.source === 'upload') references.push({ file: s.file, name: s.name, role: s.role });
    else referencePaths.push({ path: s.path, role: s.role });
  }
  return { references, referencePaths };
}

// Образцы из состояния агента: в нём только образцы из проекта, так что его список — полная
// правда о них. Образца проекта нет у агента — убран; новый — дописывается в конец. Образцы
// с компьютера агент не видит и не трогает, порядок у пользователя сохраняется
export function applyAgentReferences(
  list: Sample[],
  refs: ImageEditProjectReference[],
  make: (ref: ImageEditProjectReference) => Sample,
): Sample[] {
  const kept = list.flatMap((s): Sample[] => {
    if (s.source === 'upload') return [s];
    const r = refs.find(x => x.path === s.path);
    return r ? [{ ...s, role: r.role }] : [];
  });
  const added = refs.filter(r => !list.some(s => s.source === 'project' && s.path === r.path)).map(make);
  return [...kept, ...added];
}

// ── Быстрые действия ──

export type QuickAction = 'removeBackground' | 'upscale' | 'removeMarked' | 'outpaint' | 'enhanceFaces';
export const QUICK_ACTIONS: QuickAction[] = ['removeBackground', 'upscale', 'removeMarked', 'outpaint', 'enhanceFaces'];
export const OUTPAINT_RATIOS = ['1:1', '16:9', '9:16'] as const;
export type OutpaintRatio = typeof OUTPAINT_RATIOS[number];

export const QUICK_LABEL: Record<QuickAction, string> = {
  removeBackground: 'Убрать фон',
  upscale: 'Улучшить качество',
  removeMarked: 'Убрать отмеченное',
  outpaint: 'Дорисовать за края',
  enhanceFaces: 'Улучшить лица',
};

// Что делал запуск — для заголовка шага истории и «Ещё варианты»
export type LaunchAction = { kind: 'prompt'; prompt: string } | { kind: QuickAction; ratio?: OutpaintRatio };

export interface LaunchPlan {
  op: ImageEditOp;
  prompt: string;
  // Маска уходит только туда, где она и есть задача
  useMask: boolean;
  removal: boolean;
  aspectRatio?: OutpaintRatio;
}

// Быстрое действие запускается без промпта: операцию сервер берёт из котировки.
// «Убрать отмеченное» — это инпейнт с намерением стереть: слова дают серверу то же
// намерение, что и запрос человека «убери»
export function quickPlan(action: QuickAction, ratio: OutpaintRatio): LaunchPlan {
  switch (action) {
    case 'removeBackground': return { op: 'removeBackground', prompt: '', useMask: false, removal: false };
    case 'upscale': return { op: 'upscale', prompt: '', useMask: false, removal: false };
    case 'removeMarked': return { op: 'inpaint', prompt: 'Убрать отмеченное', useMask: true, removal: true };
    case 'outpaint': return { op: 'outpaint', prompt: '', useMask: false, removal: false, aspectRatio: ratio };
    case 'enhanceFaces': return { op: 'enhanceFaces', prompt: '', useMask: false, removal: false };
  }
}

// «Улучшить лица» — модель одного действия: её подбирает сервер, выбранная модель
// поставщика тут ни при чём, и вариант всегда один
export const quickUsesOwnModel = (action: QuickAction) => action === 'enhanceFaces';

// Чем запускать быстрое действие: поставщик, модель, умеющая операцию, и число вариантов
// в её пределах. model = auto — каталог не знает возможностей моделей, решает сервер
export interface QuickRoute {
  provider: string;
  providerLabel: string;
  model: string;
  count: number;
  // Сколько образцов модель принимает; null — неизвестно
  maxReferences: number | null;
  priceHint: ImageEditPriceHint | null;
}

// route — чем запускать; нет его — reason, почему нельзя, и fallback — другой
// поставщик, который умеет (только на этот запуск, выбор в полосе не меняется)
export interface QuickAvailability { route: QuickRoute | null; reason: string; fallback: QuickRoute | null }

// «Локальные модели не умеют дорисовку за края»
const QUICK_SKILL: Record<QuickAction, string> = {
  removeBackground: 'убирать фон',
  upscale: 'улучшать качество',
  removeMarked: 'стирать отмеченное',
  outpaint: 'дорисовку за края',
  enhanceFaces: 'улучшать лица',
};

const cannot = (label: string) => (/модели$/i.test(label) ? 'не умеют' : 'не умеет');

// Модель поставщика под операцию: выбранная, если умеет, иначе первая умеющая (как «Авто»
// внутри поставщика). Возможности хоть одной модели неизвестны — «Авто», решит сервер
function pickQuickModel(p: ImageEditProvider, preferId: string, op: ImageEditOp): ImageEditModel | null {
  const models = p.models.filter(m => m.id !== AUTO_MODEL);
  const preferred = models.find(m => m.id === preferId && m.caps?.ops.includes(op));
  if (preferred) return preferred;
  if (models.some(m => !m.caps)) return { id: AUTO_MODEL, label: 'Авто' };
  return models.find(m => m.caps!.ops.includes(op)) ?? null;
}

function toRoute(p: ImageEditProvider, m: ImageEditModel, action: QuickAction, count: number): QuickRoute {
  const max = m.caps?.maxCount ?? count;
  return {
    provider: p.key, providerLabel: p.label, model: m.id,
    count: quickUsesOwnModel(action) ? 1 : Math.max(1, Math.min(count, max)),
    maxReferences: m.caps ? m.caps.maxReferences : null,
    priceHint: m.priceHint ?? null,
  };
}

// Правило доступности быстрого действия ДО запуска — по возможностям моделей каталога
export function quickAvailability(
  action: QuickAction, catalog: ImageEditCatalog | null, providerKey: string | null, modelId: string, count: number,
): QuickAvailability {
  const pv = catalog?.providers.find(p => p.key === providerKey) ?? null;
  if (!catalog || !pv) return { route: null, reason: 'Рисовать нечем: поставщик картинок не настроен', fallback: null };
  const op = quickPlan(action, '1:1').op;
  const own = pickQuickModel(pv, modelId, op);
  if (own) return { route: toRoute(pv, own, action, count), reason: '', fallback: null };
  const reason = `${pv.label} ${cannot(pv.label)} ${QUICK_SKILL[action]}`;
  for (const p of catalog.providers) {
    if (p.key === pv.key || p.available === false) continue;
    const m = pickQuickModel(p, AUTO_MODEL, op);
    if (m) return { route: null, reason, fallback: toRoute(p, m, action, count) };
  }
  return { route: null, reason, fallback: null };
}

// «Взять fal · ≈ $0.04»: ориентир из каталога, до котировки — платный запуск не начинается
export function fallbackLabel(r: QuickRoute): string {
  const h = r.priceHint;
  const price = !h ? null : isFreeUnit(h.unit) ? 'бесплатно' : priceSum(h.amount * r.count, h.unit, true);
  return price ? `Взять ${r.providerLabel} · ${price}` : `Взять ${r.providerLabel}`;
}

// Действие, которого не умеет ни один поставщик каталога, не показывается вовсе
export const quickOffered = (action: QuickAction, catalog: ImageEditCatalog | null) =>
  !catalog || catalog.providers.some(p => pickQuickModel(p, AUTO_MODEL, quickPlan(action, '1:1').op));

export function actionTitle(a: LaunchAction): string {
  if (a.kind === 'prompt') {
    const p = a.prompt.trim();
    return p ? (p.length > 40 ? `${p.slice(0, 40)}…` : p) : 'Правка';
  }
  switch (a.kind) {
    case 'removeBackground': return 'Убран фон';
    case 'upscale': return 'Улучшено качество';
    case 'removeMarked': return 'Убрано отмеченное';
    case 'outpaint': return `Дорисовано до ${a.ratio ?? ''}`.trim();
    case 'enhanceFaces': return 'Улучшены лица';
  }
}

// ── История шагов ──

export interface HistoryStep {
  id: string;
  // Оригинал — файл, с которого начали; остальные — «Взять за основу» и правки без ИИ
  original: boolean;
  title: string;
  src: string;
  // База серверной правки (transform): файл проекта, вариант задачи или шаг. Пока шаг
  // в полёте — обещание его stepId. null — картинка не на сервере (загружена с компьютера)
  ready?: Promise<ImageTransformBase> | null;
  // Та же база, когда уже известна
  base?: ImageTransformBase | null;
  // Шаг правки ещё не записан сервером: на экране предпросмотр
  pending?: boolean;
  // Размеры и вес, если известны (у шага правки их отдаёт сервер)
  w?: number;
  h?: number;
  bytes?: number;
}

export type SaveSource = { jobId: string; variant: number } | { stepId: string };

// Что сохранять «Сохранить в проект» у текущего шага: вариант задачи или шаг правки.
// У оригинала и у шага в полёте сохранять нечего
export function stepSaveSource(s: HistoryStep | undefined): SaveSource | null {
  const b = s?.base;
  if (!s || s.original || s.pending || !b) return null;
  if (b.stepId) return { stepId: b.stepId };
  if (b.jobId) return { jobId: b.jobId, variant: b.variant };
  return null;
}

export interface History { steps: HistoryStep[]; cur: number }

export const EMPTY_HISTORY: History = { steps: [], cur: -1 };

// Новый шаг встаёт за текущим: шаги после него (после отката назад) отбрасываются
export function pushStep(h: History, step: HistoryStep): History {
  const steps = [...h.steps.slice(0, h.cur + 1), step];
  return { steps, cur: steps.length - 1 };
}

// Шаг не записался — убираем его и всё, что строилось поверх него
export function dropStepsFrom(h: History, id: string): History {
  const i = h.steps.findIndex(s => s.id === id);
  if (i < 0) return h;
  return { steps: h.steps.slice(0, i), cur: Math.min(h.cur, i - 1) };
}

export const patchStep = (h: History, id: string, patch: Partial<HistoryStep>): History =>
  ({ ...h, steps: h.steps.map(s => (s.id === id ? { ...s, ...patch } : s)) });

export const goToStep = (h: History, i: number): History =>
  (i >= 0 && i < h.steps.length ? { ...h, cur: i } : h);

// «Оригинал», «Шаг 1»… Без оригинала (нарисовано с нуля) нумерация с единицы
export function stepLabel(h: History, i: number): string {
  if (h.steps[i]?.original) return 'Оригинал';
  return `Шаг ${h.steps[0]?.original ? i : i + 1}`;
}

// Картинка текущего шага — база генерации: её байты уходят во вход задачи как source
export const currentSrc = (h: History): string | null => h.steps[h.cur]?.src ?? null;

// Из выбранного в левой панели — часть входа задачи
export function panelJobInput(samples: Sample[], plan: LaunchPlan): Pick<ImageEditJobInput, 'references' | 'referencePaths' | 'aspectRatio'> {
  const { references, referencePaths } = samplesToJobInput(samples);
  return {
    ...(references.length ? { references } : null),
    ...(referencePaths.length ? { referencePaths } : null),
    ...(plan.aspectRatio ? { aspectRatio: plan.aspectRatio } : null),
  };
}
