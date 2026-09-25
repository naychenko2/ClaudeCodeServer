// Картинка для «Обсудить с Claude»: размеченная копия, а без стрелок, рамок и подписей —
// сама картинка (exportAnnotated в этом случае отдаёт null, а ручке обсуждения картинка
// нужна всегда).

import { exportAnnotated, type Mark } from '../marks';

export async function discussSnapshot(img: HTMLImageElement, marks: Mark[], w: number, h: number): Promise<Blob | null> {
  const annotated = await exportAnnotated(img, marks, w, h).catch(() => null);
  if (annotated) return annotated;
  const canvas = document.createElement('canvas');
  canvas.width = w;
  canvas.height = h;
  const ctx = canvas.getContext('2d');
  if (!ctx) return null;
  ctx.drawImage(img, 0, 0, w, h);
  return new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
}
