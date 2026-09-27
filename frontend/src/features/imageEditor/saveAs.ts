// Расчёты диалога «Сохранить как…» (ADR-018 §5): имя без расширения, список папок
// проекта и доступность кнопки. Расширение ставит сервер по формату результата.

import type { FileEntry } from '../../types';
import type { ImageEncodeFormat, SaveCheckResponse } from './api';
import { nextVersionName, splitPath } from './format';

export const FORMAT_EXT: Record<ImageEncodeFormat, string> = { png: 'png', jpeg: 'jpg', webp: 'webp' };

// Вписанное руками расширение срезается, а не удваивается: «hero.png» → «hero»
export function nameStem(input: string): string {
  return input.trim().replace(/\.(png|jpe?g|webp|gif)$/i, '').trim();
}

// Имя по умолчанию: следующая версия исходника без расширения (hero.png → hero.v2)
export function defaultStem(sourceName: string): string {
  return nameStem(nextVersionName(sourceName));
}

// «images/hero.v3.png» → «hero.v3»: подсказку сервера подставляем в поле имени
export function suggestionStem(suggestion: string): string {
  return nameStem(splitPath(suggestion).name);
}

export interface SaveFolder { path: string; files: number }

// Папки проекта с числом файлов прямо в них; корень первым
export function projectFolders(entries: FileEntry[]): SaveFolder[] {
  const counts = new Map<string, number>([['', 0]]);
  for (const e of entries) {
    if (e.isDirectory) { if (!counts.has(e.path)) counts.set(e.path, 0); continue; }
    const { folder } = splitPath(e.path);
    counts.set(folder, (counts.get(folder) ?? 0) + 1);
  }
  return [...counts].map(([path, files]) => ({ path, files }))
    .sort((a, b) => (a.path === '' ? -1 : b.path === '' ? 1 : a.path.localeCompare(b.path)));
}

// Ответ проверки относится к тому, что сейчас в поле: имя, папка и формат
export interface SaveCheckState { key: string; result: SaveCheckResponse | null; error: string | null }

export const checkKey = (folder: string, stem: string, format: ImageEncodeFormat) => `${folder}\n${stem}\n${format}`;

// «Сохранить» недоступна, пока имя пустое, занято, не проверено или отвергнуто сервером
export function saveBlocked(stem: string, key: string, check: SaveCheckState | null): boolean {
  if (!stem) return true;
  if (!check || check.key !== key) return true;
  return !!check.error || !check.result || check.result.taken;
}
