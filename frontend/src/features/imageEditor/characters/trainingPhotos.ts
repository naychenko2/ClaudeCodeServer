// Чистая логика секции «Фото для обучения»: пороги, состояние, тексты, нарезка загрузки.

import type { TrainingPhotoRejectReason } from '../api';
import {
  TRAIN_PHOTOS_COUNTER, TRAIN_PHOTOS_NEED, TRAIN_PHOTOS_TEXT, TRAIN_REJECT_TEXT,
} from './loraTrainTexts';

// Пороги ADR-026 §1: если сервер не прислал свои
export const TRAIN_MIN = 20;
export const TRAIN_MAX = 50;
export const TRAIN_MIN_SIDE_PX = 512;
export const TRAIN_MAX_MB = 8;
// За один запрос сервер принимает до 10 файлов
export const TRAIN_UPLOAD_BATCH = 10;
// Сколько плиток видно в свёрнутом виде
export const TRAIN_GRID_CAP = 12;

export type PhotosState = 'empty' | 'uploading' | 'low' | 'enough' | 'limit';
export type StatusTone = 'muted' | 'warning' | 'success';

export function photosState(n: number, uploading: boolean, min = TRAIN_MIN, max = TRAIN_MAX): PhotosState {
  if (uploading) return 'uploading';
  if (n <= 0) return 'empty';
  if (n >= max) return 'limit';
  return n < min ? 'low' : 'enough';
}

export function photosCounter(n: number, min = TRAIN_MIN, max = TRAIN_MAX) {
  return TRAIN_PHOTOS_COUNTER(n, max, min);
}

// Строка статуса под счётчиком; при загрузке её ведёт сама загрузка
export function photosStatus(state: PhotosState, n: number, min = TRAIN_MIN): { text: string; tone: StatusTone } | null {
  switch (state) {
    case 'low': return { text: TRAIN_PHOTOS_NEED(min - n), tone: 'warning' };
    case 'enough': return { text: TRAIN_PHOTOS_TEXT.enough, tone: 'warning' };
    case 'limit': return { text: TRAIN_PHOTOS_TEXT.limit, tone: 'muted' };
    default: return null;
  }
}

export function rejectText(reason: TrainingPhotoRejectReason): string {
  switch (reason) {
    case 'format': return TRAIN_REJECT_TEXT.format;
    case 'too_small': return TRAIN_REJECT_TEXT.too_small(TRAIN_MIN_SIDE_PX);
    case 'too_big': return TRAIN_REJECT_TEXT.too_big(TRAIN_MAX_MB);
    case 'duplicate': return TRAIN_REJECT_TEXT.duplicate;
    case 'limit': return TRAIN_REJECT_TEXT.limit;
  }
}

export const uploadProgressText = (done: number, total: number) =>
  `Загружаю ${total} фото… ${Math.min(done, total)} из ${total}`;

export const uploadPercent = (done: number, total: number) =>
  total <= 0 ? 0 : Math.round((Math.min(done, total) / total) * 100);

export function chunk<T>(items: T[], size = TRAIN_UPLOAD_BATCH): T[][] {
  const out: T[][] = [];
  for (let i = 0; i < items.length; i += size) out.push(items.slice(i, i + size));
  return out;
}

// Лишние сверх предела отсекаем до отправки и сообщаем отказом «limit»
export function splitByRoom<T extends { name: string }>(files: T[], count: number, max = TRAIN_MAX) {
  const room = Math.max(0, max - count);
  return { accepted: files.slice(0, room), overflow: files.slice(room) };
}
