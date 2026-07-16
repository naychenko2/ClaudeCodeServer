// Общий контракт памяти — единый вид записи для панелей памяти персоны и команды проекта
// (PersonaMemoryPanel/TeamMemoryPanel). Конкретные панели маппят свои API-сущности
// (PersonaMemoryEntry/TeamMemoryEntry) в этот нормализованный вид перед рендером —
// MemoryPanel/MemoryEntryCard ничего не знают про бэкендовые enum'ы конкретного раздела.

// Происхождение записи: ручное (человек вписал) vs авто (autolearn из хода/совещания)
export type MemoryOrigin = 'manual' | 'auto';

export interface MemoryEntryView<TType extends string = string> {
  id: string;
  type: TType;
  text: string;
  tags?: string[];
  salience: number;            // 0..1 — значимость записи (индикатор на карточке)
  createdAt: string;
  origin: MemoryOrigin;
  originDetail?: string;       // уточнение авто-происхождения: «из хода», «из совещания»
  pending?: boolean;           // предложено autolearn, ждёт подтверждения (только память персоны)
}

// Метаданные типа записи — заголовок группы/бейджа + цвет из палитры C.*
export interface MemoryTypeMeta {
  title: string;
  hint: string;
  color: string;
  softBg: string;
}

// Короткая относительная дата: «только что», «5 мин», «3 ч», «2 дн», иначе — число/месяц
export function shortAgo(iso: string): string {
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return '';
  const diffMs = Date.now() - t;
  const min = Math.floor(diffMs / 60000);
  if (min < 1) return 'только что';
  if (min < 60) return `${min} мин`;
  const h = Math.floor(min / 60);
  if (h < 24) return `${h} ч`;
  const d = Math.floor(h / 24);
  if (d < 7) return `${d} дн`;
  return new Date(t).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' }).replace('.', '');
}

// Полная дата — для tooltip над относительной
export function fmtDate(iso: string): string {
  try { return new Date(iso).toLocaleDateString('ru-RU', { day: 'numeric', month: 'long', hour: '2-digit', minute: '2-digit' }); }
  catch { return ''; }
}
