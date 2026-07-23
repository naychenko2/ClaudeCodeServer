// Slug-хелпер, зеркалящий backend PersonaManager.Slugify: транслит кириллицы,
// нижний регистр, всё небуквенное → одиночные дефисы. Единая таблица для превью
// на фронте (handle персоны, авто-имя ветки worktree) — иначе превью ≠ факт.
const TRANSLIT: Record<string, string> = {
  а: 'a', б: 'b', в: 'v', г: 'g', д: 'd', е: 'e', ё: 'e', ж: 'zh', з: 'z', и: 'i', й: 'y',
  к: 'k', л: 'l', м: 'm', н: 'n', о: 'o', п: 'p', р: 'r', с: 's', т: 't', у: 'u', ф: 'f',
  х: 'h', ц: 'ts', ч: 'ch', ш: 'sh', щ: 'sch', ъ: '', ы: 'y', ь: '', э: 'e', ю: 'yu', я: 'ya',
};

// live=true не обрезает хвостовой дефис — чтобы его можно было печатать в поле
// (финальную обрезку делает бэкенд при сохранении).
export function slugify(s: string, live = false): string {
  let out = '';
  let prevDash = false;
  for (const ch of s.trim().toLowerCase()) {
    if (/[a-z0-9]/.test(ch)) { out += ch; prevDash = false; }
    else if (ch in TRANSLIT) { const t = TRANSLIT[ch]; if (t) { out += t; prevDash = false; } }
    else if (!prevDash && out.length > 0) { out += '-'; prevDash = true; }
  }
  return live ? out : out.replace(/-+$/, '');
}
