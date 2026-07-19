import React, { useEffect, useMemo, useState } from 'react';
import { C, FONT, SHADOW } from '../lib/design';

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

// Опциональный режим зернистого stage (diff-вкладка файла из git-«Изменений»):
// кнопка «+ Хунк» на заголовке хунка и чекбоксы строк с плавающей кнопкой.
// Индексы (hunkIdx, lineIdx) согласованы с parseDiffToHunks из lib/gitPatch.ts.
export interface DiffStaging {
  busy?: boolean;
  onStageHunk: (hunkIdx: number) => void;
  onStageLines: (selected: Map<number, Set<number>>) => void;  // hunkIdx → индексы строк хунка
}

// Аннотация строки: к какому хунку относится и её индекс внутри хунка (ctx+add+del)
interface RowMeta { hunkIdx: number; lineIdx: number }

export function DiffView({ diff, staging }: { diff: string; staging?: DiffStaging }) {
  const { rows, meta } = useMemo(() => {
    const rows = parseDiff(diff);
    // Индексы для сборки частичных патчей: meta-строки («\ No newline») не считаются
    const meta: (RowMeta | null)[] = [];
    let hunkIdx = -1, lineIdx = 0;
    for (const r of rows) {
      if (r.type === 'hunk') { hunkIdx++; lineIdx = 0; meta.push({ hunkIdx, lineIdx: -1 }); }
      else if (r.type === 'meta') meta.push(null);
      else { meta.push(hunkIdx >= 0 ? { hunkIdx, lineIdx } : null); lineIdx++; }
    }
    return { rows, meta };
  }, [diff]);

  // Выбранные строки для частичного stage (ключ «hunk:line»); сброс при смене диффа
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [hoverHunk, setHoverHunk] = useState<number | null>(null);
  useEffect(() => { setSelected(new Set()); }, [diff]);

  const toggleLine = (key: string) =>
    setSelected(prev => { const n = new Set(prev); n.has(key) ? n.delete(key) : n.add(key); return n; });

  const handleStageSelected = () => {
    if (!staging || selected.size === 0) return;
    const map = new Map<number, Set<number>>();
    for (const key of selected) {
      const [h, l] = key.split(':').map(Number);
      (map.get(h) ?? map.set(h, new Set<number>()).get(h)!).add(l);
    }
    setSelected(new Set());
    staging.onStageLines(map);
  };

  const gutter: React.CSSProperties = { width: 40, textAlign: 'right', padding: '0 7px', color: C.textMuted, userSelect: 'none', flexShrink: 0 };
  return (
    <div style={{ fontFamily: FONT.mono, fontSize: 12, lineHeight: '1.55' }}>
      {rows.map((r, i) => {
        const rm = meta[i];
        if (r.type === 'hunk') return (
          <div
            key={i}
            onMouseEnter={staging ? () => setHoverHunk(rm?.hunkIdx ?? null) : undefined}
            style={{ display: 'flex', alignItems: 'center', gap: 8, background: C.infoBg, color: C.info, padding: '2px 10px' }}
          >
            <span style={{ flex: 1, whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>{r.text}</span>
            {staging && rm && (
              <button
                title="Проиндексировать хунк"
                disabled={staging.busy}
                onClick={() => staging.onStageHunk(rm.hunkIdx)}
                style={{
                  flexShrink: 0, border: `1px solid ${C.border}`, background: C.bgWhite, color: C.accent,
                  borderRadius: 6, padding: '1px 8px', fontSize: 11, fontWeight: 700, fontFamily: FONT.sans,
                  cursor: staging.busy ? 'default' : 'pointer', opacity: staging.busy ? 0.5 : 1, whiteSpace: 'nowrap',
                }}
              >+ Хунк</button>
            )}
          </div>
        );
        if (r.type === 'meta') return (
          <div key={i} style={{ color: C.textMuted, padding: '0 10px', fontStyle: 'italic' }}>{r.text}</div>
        );
        const bg = r.type === 'add' ? C.diffAddBg : r.type === 'del' ? C.diffRemBg : 'transparent';
        const sign = r.type === 'add' ? '+' : r.type === 'del' ? '−' : '';
        const signColor = r.type === 'add' ? C.diffAddText : C.diffRemText;
        const selectable = staging && rm && r.type !== 'ctx';
        const key = rm ? `${rm.hunkIdx}:${rm.lineIdx}` : '';
        const showCheckbox = selectable && (hoverHunk === rm!.hunkIdx || selected.has(key));
        return (
          <div
            key={i}
            onMouseEnter={staging ? () => setHoverHunk(rm?.hunkIdx ?? null) : undefined}
            style={{ display: 'flex', background: bg, alignItems: 'flex-start' }}
          >
            {staging && (
              <span style={{ width: 20, flexShrink: 0, display: 'flex', justifyContent: 'center', paddingTop: 2 }}>
                {selectable && (
                  <input
                    type="checkbox"
                    checked={selected.has(key)}
                    onChange={() => toggleLine(key)}
                    disabled={staging.busy}
                    title="Выбрать строку для индексации"
                    style={{
                      width: 12, height: 12, margin: 0, cursor: 'pointer', accentColor: C.accent,
                      visibility: showCheckbox ? 'visible' : 'hidden',
                    }}
                  />
                )}
              </span>
            )}
            <span style={gutter}>{r.oldNo ?? ''}</span>
            <span style={gutter}>{r.newNo ?? ''}</span>
            <span style={{ width: 16, textAlign: 'center', color: signColor, userSelect: 'none', flexShrink: 0 }}>{sign}</span>
            <span style={{ flex: 1, whiteSpace: 'pre-wrap', wordBreak: 'break-all', color: C.textHeading, paddingRight: 10 }}>{r.text || ' '}</span>
          </div>
        );
      })}

      {/* Плавающая кнопка индексации выбранных строк */}
      {staging && selected.size > 0 && (
        <div style={{ position: 'sticky', bottom: 12, display: 'flex', justifyContent: 'flex-end', padding: '8px 16px', pointerEvents: 'none' }}>
          <button
            disabled={staging.busy}
            onClick={handleStageSelected}
            style={{
              pointerEvents: 'auto', border: 'none', background: C.accent, color: C.onAccent,
              borderRadius: 10, padding: '8px 16px', fontSize: 13, fontWeight: 600, fontFamily: FONT.sans,
              cursor: staging.busy ? 'default' : 'pointer', opacity: staging.busy ? 0.6 : 1,
              boxShadow: SHADOW.fab,
            }}
          >Проиндексировать: {selected.size} стр.</button>
        </div>
      )}
    </div>
  );
}
