import type { NoteDetail } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { NotesGraph } from './NotesGraph';
import { CollapseGroup, SourceDot, IconBacklink, IconOutlink, IconGraph, IconLink } from './shared';

// Блок связей заметки: обратные ссылки, исходящие, несвязанные упоминания, локальный
// граф. Используется сайдбаром NoteView (раздел «Заметки») и FileViewer (notes/*.md
// в файлах проекта). Навигация: onOpenNote(id, title) — вызывающий сам решает,
// переходить по id (раздел заметок) или по заголовку (из файлового менеджера).
export function NoteConnections({ note, onOpenNote, onWikilink, onLinkMention }: {
  note: NoteDetail;
  onOpenNote: (id: string, title: string) => void;
  onWikilink: (target: string) => void;
  // Задан — у несвязанных упоминаний появляется кнопка «Связать»
  onLinkMention?: (targetTitle: string) => void;
}) {
  return (
    <div>
      <CollapseGroup
        defaultOpen={note.backlinks.length > 0}
        title={<span style={capStyle}><IconBacklink />Обратные ссылки · {note.backlinks.length}</span>}
      >
        {note.backlinks.length === 0
          ? <div style={{ padding: '4px 4px 8px', color: C.textMuted, fontSize: 12 }}>Пока никто не ссылается</div>
          : note.backlinks.map((b, i) => (
              <button key={i} onClick={() => onOpenNote(b.sourceId, b.sourceTitle)} style={rowStyle}>
                <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                  <SourceDot source={b.source} size={7} />
                  <span style={{ fontSize: 12.5, fontWeight: 500, color: C.textPrimary }}>{b.sourceTitle}</span>
                </span>
                <span style={snippetStyle}>{b.snippet}</span>
              </button>
            ))}
      </CollapseGroup>

      {note.links.length > 0 && (
        <CollapseGroup
          defaultOpen={false}
          title={<span style={capStyle}><IconOutlink />Исходящие · {note.links.length}</span>}
        >
          {note.links.map((l, i) => (
            <button
              key={i}
              onClick={() => l.resolved ? onOpenNote(l.targetId, l.targetTitle) : onWikilink(l.targetTitle)}
              style={{ ...rowStyle, display: 'flex', alignItems: 'center', gap: 6 }}
            >
              <span style={{ width: 7, height: 7, borderRadius: '50%', background: l.resolved ? C.accent : 'transparent', border: l.resolved ? 'none' : `1px dashed ${C.textMuted}`, flex: 'none' }} />
              <span style={{ fontSize: 12.5, color: l.resolved ? C.textPrimary : C.textMuted, fontStyle: l.resolved ? 'normal' : 'italic' }}>{l.targetTitle}</span>
            </button>
          ))}
        </CollapseGroup>
      )}

      {note.unlinkedMentions.length > 0 && (
        <CollapseGroup
          defaultOpen={false}
          title={<span style={capStyle}>Несвязанные упоминания · {note.unlinkedMentions.length}</span>}
        >
          {note.unlinkedMentions.map((u, i) => (
            <div key={i} style={{ ...rowStyle, display: 'flex', alignItems: 'flex-start', gap: 8 }}>
              <button onClick={() => onOpenNote(u.sourceId, u.sourceTitle)} title="Открыть заметку"
                style={{ flex: 1, minWidth: 0, textAlign: 'left', background: 'none', border: 'none', cursor: 'pointer', padding: 0, fontFamily: FONT.sans }}>
                <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                  <SourceDot source={u.source} size={7} />
                  <span style={{ fontSize: 12.5, fontWeight: 500, color: C.textPrimary }}>{u.sourceTitle}</span>
                </span>
                <span style={snippetStyle}>{u.snippet}</span>
              </button>
              {onLinkMention && (
                <button
                  onClick={() => onLinkMention(u.sourceTitle)}
                  title={`Обернуть упоминание в [[${u.sourceTitle}]]`}
                  style={{
                    display: 'flex', alignItems: 'center', gap: 4, flex: 'none',
                    fontSize: 11, fontWeight: 500, color: C.accent, background: C.accentLight,
                    border: 'none', borderRadius: R.sm, padding: '3px 8px', cursor: 'pointer', fontFamily: FONT.sans,
                  }}>
                  <IconLink />Связать
                </button>
              )}
            </div>
          ))}
        </CollapseGroup>
      )}

      <CollapseGroup
        defaultOpen={note.backlinks.length + note.links.length > 0}
        title={<span style={capStyle}><IconGraph />Граф связей</span>}
      >
        <div style={{ height: 230, border: `1px solid ${C.border}`, borderRadius: R.lg, overflow: 'hidden', background: C.bgMain }}>
          <NotesGraph sourceFilter={null} selectedId={note.id} focusId={note.id}
            onSelectNode={id => onOpenNote(id, '')} />
        </div>
      </CollapseGroup>
    </div>
  );
}

const capStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 6, fontSize: 11.5, fontWeight: 600,
  textTransform: 'uppercase', letterSpacing: '.03em',
};
const rowStyle: React.CSSProperties = {
  width: '100%', textAlign: 'left', background: 'none', border: 'none', cursor: 'pointer',
  padding: '6px 4px', borderRadius: R.sm, fontFamily: FONT.sans, display: 'block',
};
const snippetStyle: React.CSSProperties = {
  fontFamily: FONT.mono, fontSize: 11, color: C.textMuted, marginLeft: 13, marginTop: 2,
  display: 'block', overflowWrap: 'anywhere',
};
