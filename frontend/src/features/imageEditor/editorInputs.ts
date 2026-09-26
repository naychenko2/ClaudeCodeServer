// Входы генерации из левой панели редактора v2: образцы с ролями, быстрые действия и
// история шагов. Чистые функции — их держат тесты editorInputs.test.ts.

import type {
  ImageEditJobInput, ImageEditOp, ImageEditProjectReference, ImageEditUploadedReference, ReferenceRole,
} from '../../api/imageEditor';

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

export const roleShort = (r: ReferenceRole) => SAMPLE_ROLES.find(x => x[0] === r)?.[2] ?? r;

const IMAGE_EXT = /\.(png|jpe?g|webp)$/i;
export const isImagePath = (path: string) => IMAGE_EXT.test(path);

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

// ── Быстрые действия ──

export type QuickAction = 'removeBackground' | 'upscale' | 'removeMarked' | 'outpaint';
export const OUTPAINT_RATIOS = ['1:1', '16:9', '9:16'] as const;
export type OutpaintRatio = typeof OUTPAINT_RATIOS[number];

export const QUICK_LABEL: Record<QuickAction, string> = {
  removeBackground: 'Убрать фон',
  upscale: 'Улучшить качество',
  removeMarked: 'Убрать отмеченное',
  outpaint: 'Дорисовать за края',
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
  }
}

// Почему быстрое действие недоступно (пусто — доступно)
export function quickBlockReason(action: QuickAction, hasImage: boolean, hasMask: boolean, modelOps: ImageEditOp[] | null): string {
  if (!hasImage) return 'Сначала загрузите картинку';
  if (action === 'removeMarked' && !hasMask) return 'Сначала отметьте кистью, что убрать';
  const op = quickPlan(action, '1:1').op;
  if (modelOps && !modelOps.includes(op)) return 'Выбранная модель так не умеет — возьмите «Авто» или другую';
  return '';
}

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
  }
}

// ── История шагов ──

export interface HistoryStep {
  id: string;
  // Оригинал — файл, с которого начали; остальные — «Взять за основу»
  original: boolean;
  title: string;
  src: string;
}

export interface History { steps: HistoryStep[]; cur: number }

export const EMPTY_HISTORY: History = { steps: [], cur: -1 };

// Новый шаг встаёт за текущим: шаги после него (после отката назад) отбрасываются
export function pushStep(h: History, step: HistoryStep): History {
  const steps = [...h.steps.slice(0, h.cur + 1), step];
  return { steps, cur: steps.length - 1 };
}

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
