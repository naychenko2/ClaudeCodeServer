// Правки без ИИ (ADR-018 §9): растр делает сервер ручкой transform, фронт — только
// мгновенный предпросмотр. Здесь чистая логика: цепочка шагов, рамка обрезки, расчёт
// размера и веса. Её держит transforms.test.ts; рисование предпросмотра — внизу файла.

import type {
  ImageEncodeFormat, ImageEncodeSpec, ImageFractionRect, ImageTransformBase, ImageTransformOp,
  ImageTransformRequest, ImageTransformResponse,
} from './api';

export interface Dims { w: number; h: number }

// ── Цепочка шагов ──

// Пока шаг в полёте, следующая правка не ждёт человека: её база — обещание предыдущего
// шага. Запрос уходит, как только предыдущий шаг получил stepId; упал предыдущий — падает
// и этот (строить не на чем)
export function chainTransform(
  base: Promise<ImageTransformBase>,
  ops: ImageTransformOp[],
  encode: ImageEncodeSpec | null,
  run: (req: ImageTransformRequest) => Promise<ImageTransformResponse>,
): { result: Promise<ImageTransformResponse & { stepId: string }>; ready: Promise<ImageTransformBase> } {
  const result = base.then(b => run({ base: b, ops, ...(encode ? { encode } : null) })).then(r => {
    if (!r.stepId) throw new Error('Сервер не записал шаг правки');
    return { ...r, stepId: r.stepId };
  });
  const ready = result.then((r): ImageTransformBase => ({ stepId: r.stepId }));
  // Падение ловит тот, кто ждёт result; сама база следующих шагов не должна шуметь
  ready.catch(() => {});
  return { result, ready };
}

// Размеры после цепочки правок — так же посчитает сервер
export function transformedSize(size: Dims, ops: ImageTransformOp[]): Dims {
  let { w, h } = size;
  for (const op of ops) {
    if (op.type === 'crop') {
      w = Math.max(1, Math.round(w * op.rect.width));
      h = Math.max(1, Math.round(h * op.rect.height));
    } else if (op.type === 'rotate' && op.degrees !== 180) {
      [w, h] = [h, w];
    } else if (op.type === 'resize') {
      if (op.percent != null) {
        w = Math.max(1, Math.round(w * op.percent / 100));
        h = Math.max(1, Math.round(h * op.percent / 100));
      } else {
        const lock = op.lockAspect ?? true;
        const nw = op.width ?? (lock && op.height ? Math.round(w * op.height / h) : w);
        const nh = op.height ?? (lock && op.width ? Math.round(h * op.width / w) : h);
        [w, h] = [nw, nh];
      }
    }
  }
  return { w, h };
}

export function opTitle(op: ImageTransformOp, encode?: ImageEncodeSpec | null): string {
  switch (op.type) {
    case 'crop': return 'Обрезка';
    case 'rotate': return op.degrees === 270 ? 'Поворот влево' : op.degrees === 90 ? 'Поворот вправо' : 'Поворот на 180°';
    case 'flip': return op.axis === 'horizontal' ? 'Отражение по горизонтали' : 'Отражение по вертикали';
    case 'resize': return encode ? 'Размер и сжатие' : 'Размер';
    case 'autoOrient': return 'Ориентация';
  }
}

// ── Рамка обрезки ──

export const CROP_RATIOS = ['free', '1:1', '16:9', '9:16'] as const;
export type CropRatio = typeof CROP_RATIOS[number];
export type CropCorner = 'nw' | 'ne' | 'sw' | 'se';

const RATIO: Record<Exclude<CropRatio, 'free'>, number> = { '1:1': 1, '16:9': 16 / 9, '9:16': 9 / 16 };

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));
// Самая маленькая рамка — доля стороны: меньше её пальцем не ухватить
const MIN = 0.05;

// Отношение сторон рамки в ДОЛЯХ: w/h в пикселях равно r, а доли считаются от разных сторон
const fracRatio = (ratio: CropRatio, size: Dims) => (ratio === 'free' ? null : RATIO[ratio] * size.h / size.w);

// Стартовая рамка: при заданных пропорциях — самая большая по центру, иначе отступ 10 %
export function initialCrop(ratio: CropRatio, size: Dims): ImageFractionRect {
  const r = fracRatio(ratio, size);
  if (r == null) return { x: 0.1, y: 0.1, width: 0.8, height: 0.8 };
  const width = r >= 1 ? 1 : r;
  const height = r >= 1 ? 1 / r : 1;
  return { x: (1 - width) / 2, y: (1 - height) / 2, width, height };
}

