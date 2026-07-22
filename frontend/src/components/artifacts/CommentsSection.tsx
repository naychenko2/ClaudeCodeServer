// Секция «Комментарии» (комментарии к документам из хода). Перенесена из ArtifactsPanel verbatim.
import { useState, type CSSProperties } from 'react';
import { MessageCircle, ArrowUpRight } from 'lucide-react';
import { C, FONT } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { api } from '../../lib/api';
import type { CommentArtifact } from '../../hooks/useSessionArtifacts';

// Строка комментария к документу: клик — открыть заметку-комментарий (резолв по заголовку)
function CommentRow({ title, doc, mentioned }: { title: string; doc: string; mentioned?: boolean }) {
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
        display: 'flex', flexDirection: 'column', gap: 2, padding: '7px 14px',
        width: '100%', boxSizing: 'border-box', textAlign: 'left',
        border: 'none', cursor: 'pointer', background: 'transparent',
        fontFamily: 'inherit', opacity: opening ? 0.6 : 1,
      }}
      onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
      onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
    >
      <span style={{ display: 'flex', alignItems: 'center', gap: 9, minWidth: 0 }}>
        <MessageCircle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={mentioned ? C.textMuted : C.accent} style={{ flexShrink: 0 }} />
        <span style={{ fontFamily: FONT.sans, fontSize: 12.5, color: C.textSecondary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', flex: 1 } as CSSProperties}>
          {title}
        </span>
        {mentioned && (
          <span style={{ fontSize: 10, color: C.textMuted, flexShrink: 0 }}>упомянут</span>
        )}
        <ArrowUpRight size={12} strokeWidth={2} color={C.textMuted} style={{ flexShrink: 0, opacity: 0.5 }} />
      </span>
      {doc && (
        <span style={{ fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted, paddingLeft: 23, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' } as CSSProperties}>
          {doc}
        </span>
      )}
    </button>
  );
}

export function CommentsSection({ comments }: { comments: CommentArtifact[] }) {
  return (
    <div style={{ flex: 1, overflowY: 'auto', padding: '8px 0' }}>
      {comments.length === 0 ? (
        <div style={{ padding: '20px 14px', fontFamily: FONT.sans, fontSize: 13, color: C.textMuted, textAlign: 'center' }}>
          Комментарии к документам не создавались
        </div>
      ) : comments.map((c, i) => (
        <CommentRow key={i} title={c.title} doc={c.doc} mentioned={c.mentioned} />
      ))}
    </div>
  );
}
