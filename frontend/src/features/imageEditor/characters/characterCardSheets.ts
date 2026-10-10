// Чистая логика карточки персонажа (спека character-panel-final-spec §5): какие шторки открывает
// какой чип или пункт меню, состав меню, раскладка коллажа, точка «Нужны фото» и тексты удаления.
// React здесь нет — тестируется в node.

import { TRAIN_MIN } from './trainingPhotos';

export type CardSheet = 'name' | 'photos' | 'versions' | 'tune';
export type CardMenuKey = 'name' | 'photos' | 'versions' | 'tune' | 'remove';

// Пункт меню «⋯» → шторка; «Удалить» шторки не открывает
export function sheetForMenu(key: CardMenuKey): CardSheet | null {
  return key === 'remove' ? null : key;
}

// Пункты меню по порядку; «Версии» — только если версий больше нуля, «Удалить» отделён разделителем
export function menuKeys(versionsCount: number): CardMenuKey[] {
  return [
    'name', 'photos',
    ...(versionsCount > 0 ? ['versions' as const] : []),
    'tune', 'remove',
  ];
}

export type CardChipKey = 'photos' | 'versions' | 'tune';

export function sheetForChip(key: CardChipKey): CardSheet {
  return key === 'tune' ? 'tune' : key;
}

// Чипы по порядку; «Версии» — только если версий больше нуля
export function chipKeys(versionsCount: number): CardChipKey[] {
  return versionsCount > 0 ? ['photos', 'versions', 'tune'] : ['photos', 'tune'];
}

// Точка «Нужны фото для обучения»: мало фото для обучения или ни одно не подошло.
// train === null (ещё грузится) — точки нет: «мало» было бы неправдой
export function photosNeedDot(train: number | null, allRejected: boolean): boolean {
  return allRejected || (train !== null && train < TRAIN_MIN);
}

export interface CollageLayout {
  // Сколько фото показываем (1–3)
  shown: number;
  // Остаток для «+N» на третьем; 0 — затемнения нет
  extra: number;
}

export function collageLayout(total: number): CollageLayout {
  const shown = Math.min(total, 3);
  return { shown, extra: total > 3 ? total - 3 : 0 };
}

// Главное фото — с primary, иначе первое; остальные в прежнем порядке
export function orderedPhotos<T extends { primary?: boolean | null }>(photos: readonly T[]): T[] {
  const main = photos.find(p => p.primary) ?? photos[0];
  return main ? [main, ...photos.filter(p => p !== main)] : [];
}

// Подтверждение удаления — прежние тексты из CharacterForm
export function deleteConfirmTexts(name: string, path: string) {
  return {
    title: `Удалить «${name}»?`,
    subtitle: `Папка ${path}/ с фото удалится из проекта`,
    confirmLabel: 'Удалить',
  } as const;
}