// Перетаскивание рамки целиком: внутри картинки
export function moveCrop(rect: ImageFractionRect, dx: number, dy: number): ImageFractionRect {
  return { ...rect, x: clamp(rect.x + dx, 0, 1 - rect.width), y: clamp(rect.y + dy, 0, 1 - rect.height) };
}

// Тянем угол: противоположный стоит на месте. С пропорциями высота следует за шириной,
// а упёрлась в край — ширина ужимается под неё
export function resizeCrop(rect: ImageFractionRect, corner: CropCorner, px: number, py: number, ratio: CropRatio, size: Dims): ImageFractionRect {
  const ax = corner.includes('w') ? rect.x + rect.width : rect.x;
  const ay = corner.includes('n') ? rect.y + rect.height : rect.y;
  const sx = corner.includes('w') ? -1 : 1;
  const sy = corner.includes('n') ? -1 : 1;
  const maxW = sx > 0 ? 1 - ax : ax;
  const maxH = sy > 0 ? 1 - ay : ay;
  let w = clamp((px - ax) * sx, MIN, maxW);
  let h = clamp((py - ay) * sy, MIN, maxH);
  const r = fracRatio(ratio, size);
  if (r != null) {
    h = w / r;
    if (h > maxH) { h = maxH; w = h * r; }
    if (h < MIN) { h = MIN; w = Math.min(maxW, h * r); }
  }
  return { x: sx > 0 ? ax : ax - w, y: sy > 0 ? ay : ay - h, width: w, height: h };
}

// Сменили пропорции у открытой рамки — вписываем новую в центр прежней
export function fitCropRatio(rect: ImageFractionRect, ratio: CropRatio, size: Dims): ImageFractionRect {
  const r = fracRatio(ratio, size);
  if (r == null) return rect;
  let w = rect.width, h = w / r;
  if (h > rect.height) { h = rect.height; w = h * r; }
  const cx = rect.x + rect.width / 2, cy = rect.y + rect.height / 2;
  return { x: clamp(cx - w / 2, 0, 1 - w), y: clamp(cy - h / 2, 0, 1 - h), width: w, height: h };
}

// Рамка на всю картинку — обрезать нечего
export const isFullCrop = (r: ImageFractionRect) => r.x < 0.001 && r.y < 0.001 && r.width > 0.999 && r.height > 0.999;

// ── Размер и сжатие ──

// Пресеты по длинной стороне, плюс «50 %»
export const SIZE_PRESETS = [1920, 1280, 1080, 512] as const;

export const QUALITY_MIN = 40;
export const QUALITY_DEFAULT = 85;

// Сторона по длинной стороне пресета, короткая — по пропорциям
export function presetDims(size: Dims, longSide: number): Dims {
  const k = longSide / Math.max(size.w, size.h);
  return { w: Math.max(1, Math.round(size.w * k)), h: Math.max(1, Math.round(size.h * k)) };
}

// Поля px: введена одна сторона при замке — вторая по пропорциям исходника
export const lockedHeight = (size: Dims, w: number) => Math.max(1, Math.round(size.h * w / size.w));
export const lockedWidth = (size: Dims, h: number) => Math.max(1, Math.round(size.w * h / size.h));

export interface SizeForm {
  unit: 'px' | '%';
  w: number;
  h: number;
  percent: number;
  format: ImageEncodeFormat;
  quality: number;
}

// Операции формы: ресайз, только если размер и правда другой; кодирование — всегда
// (формат и качество — сама суть «сжатия»)
export function sizeFormOps(size: Dims, f: SizeForm): { ops: ImageTransformOp[]; encode: ImageEncodeSpec; target: Dims } {
  const target = f.unit === '%'
    ? { w: Math.max(1, Math.round(size.w * f.percent / 100)), h: Math.max(1, Math.round(size.h * f.percent / 100)) }
    : { w: Math.max(1, Math.round(f.w)), h: Math.max(1, Math.round(f.h)) };
  const resized = target.w !== size.w || target.h !== size.h;
  const ops: ImageTransformOp[] = !resized ? []
    : f.unit === '%' ? [{ type: 'resize', percent: f.percent }]
      : [{ type: 'resize', width: target.w, height: target.h, lockAspect: false }];
  const encode: ImageEncodeSpec = f.format === 'png' ? { format: 'png' } : { format: f.format, quality: f.quality };
  return { ops, encode, target };
}

// Форма ничего не меняет — применять нечего
export function sizeFormChanged(size: Dims, f: SizeForm, sourceFormat: ImageEncodeFormat | null): boolean {
  const { ops } = sizeFormOps(size, f);
  return ops.length > 0 || f.format !== sourceFormat || (f.format !== 'png' && f.quality !== QUALITY_DEFAULT);
}

