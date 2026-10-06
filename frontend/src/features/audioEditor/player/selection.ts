// Выделение куска на волне. Секунды; end = -1 — «до конца» (как end_seconds у
// «Перегенерировать кусок»): при смене длины версии такой кусок тянется за ней.
// Поле «Кусок» в панели и волна в ленте работают с одним и тем же значением.

export const TO_END = -1;

export interface AudioSelection {
  start: number;
  end: number;
}

// Шаг клавиатурной правки выделения, с: обычный и с Alt (тонкий)
export const KEY_STEP = 0.5;
export const KEY_STEP_FINE = 0.1;

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));

/** Конец куска в секундах: «до конца» разворачивается в длину версии. */
export function resolveEnd(sel: AudioSelection, duration: number): number {
  return sel.end === TO_END ? duration : sel.end;
}

/**
 * Обрезает выделение по длине версии: начало и конец в [0, duration], конец не
 * раньше начала. «До конца» сохраняется. Пустое (короче минимума) или целиком
 * вылезшее за длину выделение снимается — null.
 */
export function clampSelection(sel: AudioSelection | null, duration: number, minLen = 0.05): AudioSelection | null {
  if (!sel || !(duration > 0) || !Number.isFinite(sel.start) || !Number.isFinite(sel.end)) return null;
  const toEnd = sel.end === TO_END;
  let a = clamp(sel.start, 0, duration);
  let b = toEnd ? duration : clamp(sel.end, 0, duration);
  if (b < a) [a, b] = [b, a];
  if (b - a < minLen) return null;
  return { start: a, end: toEnd ? TO_END : b };
}

/** Выделение из протяжки мышью между двумя точками (в любом порядке). */
export function selectionFromDrag(from: number, to: number, duration: number): AudioSelection | null {
  return clampSelection({ start: Math.min(from, to), end: Math.max(from, to) }, duration);
}

export interface SelectionKey {
  key: string;
  shiftKey?: boolean;
  altKey?: boolean;
}

/**
 * Клавиатурная правка выделения. Возвращает новое выделение, null — снять,
 * undefined — клавиша к выделению не относится (её разберёт перемотка).
 *   Shift+← / Shift+→ — двигать конец (нет выделения — начать его от курсора);
 *   Shift+End — «до конца»; Shift+Home — начало куска в 0;
 *   [ и ] — начало и конец куска в позицию курсора; Escape — снять.
 * Alt уменьшает шаг до KEY_STEP_FINE.
 */
export function keySelection(
  sel: AudioSelection | null, e: SelectionKey, cursor: number, duration: number,
): AudioSelection | null | undefined {
  const step = e.altKey ? KEY_STEP_FINE : KEY_STEP;
  if (e.key === 'Escape') return sel ? null : undefined;
  if (e.key === '[') {
    const end = sel ? sel.end : TO_END;
    return clampSelection({ start: cursor, end }, duration);
  }
  if (e.key === ']') {
    const start = sel && sel.start < cursor ? sel.start : 0;
    return clampSelection({ start, end: cursor }, duration);
  }
  if (!e.shiftKey) return undefined;
  if (e.key === 'End') return clampSelection({ start: sel ? sel.start : cursor, end: TO_END }, duration);
  if (e.key === 'Home') return clampSelection({ start: 0, end: sel ? sel.end : cursor }, duration);
  if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
    const d = e.key === 'ArrowLeft' ? -step : step;
    if (!sel) return selectionFromDrag(cursor, cursor + d, duration);
    const end = resolveEnd(sel, duration) + d;
    // Конец ушёл за начало — кусок схлопывается в минимальный, а не переворачивается
    return clampSelection({ start: sel.start, end: Math.max(end, sel.start + step) }, duration);
  }
  return undefined;
}

/** «0:04.2» — минуты и секунды с десятыми, как в макете. */
export function fmtTime(sec: number): string {
  const s = Math.max(0, Number.isFinite(sec) ? sec : 0);
  const m = Math.floor(s / 60);
  // Округляем до десятых ДО разбиения на минуты: иначе 59.96 даёт «0:60.0»
  const tenths = Math.round((s - m * 60) * 10);
  if (tenths >= 600) return `${m + 1}:00.0`;
  return `${m}:${(tenths / 10).toFixed(1).padStart(4, '0')}`;
}

/** «0:06.2 – 0:11.8 · 5,6 с»; «до конца» — словами. */
export function fmtSelection(sel: AudioSelection, duration: number): string {
  const end = resolveEnd(sel, duration);
  const len = (end - sel.start).toFixed(1).replace('.', ',');
  return `${fmtTime(sel.start)} – ${sel.end === TO_END ? 'до конца' : fmtTime(end)} · ${len} с`;
}
