// Разбор unified diff на хунки и сборка частичных патчей для зернистого stage
// (POST /git/stage-hunk). Точный пересчёт заголовков @@ не нужен — сервер
// применяет патч с git apply --recount.

export interface PatchLine {
  kind: 'ctx' | 'add' | 'del';
  text: string;   // строка без ведущего знака
}

export interface DiffHunk {
  header: string;      // строка @@ -a,b +c,d @@ …
  oldStart: number;
  newStart: number;
  lines: PatchLine[];
  raw: string;         // хунк как есть (заголовок + строки), с завершающим \n
}

export interface ParsedFileDiff {
  fileHeader: string;  // заголовки diff --git/index/---/+++ до первого @@
  hunks: DiffHunk[];
}

export function parseDiffToHunks(diff: string): ParsedFileDiff {
  const lines = diff.split('\n');
  if (lines.length && lines[lines.length - 1] === '') lines.pop();  // хвост от split по \n
  const headerLines: string[] = [];
  let i = 0;
  while (i < lines.length && !lines[i].startsWith('@@')) { headerLines.push(lines[i]); i++; }
  const hunks: DiffHunk[] = [];
  let cur: DiffHunk | null = null;
  for (; i < lines.length; i++) {
    const l = lines[i];
    if (l.startsWith('@@')) {
      const m = l.match(/@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/);
      cur = {
        header: l,
        oldStart: m ? parseInt(m[1], 10) : 0,
        newStart: m ? parseInt(m[2], 10) : 0,
        lines: [],
        raw: l + '\n',
      };
      hunks.push(cur);
    } else if (cur) {
      if (l.startsWith('+')) { cur.lines.push({ kind: 'add', text: l.slice(1) }); cur.raw += l + '\n'; }
      else if (l.startsWith('-')) { cur.lines.push({ kind: 'del', text: l.slice(1) }); cur.raw += l + '\n'; }
      else if (l.startsWith('\\')) { cur.raw += l + '\n'; }  // «\ No newline at end of file» — не строка кода
      else { cur.lines.push({ kind: 'ctx', text: l.startsWith(' ') ? l.slice(1) : l }); cur.raw += l + '\n'; }
    }
  }
  return { fileHeader: headerLines.join('\n'), hunks };
}

// Полный патч одного хунка (для кнопки «+ Хунк»)
export function buildHunkPatch(fileHeader: string, hunk: DiffHunk): string {
  return fileHeader + '\n' + hunk.raw;
}

// Патч только выбранных строк хунка: невыбранные add-строки выбрасываются,
// невыбранные del-строки превращаются в контекст (остаются в файле)
export function buildLinesPatch(fileHeader: string, hunk: DiffHunk, selectedIdx: Set<number>): string {
  const body: string[] = [];
  hunk.lines.forEach((l, idx) => {
    if (l.kind === 'ctx') body.push(' ' + l.text);
    else if (l.kind === 'add') { if (selectedIdx.has(idx)) body.push('+' + l.text); }
    else { body.push((selectedIdx.has(idx) ? '-' : ' ') + l.text); }
  });
  return fileHeader + '\n' + hunk.header + '\n' + body.join('\n') + '\n';
}
