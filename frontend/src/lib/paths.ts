// Нормализация путей проекта — единственная каноническая реализация.
// Используется лентой чата (ChatPanel) и сборкой артефактов (useSessionArtifacts).
// Все сравнения регистронезависимые (Windows: C:\ vs c:\), разделители приводятся к «/».

// Привести абсолютный путь к относительному внутри rootPath.
// Возвращает null, если путь вне корня (или сам корень — это не файл).
// Уже относительные пути нормализует (backslash → slash, срез «./»);
// выход за корень («../») тоже даёт null.
export function toRelative(raw: string, rootPath: string): string | null {
  const p = raw.replace(/\\/g, '/');
  // Уже относительный (Claude иногда передаёт относительные пути)
  if (!/^([a-zA-Z]:\/|\/)/.test(p)) {
    const rel = p.replace(/^\.\//, '');
    // Выход за пределы корня — не файл проекта
    if (rel.startsWith('../') || rel.includes('/../')) return null;
    return rel;
  }
  const root = rootPath.replace(/\\/g, '/').replace(/\/+$/, '');
  if (!root) return null;
  const lp = p.toLowerCase();
  const lr = root.toLowerCase();
  if (lp === lr) return null;
  if (lp.startsWith(lr + '/')) return p.slice(root.length + 1);
  return null;
}

// Путь для показа в UI: внутри корня — относительный, сам корень — «.»,
// вне корня или относительный — как есть (в отличие от toRelative ничего не отсекает).
export function relPath(p: string, root?: string | null): string {
  if (!root || !p) return p;
  const np = p.replace(/\\/g, '/');
  const nr = root.replace(/\\/g, '/').replace(/\/+$/, '');
  if (np.toLowerCase() === nr.toLowerCase()) return '.';
  // Абсолютный путь внутри корня → относительный; прочее (вне корня, относительный) — без изменений
  if (/^([a-zA-Z]:\/|\/)/.test(np)) {
    const rel = toRelative(p, root);
    if (rel !== null) return rel;
  }
  return p;
}

// Делает пути относительными в произвольном тексте (командах, выводе, плане):
// «<root>\sub\file» → «sub\file», голый «<root>» → «.». Учитывает оба разделителя и регистр (Windows).
export function stripRoot(text: string, root?: string | null): string {
  if (!root || !text) return text;
  const nr = root.replace(/[\\/]+$/, '');
  const variants = Array.from(new Set([nr, nr.replace(/\\/g, '/'), nr.replace(/\//g, '\\')]));
  let out = text;
  for (const v of variants) {
    if (!v) continue;
    const esc = v.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    // после корня: разделитель + остаток пути (до пробела/кавычки) → остаток; иначе корень → «.»
    out = out.replace(new RegExp(esc + '([\\\\/]([^\\s"\'`]*))?', 'gi'), (_m, _g1, rest) => (rest ? rest : '.'));
  }
  return out;
}
