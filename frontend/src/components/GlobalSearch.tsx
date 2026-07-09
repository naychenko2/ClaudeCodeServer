import { useEffect, useRef, useState } from 'react';
import type { SearchHit } from '../types';
import { api } from '../lib/api';
import { openNoteById } from '../features/notes/saveToNote';
import { Modal } from './ui';
import { C, FONT, R } from '../lib/design';

// Единый поиск (флаг unified-search): оверлей с поиском по заметкам и задачам сразу.
// Заметка → открыть в разделе «Заметки»; задача → hash-диплинк (календарь/проект).
export function GlobalSearch({ onClose }: { onClose: () => void }) {
  const [q, setQ] = useState('');
  const [hits, setHits] = useState<SearchHit[]>([]);
  const [loading, setLoading] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => { inputRef.current?.focus(); }, []);

  useEffect(() => {
    const term = q.trim();
    if (term.length < 2) { setHits([]); setLoading(false); return; }
    setLoading(true);
    const h = setTimeout(() => {
      api.search(term).then(setHits).catch(() => setHits([])).finally(() => setLoading(false));
    }, 250);
    return () => clearTimeout(h);
  }, [q]);

  const open = (hit: SearchHit) => {
    onClose();
    if (hit.type === 'note') openNoteById(hit.id);
    else window.location.hash = hit.url;
  };

  const notes = hits.filter(h => h.type === 'note');
  const tasks = hits.filter(h => h.type === 'task');
  const empty = !loading && q.trim().length >= 2 && hits.length === 0;

  return (
    <Modal width={560} title="Поиск по пространству" onClose={onClose}>
      <input
        ref={inputRef} value={q} onChange={e => setQ(e.target.value)}
        placeholder="Заметки и задачи…"
        style={{
          width: '100%', boxSizing: 'border-box', padding: '10px 12px', fontSize: 15,
          fontFamily: FONT.sans, color: C.textPrimary, background: C.bgInset,
          border: `1px solid ${C.border}`, borderRadius: R.md, outline: 'none',
        }}
      />
      <div style={{ marginTop: 12, maxHeight: 420, overflowY: 'auto' }}>
        {loading && <div style={hintStyle}>Ищу…</div>}
        {empty && <div style={hintStyle}>Ничего не найдено</div>}
        {!loading && q.trim().length < 2 && (
          <div style={hintStyle}>Введите минимум 2 символа</div>
        )}
        {notes.length > 0 && <Group label="Заметки" items={notes} onOpen={open} />}
        {tasks.length > 0 && <Group label="Задачи" items={tasks} onOpen={open} />}
      </div>
    </Modal>
  );
}

const hintStyle: React.CSSProperties = {
  padding: '18px 4px', textAlign: 'center', fontSize: 13, color: C.textMuted, fontFamily: FONT.sans,
};

function Group({ label, items, onOpen }: { label: string; items: SearchHit[]; onOpen: (h: SearchHit) => void }) {
  return (
    <div style={{ marginBottom: 10 }}>
      <div style={{
        fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textMuted,
        textTransform: 'uppercase', letterSpacing: 0.5, padding: '6px 4px 4px',
      }}>
        {label}
      </div>
      {items.map(h => (
        <button
          key={`${h.type}:${h.id}`} onClick={() => onOpen(h)}
          style={{
            display: 'block', width: '100%', textAlign: 'left', border: 'none', cursor: 'pointer',
            background: 'transparent', borderRadius: R.md, padding: '8px 10px', fontFamily: FONT.sans,
          }}
          onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
          onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
        >
          <div style={{ display: 'flex', alignItems: 'baseline', gap: 8 }}>
            <span style={{
              flex: 1, minWidth: 0, fontSize: 14, fontWeight: 500, color: C.textHeading,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>
              {h.title}
            </span>
            <span style={{ flex: 'none', fontSize: 11, color: C.textMuted }}>{h.context}</span>
          </div>
          {h.snippet && (
            <div style={{
              marginTop: 2, fontSize: 12, color: C.textSecondary, lineHeight: 1.4,
              display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden',
            }}>
              {h.snippet}
            </div>
          )}
        </button>
      ))}
    </div>
  );
}
