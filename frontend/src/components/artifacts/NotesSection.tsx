// Секция «Заметки» (созданные/упомянутые в ходе разговора). Перенесена из ArtifactsPanel verbatim.
import { useState, type CSSProperties } from 'react';
import { StickyNote, ArrowUpRight } from 'lucide-react';
import { C, FONT } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { api } from '../../lib/api';

// Строка заметки в артефактах: клик — открыть заметку
function NoteRow({ title }: { title: string }) {
  const [opening, setOpening] = useState(false);
  return (
    <button disabled={opening}
      onClick={async () => {
        setOpening(true);
        try {
          const r = await api.notes.resolve(title);
          if (r?.note) {
            window.dispatchEvent(new CustomEvent('cc-open-url', {
              detail: { url: `#/notes/${encodeURIComponent(r.note.id)}` }
            }));
          }
        } catch { /* заметка не найдена */ }
        setOpening(false);
      }}
      style={{
        display: 'flex', alignItems: 'center', gap: 9, padding: '7px 14px',
        width: '100%', boxSizing: 'border-box', textAlign: 'left',
        border: 'none', cursor: 'pointer', background: 'transparent',
        fontFamily: 'inherit', opacity: opening ? 0.6 : 1,
      }}
      onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
      onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
    >
      <StickyNote size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0 }} />
      <span style={{ fontFamily: FONT.sans, fontSize: 12.5, color: C.textSecondary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', flex: 1 } as CSSProperties}>
        {title}
      </span>
      <ArrowUpRight size={12} strokeWidth={2} color={C.textMuted} style={{ flexShrink: 0, opacity: 0.5 }} />
    </button>
  );
}

export function NotesSection({ notes }: { notes: string[] }) {
  return (
    <div style={{ flex: 1, overflowY: 'auto', padding: '8px 0' }}>
      {notes.length === 0 ? (
        <div style={{ padding: '20px 14px', fontFamily: FONT.sans, fontSize: 13, color: C.textMuted, textAlign: 'center' }}>
          Заметки не создавались
        </div>
      ) : notes.map((title, i) => (
        <NoteRow key={i} title={title} />
      ))}
    </div>
  );
}
