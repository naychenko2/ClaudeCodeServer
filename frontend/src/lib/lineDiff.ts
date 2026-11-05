// Построчный unified-diff «в проекте → ваша версия» — для диалога конфликта сохранения
// (S2c-F2a, К8). Вход: текст чужой версии (из тела 409) и текст своей. Выход: стандартный
// unified diff, который понимают оба дифф-рендера проекта — components/DiffView (parseDiff)
// и lib/gitPatch (parseDiffToHunks). npm-зависимостей нет: LCS по строкам, цена ограничена
// потолком LINE_DIFF_MAX_LINES — выше потолка null, и диалог показывает вариант без диффа,
// чтобы не вешать вкладку на файле в единицы МиБ.
//
// Направление: чужая версия — «a» (строки «-»), ваша — «b» (строки «+»): читают как
// «в проекте → ваша версия», той же подписью, что у SaveConflictDialog.
//
// Возврат: '' — различий нет; null — выше потолка строк (решает вызывающий: он знает свои
// тексты и может показать вариант без диффа); иначе текст диффа.

const CONTEXT = 3; // строк контекста вокруг каждого хунка — как у git

// Потолок на каждую сторону: LCS-DP 2000×2000 — ~16 МБ и десятки мс, вкладка не виснет.
// Файл с большими текстами — единицы МиБ (потолок ETag бэка — 10 МиБ, FilesController.
// MaxEtagTextBytes) — выше потолка диалог идёт по варианту без диффа.
export const LINE_DIFF_MAX_LINES = 2000;

interface Op { kind: 'ctx' | 'del' | 'add'; text: string }

// Строки текста. Терминатор строки — не часть строки: разница терминаторов (CRLF vs LF)
// не разница содержимого — человек видит тот же текст. Пустой элемент после финального
// перевода строки — артефакт split, а не строка файла.
function splitLines(text: string): string[] {
  if (!text) return [];
  const parts = text.split(/\r\n|\r|\n/);
  if (parts.length && parts[parts.length - 1] === '') parts.pop();
  return parts;
}

// LCS по строкам (DP) + backtrack. При равных длинах предпочитается del раньше add —
// порядок строк, как у git diff.
function lcsOps(a: string[], b: string[]): Op[] {
  const n = a.length, m = b.length;
  const W = m + 1;
  const dp = new Int32Array((n + 1) * W);
  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      dp[i * W + j] = a[i] === b[j] ? dp[(i + 1) * W + j + 1] + 1 : Math.max(dp[(i + 1) * W + j], dp[i * W + j + 1]);
    }
  }
  const ops: Op[] = [];
  let i = 0, j = 0;
  while (i < n && j < m) {
    if (a[i] === b[j]) { ops.push({ kind: 'ctx', text: a[i] }); i++; j++; }
    else if (dp[(i + 1) * W + j] >= dp[i * W + j + 1]) { ops.push({ kind: 'del', text: a[i] }); i++; }
    else { ops.push({ kind: 'add', text: b[j] }); j++; }
  }
  while (i < n) ops.push({ kind: 'del', text: a[i++] });
  while (j < m) ops.push({ kind: 'add', text: b[j++] });
  return ops;
}

export function lineDiff(theirs: string, mine: string): string | null {
  const a = splitLines(theirs);
  const b = splitLines(mine);
  if (a.length > LINE_DIFF_MAX_LINES || b.length > LINE_DIFF_MAX_LINES) return null;

  const ops = lcsOps(a, b);
  const changed: number[] = [];
  for (let k = 0; k < ops.length; k++) if (ops[k].kind !== 'ctx') changed.push(k);
  if (!changed.length) return '';

  // Группировка правок в хунки: окружение CONTEXT строк, соседние группы со щелью
  // 2·CONTEXT или меньше сливаются — как у git (две правки через 6 строк — один хунк).
  const groups: Array<[number, number]> = [];
  for (const k of changed) {
    const s = Math.max(0, k - CONTEXT);
    const e = Math.min(ops.length, k + CONTEXT + 1);
    const last = groups[groups.length - 1];
    if (last && s <= last[1]) last[1] = Math.max(last[1], e);
    else groups.push([s, e]);
  }

  const out: string[] = [];
  for (const [s, e] of groups) {
    let oldBefore = 0, newBefore = 0, oldCount = 0, newCount = 0;
    for (let k = 0; k < s; k++) { if (ops[k].kind !== 'add') oldBefore++; if (ops[k].kind !== 'del') newBefore++; }
    for (let k = s; k < e; k++) { if (ops[k].kind !== 'add') oldCount++; if (ops[k].kind !== 'del') newCount++; }
    // Хунк без строк на стороне (создание/удаление файла) нумеруется, как git, линией перед
    // вставкой: в начале файла — 0 («@@ -0,0 +1,N @@»).
    const oldStart = oldCount ? oldBefore + 1 : oldBefore;
    const newStart = newCount ? newBefore + 1 : newBefore;
    out.push(`@@ -${oldStart},${oldCount} +${newStart},${newCount} @@`);
    for (let k = s; k < e; k++) {
      const o = ops[k];
      out.push((o.kind === 'ctx' ? ' ' : o.kind === 'del' ? '-' : '+') + o.text);
    }
  }
  return out.join('\n') + '\n';
}
