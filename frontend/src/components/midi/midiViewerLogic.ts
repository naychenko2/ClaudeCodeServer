// Чистая логика просмотрщика нот: формат времени, масштаб, заглушение и соло.

export const MIN_PX_PER_SEC = 8;
export const MAX_PX_PER_SEC = 800;
export const ZOOM_STEP = 1.5;
const DEFAULT_PX_PER_SEC = 100;

// Секунды → «m:ss»
export function formatTime(sec: number): string {
  const total = Math.max(0, Math.floor(Number.isFinite(sec) ? sec : 0));
  const m = Math.floor(total / 60);
  const s = total % 60;
  return `${m}:${s < 10 ? '0' : ''}${s}`;
}

export const clampPxPerSec = (px: number) => Math.min(MAX_PX_PER_SEC, Math.max(MIN_PX_PER_SEC, px));

// Начальный масштаб: трек целиком влезает в ширину ленты за вычетом клавиатуры.
// Ширина берётся по целым пикселям вниз: дробная вёрстка иначе даёт лишний пиксель прокрутки
export function fitPxPerSec(viewW: number, keyW: number, durationSec: number): number {
  const availW = Math.floor(viewW) - keyW;
  if (!(availW > 0) || !(durationSec > 0)) return DEFAULT_PX_PER_SEC;
  return clampPxPerSec(availW / durationSec);
}

export const zoomPxPerSec = (px: number, dir: 1 | -1) =>
  clampPxPerSec(dir > 0 ? px * ZOOM_STEP : px / ZOOM_STEP);

export function toggleMute(muted: Set<number>, index: number): Set<number> {
  const next = new Set(muted);
  if (next.has(index)) next.delete(index);
  else next.add(index);
  return next;
}

// Соло = звучит только эта дорожка, остальные заглушены
export function isSolo(indices: number[], muted: Set<number>, index: number): boolean {
  if (indices.length < 2 || muted.has(index)) return false;
  return indices.every(i => i === index || muted.has(i));
}

// Повторное соло на той же дорожке снимает заглушение со всех
export function toggleSolo(indices: number[], muted: Set<number>, index: number): Set<number> {
  if (isSolo(indices, muted, index)) return new Set();
  return new Set(indices.filter(i => i !== index));
}

// Догонять ли playhead прокруткой. Сам по себе — только если он был виден и ушёл
// за край (ручную прокрутку во время игры не перебиваем); по явному действию
// (старт, переход, «В начало») — всегда, когда он вне окна
export function shouldFollowPlayhead(
  prevSec: number, sec: number, [t0, t1]: [number, number], force: boolean,
): boolean {
  if (sec >= t0 && sec <= t1) return false;
  return force || (prevSec >= t0 && prevSec <= t1);
}
