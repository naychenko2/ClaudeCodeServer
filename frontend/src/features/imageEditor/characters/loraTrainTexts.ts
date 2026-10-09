// Тексты обучения LoRA персонажа — дословно из заметки «LoRA-обучение — тексты».
// Общий файл для секции фото (7б) и раздела «Обучение» (7в).

import type { TrainingPhotoRejectReason } from '../api';

export const TRAIN_PHOTOS_TEXT = {
  title: 'Фото для обучения',
  hint: 'Загрузите 20–50 фото одного человека: лицо хорошо видно, разные ракурсы, свет и фон. Лучше всего — по одному лицу на кадре.',
  add: 'Добавить фото',
  empty: 'Здесь пока нет фото. Добавьте хотя бы 20 — тогда можно обучить персонажа.',
  enough: 'Фото достаточно. Можно обучать.',
  limit: 'Достигнут предел — 50 фото.',
  remove: 'Убрать фото',
} as const;

export const TRAIN_PHOTOS_NEED = (k: number) => `Нужно ещё ${k} фото, чтобы начать.`;
export const TRAIN_PHOTOS_COUNTER = (n: number, max: number, min: number) => `${n} из ${max} · нужно минимум ${min}`;

export const TRAIN_REJECT_TEXT = {
  format: 'Формат не подходит. Нужен JPG, PNG или WebP.',
  too_small: (px: number) => `Фото слишком маленькое. Нужна короткая сторона от ${px} пикселей.`,
  too_big: (mb: number) => `Фото слишком тяжёлое. Максимум ${mb} МБ.`,
  duplicate: 'Такое фото уже добавлено.',
  limit: 'Достигнут предел — 50 фото.',
  // причины проверки лица приходят с версией обучения (7в)
  no_face: 'На фото не нашли лицо — оно не пойдёт в обучение.',
  many_faces: 'На фото несколько лиц — оно не пойдёт в обучение.',
} as const;

export type { TrainingPhotoRejectReason };
