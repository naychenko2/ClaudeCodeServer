import React from 'react';
import { C, FONT } from '../lib/design';

// Парсинг unified-diff (git) в строки с номерами old/new и заголовками ханков.
// Общий модуль рендера unified-diff (используется в FileViewer для diff файла).
export interface DiffRow { type: 'hunk' | 'add' | 'del' | 'ctx' | 'meta'; text: string; oldNo?: number; newNo?: number }

export function parseDiff(diff: string): DiffRow[] {
  const rows: DiffRow[] = [];
  let oldNo = 0, newNo = 0;
  for (const raw of diff.split('\n')) {
    if (raw.startsWith('diff --git') || raw.startsWith('index ') || raw.startsWith('--- ') || raw.startsWith('+++ ') ||
        raw.startsWith('new file') || raw.startsWith('deleted file') || raw.startsWith('similarity') || raw.startsWith('rename ')) continue;
    if (raw.startsWith('@@')) {
      const m = raw.match(/@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/);
      if (m) { oldNo = parseInt(m[1], 10); newNo = parseInt(m[2], 10); }
      rows.push({ type: 'hunk', text: raw });
    } else if (raw.startsWith('+')) {
      rows.push({ type: 'add', text: raw.slice(1), newNo }); newNo++;
    } else if (raw.startsWith('-')) {
      rows.push({ type: 'del', text: raw.slice(1), oldNo }); oldNo++;
    } else if (raw.startsWith('\\')) {
      rows.push({ type: 'meta', text: raw });
    } else {
      rows.push({ type: 'ctx', text: raw.startsWith(' ') ? raw.slice(1) : raw, oldNo, newNo }); oldNo++; newNo++;
    }
  }
  // Срезаем хвостовую пустую строку (split по \n)
  if (rows.length && rows[rows.length - 1].type === 'ctx' && rows[rows.length - 1].text === '') rows.pop();
  return rows;
}

// Список изменённых файлов из заголовков `diff --git a/… b/…` (для шапки коммита)
export function diffFileNames(diff: string): string[] {
  const names: string[] = [];
  for (const line of diff.split('\n')) {
    if (!line.startsWith('diff --git ')) continue;
    const m = line.match(/^diff --git a\/(.+?) b\/(.+)$/);
    if (m) names.push(m[2]);
  }
  return names;
}

export function DiffView({ diff }: { diff: string }) {
  const rows = parseDiff(diff);
  const gutter: React.CSSProperties = { width: 40, textAlign: 'right', padding: '0 7px', color: C.textMuted, userSelect: 'none', flexShrink: 0 };
  return (
    <div style={{ fontFamily: FONT.mono, fontSize: 12, lineHeight: '1.55' }}>
      {rows.map((r, i) => {
        if (r.type === 'hunk') return (
          <div key={i} style={{ background: C.infoBg, color: C.info, padding: '2px 10px', whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>{r.text}</div>
        );
        if (r.type === 'meta') return (
          <div key={i} style={{ color: C.textMuted, padding: '0 10px', fontStyle: 'italic' }}>{r.text}</div>
        );
        const bg = r.type === 'add' ? C.diffAddBg : r.type === 'del' ? C.diffRemBg : 'transparent';
        const sign = r.type === 'add' ? '+' : r.type === 'del' ? '−' : '';
        const signColor = r.type === 'add' ? C.diffAddText : C.diffRemText;
        return (
          <div key={i} style={{ display: 'flex', background: bg, alignItems: 'flex-start' }}>
            <span style={gutter}>{r.oldNo ?? ''}</span>
            <span style={gutter}>{r.newNo ?? ''}</span>
            <span style={{ width: 16, textAlign: 'center', color: signColor, userSelect: 'none', flexShrink: 0 }}>{sign}</span>
            <span style={{ flex: 1, whiteSpace: 'pre-wrap', wordBreak: 'break-all', color: C.textHeading, paddingRight: 10 }}>{r.text || ' '}</span>
          </div>
        );
      })}
    </div>
  );
}
