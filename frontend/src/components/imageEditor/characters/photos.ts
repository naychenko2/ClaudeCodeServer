// Фото персонажа ужимаются на фронте до 2048 px по длинной стороне (ADR-016,
// раздел 10): 10 снимков с телефона дают 30–60 МБ, серверной библиотеки картинок нет.

export const MIN_PHOTOS = 3;
export const MAX_PHOTOS = 10;
export const MAX_PHOTO_MB = 8;
const MAX_SIDE = 2048;

export async function shrinkPhoto(file: File): Promise<Blob> {
  const url = URL.createObjectURL(file);
  try {
    const img = await new Promise<HTMLImageElement>((resolve, reject) => {
      const el = new Image();
      el.onload = () => resolve(el);
      el.onerror = () => reject(new Error(`Не удалось открыть ${file.name}`));
      el.src = url;
    });
    const k = Math.min(1, MAX_SIDE / Math.max(img.naturalWidth, img.naturalHeight));
    if (k === 1 && file.size <= MAX_PHOTO_MB * 1024 * 1024) return file;
    const canvas = document.createElement('canvas');
    canvas.width = Math.round(img.naturalWidth * k);
    canvas.height = Math.round(img.naturalHeight * k);
    canvas.getContext('2d')?.drawImage(img, 0, 0, canvas.width, canvas.height);
    const blob = await new Promise<Blob | null>(r => canvas.toBlob(r, 'image/jpeg', 0.9));
    if (!blob) throw new Error(`Не удалось уменьшить ${file.name}`);
    return new File([blob], file.name.replace(/\.\w+$/, '') + '.jpg', { type: 'image/jpeg' });
  } finally {
    URL.revokeObjectURL(url);
  }
}

export const photosCountText = (n: number) =>
  n >= MIN_PHOTOS ? `${n} фото из ${MAX_PHOTOS}` : `${n} из ${MAX_PHOTOS} · нужно ещё ${MIN_PHOTOS - n}`;
