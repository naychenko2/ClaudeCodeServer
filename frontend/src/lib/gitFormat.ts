// Форматирование для git-поверхностей: разбор пути и относительная дата.
// Общий модуль, а не экспорт из панели: обеими функциями пользуются и «Изменения»
// (GitChangesRail), и вкладка «Авторы» просмотрщика (FileViewer) — держать их
// внутри одной из панелей значит связывать соседей друг с другом.

// [папка-родитель, имя файла] из относительного пути
export function splitPath(p: string): [string, string] {
  const norm = p.replace(/\\/g, '/').replace(/\/+$/, '');
  const i = norm.lastIndexOf('/');
  return i < 0 ? ['', norm] : [norm.slice(0, i), norm.slice(i + 1)];
}

// Относительная дата: «2 ч назад», «5 мин назад», дальше — обычная дата
export function relTime(iso: string): string {
  const t = Date.parse(iso);
  if (isNaN(t)) return '';
  const diffMin = Math.floor((Date.now() - t) / 60_000);
  if (diffMin < 1) return 'только что';
  if (diffMin < 60) return `${diffMin} мин назад`;
  const h = Math.floor(diffMin / 60);
  if (h < 24) return `${h} ч назад`;
  const d = Math.floor(h / 24);
  if (d < 30) return `${d} дн назад`;
  return new Date(t).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short', year: 'numeric' });
}
