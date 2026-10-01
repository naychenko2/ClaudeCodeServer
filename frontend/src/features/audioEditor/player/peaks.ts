// Пики волны: по одному числу 0…1 на столбик. Сырые данные — каналы WebAudio-декода
// (useBrowserPeaks) или готовый массив пиков с сервера (шаг 2.8б).

/** Максимум модуля по корзинам из всех каналов, нормированный к самому громкому. */
export function computePeaks(channels: ArrayLike<number>[], count: number): number[] {
  const len = channels.reduce((m, c) => Math.max(m, c.length), 0);
  if (!len || count <= 0) return [];
  const out = new Array<number>(count).fill(0);
  for (let i = 0; i < count; i++) {
    const from = Math.floor((i * len) / count);
    const to = Math.max(from + 1, Math.floor(((i + 1) * len) / count));
    let peak = 0;
    for (const ch of channels) {
      for (let j = from; j < to && j < ch.length; j++) {
        const v = Math.abs(ch[j]);
        if (v > peak) peak = v;
      }
    }
    out[i] = peak;
  }
  return normalize(out);
}

/** Приводит массив пиков к нужному числу столбиков (максимум по корзине). */
export function resamplePeaks(peaks: number[], count: number): number[] {
  if (count <= 0 || !peaks.length) return [];
  if (peaks.length === count) return peaks;
  const out = new Array<number>(count);
  for (let i = 0; i < count; i++) {
    const from = Math.floor((i * peaks.length) / count);
    const to = Math.max(from + 1, Math.floor(((i + 1) * peaks.length) / count));
    let m = 0;
    for (let j = from; j < to; j++) m = Math.max(m, peaks[j] ?? 0);
    out[i] = m;
  }
  return out;
}

/**
 * Кладёт значение в кэш с потолком: Map помнит порядок вставки, поэтому свежее —
 * в конец (повторное чтение тоже переносит в конец), лишнее — с головы.
 */
export function lruGet<V>(cache: Map<string, V>, key: string): V | undefined {
  const v = cache.get(key);
  if (v !== undefined) { cache.delete(key); cache.set(key, v); }
  return v;
}

export function lruSet<V>(cache: Map<string, V>, key: string, value: V, max: number): void {
  cache.delete(key);
  cache.set(key, value);
  while (cache.size > max) cache.delete(cache.keys().next().value as string);
}

/**
 * Нормирует несколько волн общим максимумом: громкая и тихая версия (A/B, стемы)
 * остаются разными по высоте, но тихий файл не превращается в плоскую линию.
 */
export function normalizeJoint(list: (number[] | null | undefined)[]): (number[] | null)[] {
  const max = list.reduce<number>((m, a) => (a ? a.reduce((x, v) => Math.max(x, v), m) : m), 0);
  return list.map(a => (a ? (max > 0 ? a.map(v => v / max) : a) : null));
}

function normalize(arr: number[]): number[] {
  const max = arr.reduce((m, v) => Math.max(m, v), 0);
  return max > 0 ? arr.map(v => v / max) : arr;
}
