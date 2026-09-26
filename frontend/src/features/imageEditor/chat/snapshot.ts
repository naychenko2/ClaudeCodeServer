// Снимок холста для сообщения в чат картинки (ADR-018 §3): размеченная копия, а без
// стрелок, рамок и подписей — сама картинка (exportAnnotated в этом случае отдаёт null).
// Ужимается до SNAPSHOT_MAX_SIDE по длинной стороне: больше модели не нужно, а лишние
// мегабайты base64 оседают в транскрипте на каждый --resume.
//
// Прикладывается, только когда холст изменился: признак — canvasRevision.

import { exportAnnotated, marksToJson, type Mark } from '../marks';

export const SNAPSHOT_MAX_SIDE = 1568;

// Размер снимка: длинная сторона не больше max, пропорции сохраняются
export function snapshotSize(w: number, h: number, max = SNAPSHOT_MAX_SIDE): { w: number; h: number } {
  const k = Math.min(1, max / Math.max(w, h));
  return { w: Math.max(1, Math.round(w * k)), h: Math.max(1, Math.round(h * k)) };
}

export async function canvasSnapshot(img: HTMLImageElement, marks: Mark[], w: number, h: number): Promise<Blob | null> {
  const annotated = await exportAnnotated(img, marks, w, h).catch(() => null);
  const source: CanvasImageSource | null = annotated ? await createImageBitmap(annotated).catch(() => null) : img;
  if (!source) return null;
  const out = snapshotSize(w, h);
  const canvas = document.createElement('canvas');
  canvas.width = out.w;
  canvas.height = out.h;
  const ctx = canvas.getContext('2d');
  if (!ctx) return null;
  ctx.imageSmoothingQuality = 'high';
  ctx.drawImage(source, 0, 0, out.w, out.h);
  if (source instanceof ImageBitmap) source.close();
  return new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
}

// Ревизия холста: файл, текущий шаг истории и канонические пометки (доли размера,
// как в marks.json). Зум и прокрутка холста в неё не входят — они не меняют картинку
export function canvasRevision(path: string, stepId: string, marks: Mark[], size: { w: number; h: number }): string {
  return hash53(`${path}\n${stepId}\n${marksToJson(marks, size.w, size.h)}`).toString(36);
}

// cyrb53: быстрый 53-битный хеш строки — для сравнения ревизий хватает с запасом
function hash53(s: string): number {
  let h1 = 0xdeadbeef, h2 = 0x41c6ce57;
  for (let i = 0; i < s.length; i++) {
    const ch = s.charCodeAt(i);
    h1 = Math.imul(h1 ^ ch, 2654435761);
    h2 = Math.imul(h2 ^ ch, 1597334677);
  }
  h1 = Math.imul(h1 ^ (h1 >>> 16), 2246822507) ^ Math.imul(h2 ^ (h2 >>> 13), 3266489909);
  h2 = Math.imul(h2 ^ (h2 >>> 16), 2246822507) ^ Math.imul(h1 ^ (h1 >>> 13), 3266489909);
  return 4294967296 * (2097151 & h2) + (h1 >>> 0);
}