export function formatOf(mime: string): ImageEncodeFormat | null {
  if (/png/i.test(mime)) return 'png';
  if (/jpe?g/i.test(mime)) return 'jpeg';
  if (/webp/i.test(mime)) return 'webp';
  return null;
}

// «2,4 МБ», «310 КБ», «900 Б»
export function formatBytes(n: number): string {
  if (n < 1024) return `${n} Б`;
  if (n < 1024 * 1024) return `${Math.round(n / 1024)} КБ`;
  const mb = n / 1024 / 1024;
  return `${(mb < 10 ? Math.round(mb * 10) / 10 : Math.round(mb)).toString().replace('.', ',')} МБ`;
}

// ── Дебаунс веса ──

// Последний вызов побеждает: fn зовётся не раньше чем через ms после последнего вызова и
// не чаще раза в ms. cancel — уходя, не дёргать сервер
export function debounceLatest<A extends unknown[]>(fn: (...args: A) => void, ms: number): { call: (...args: A) => void; cancel: () => void } {
  let t: ReturnType<typeof setTimeout> | null = null;
  return {
    call: (...args: A) => {
      if (t) clearTimeout(t);
      t = setTimeout(() => { t = null; fn(...args); }, ms);
    },
    cancel: () => { if (t) clearTimeout(t); t = null; },
  };
}

// Вес «2,4 МБ → 310 КБ» через dryRun: запрос уходит, только когда форма 300 мс не
// менялась; ответ на устаревший запрос отбрасывается (key — содержимое формы)
export const WEIGHT_DEBOUNCE_MS = 300;

export function weightEstimator(
  run: (base: ImageTransformBase, ops: ImageTransformOp[], encode: ImageEncodeSpec) => Promise<ImageTransformResponse>,
  onResult: (key: string, bytes: number | null, error: string | null) => void,
  ms: number = WEIGHT_DEBOUNCE_MS,
) {
  let latest = '';
  const d = debounceLatest((base: Promise<ImageTransformBase>, key: string, ops: ImageTransformOp[], encode: ImageEncodeSpec) => {
    base.then(b => run(b, ops, encode))
      .then(r => { if (key === latest) onResult(key, r.bytes, null); })
      .catch((e: Error) => { if (key === latest) onResult(key, null, e.message); });
  }, ms);
  return {
    request: (base: Promise<ImageTransformBase>, key: string, ops: ImageTransformOp[], encode: ImageEncodeSpec) => {
      latest = key;
      d.call(base, key, ops, encode);
    },
    cancel: () => { latest = ''; d.cancel(); },
  };
}

// ── Предпросмотр ──

// Потолок стороны предпросмотра: экранное разрешение, не исходник (48 Мп на телефоне
// в canvas не влезают)
const PREVIEW_MAX = 1600;

// Мгновенная картинка шага: операция поверх того, что на экране сейчас. null — выглядит
// так же, как было (ресайз и сжатие), показываем прежнюю картинку
export async function renderPreview(img: HTMLImageElement, op: ImageTransformOp): Promise<string | null> {
  if (op.type !== 'crop' && op.type !== 'rotate' && op.type !== 'flip') return null;
  const nw = img.naturalWidth, nh = img.naturalHeight;
  if (!nw || !nh) return null;
  const src = op.type === 'crop'
    ? { x: op.rect.x * nw, y: op.rect.y * nh, w: op.rect.width * nw, h: op.rect.height * nh }
    : { x: 0, y: 0, w: nw, h: nh };
  const k = Math.min(1, PREVIEW_MAX / Math.max(src.w, src.h));
  const dw = Math.max(1, Math.round(src.w * k)), dh = Math.max(1, Math.round(src.h * k));
  const turned = op.type === 'rotate' && op.degrees !== 180;
  const canvas = document.createElement('canvas');
  canvas.width = turned ? dh : dw;
  canvas.height = turned ? dw : dh;
  const ctx = canvas.getContext('2d');
  if (!ctx) return null;
  ctx.translate(canvas.width / 2, canvas.height / 2);
  if (op.type === 'rotate') ctx.rotate(op.degrees * Math.PI / 180);
  if (op.type === 'flip') ctx.scale(op.axis === 'horizontal' ? -1 : 1, op.axis === 'vertical' ? -1 : 1);
  ctx.drawImage(img, src.x, src.y, src.w, src.h, -dw / 2, -dh / 2, dw, dh);
  const blob = await new Promise<Blob | null>(r => canvas.toBlob(r, 'image/png'));
  return blob ? URL.createObjectURL(blob) : null;
}
