// Фото персонажа ужимаются на фронте до 2048 px по длинной стороне (ADR-017,
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

// Отбор файлов для фото лица: не-картинки отбрасываются молча, лишние сверх предела — с подсказкой.
// Размер проверяется отдельно, после ужатия (photoSizeError): до него файл с телефона легко за 8 МБ.
export function pickFaceFiles<T extends { name: string; type: string }>(
  files: T[], existing: number,
): { picked: T[]; error: string | null } {
  const room = MAX_PHOTOS - existing;
  if (room <= 0) return { picked: [], error: `Можно не больше ${MAX_PHOTOS} фото. Уберите лишние и загрузите заново.` };
  const images = files.filter(f => f.type.startsWith('image/'));
  const picked = images.slice(0, room);
  const error = files.length > room
    ? `Добавлено не всё: в форме не больше ${MAX_PHOTOS} фото. Уберите лишние, если хотите добавить другие.`
    : null;
  return { picked, error };
}

export const photoSizeError = (name: string, size: number): string | null =>
  size > MAX_PHOTO_MB * 1024 * 1024 ? `${name} больше ${MAX_PHOTO_MB} МБ` : null;

// Загрузка фото лица допускается только с галочкой согласия, и после неё галочка сбрасывается:
// согласие даётся перед каждой загрузкой, а не один раз. Сбой загрузки галочку не трогает.
export async function uploadWithConsent<T>(
  consent: boolean, files: T[], onAdd: (files: T[]) => Promise<void>, resetConsent: () => void,
): Promise<boolean> {
  if (!consent || !files.length) return false;
  await onAdd(files);
  resetConsent();
  return true;
}
