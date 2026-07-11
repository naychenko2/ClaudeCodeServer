// Геометрия кропа аватара: круглое окно поверх картинки с зумом и панорамой.
// Модель: масштаб scale (1..4) поверх cover-вписывания в квадратное окно;
// offsetX/offsetY — смещение центра окна от центра картинки В ПИКСЕЛЯХ ИСХОДНИКА.
// computeCropRect возвращает квадрат исходника, который надо отрисовать в canvas
// outSize×outSize (drawImage(img, sx, sy, sSize, sSize, 0, 0, outSize, outSize)).

export interface CropRect {
  sx: number;
  sy: number;
  sSize: number;
  outSize: number;
}

export const MIN_SCALE = 1;
export const MAX_SCALE = 4;

function clamp(v: number, min: number, max: number): number {
  return Math.min(Math.max(v, min), max);
}

// Кламп смещения: окно кропа не должно выходить за края картинки.
// Допустимое |offset| по оси = (размер картинки - размер окна в исходнике) / 2.
export function clampOffset(
  imgW: number, imgH: number, scale: number, offsetX: number, offsetY: number,
): { offsetX: number; offsetY: number } {
  const s = clamp(scale, MIN_SCALE, MAX_SCALE);
  const sSize = Math.min(imgW, imgH) / s;
  const maxX = Math.max(0, (imgW - sSize) / 2);
  const maxY = Math.max(0, (imgH - sSize) / 2);
  // «+ 0» нормализует -0 → 0 (clamp отрицательного к нулевой границе даёт -0)
  return { offsetX: clamp(offsetX, -maxX, maxX) + 0, offsetY: clamp(offsetY, -maxY, maxY) + 0 };
}

// Квадрат исходника под текущие масштаб/смещение (смещение предварительно клампится)
export function computeCropRect(
  imgW: number, imgH: number, scale: number, offsetX: number, offsetY: number, outSize: number,
): CropRect {
  const s = clamp(scale, MIN_SCALE, MAX_SCALE);
  const sSize = Math.min(imgW, imgH) / s;
  const { offsetX: ox, offsetY: oy } = clampOffset(imgW, imgH, s, offsetX, offsetY);
  const sx = clamp(imgW / 2 + ox - sSize / 2, 0, Math.max(0, imgW - sSize));
  const sy = clamp(imgH / 2 + oy - sSize / 2, 0, Math.max(0, imgH - sSize));
  return { sx, sy, sSize, outSize };
}
