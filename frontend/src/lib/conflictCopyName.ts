// Имя копии при конфликте сохранения (п. 27–28 «Общих проектов»):
// `<имя> (копия, Мария).<ext>`. Имя автора копии берём как есть — фронт не склоняет.
// Расширение — после последней точки, но не у скрытых файлов без расширения (`.env`)
// и не у имён, оканчивающихся точкой: там суффикс идёт в конец.
export function conflictCopyName(fileName: string, authorName: string): string {
  const slash = Math.max(fileName.lastIndexOf('/'), fileName.lastIndexOf('\\'));
  const dir = fileName.slice(0, slash + 1);
  const base = fileName.slice(slash + 1);
  const suffix = ` (копия, ${authorName.trim() || 'без имени'})`;
  const dot = base.lastIndexOf('.');
  if (dot <= 0 || dot === base.length - 1) return `${dir}${base}${suffix}`;
  return `${dir}${base.slice(0, dot)}${suffix}${base.slice(dot)}`;
}
