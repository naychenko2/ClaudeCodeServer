// Вторая строка персонажа в списке (спека character-panel-final-spec §9.1, §10.1).
// Выводятся только статусы, честные без бэкенда: обучение «идёт/в очереди/упало» в списке не показываем.
import { LIST_STATUS } from './characterTexts';

export function listStatus(photos: number, lora: string | null | undefined): string {
  if (lora) return LIST_STATUS.trained;
  if (photos === 0) return LIST_STATUS.noPhotos;
  return LIST_STATUS.recognized(photos);
}

// Удалили выбранного для рисования персонажа — выбор сбрасываем; чужого не трогаем
export function resetSelectionOnDelete(
  selected: string | null | undefined, deleted: string, reset: () => void,
) {
  if (selected === deleted) reset();
}
