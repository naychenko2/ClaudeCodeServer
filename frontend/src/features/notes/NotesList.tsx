import { useMemo } from 'react';
import type { NoteSummary } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { CollapseGroup, SourceDot } from './shared';

interface Group { source: string; label: string; notes: NoteSummary[] }

// Список заметок, сгруппированный по источнику (личный vault + проекты).
// Плоское пространство имён внутри группы (как в Obsidian), не файловое дерево.
export function NotesList({ notes, selectedId, onSelect }: {
  notes: NoteSummary[];
  selectedId: string | null;
  onSelect: (id: string) => void;
}) {
  const groups = useMemo<Group[]>(() => {
    const map = new Map<string, Group>();
    for (const n of notes) {
      let g = map.get(n.source);
      if (!g) { g = { source: n.source, label: n.sourceLabel, notes: [] }; map.set(n.source, g); }
      g.notes.push(n);
    }
    // Личный vault первым, дальше проекты по алфавиту
    return [...map.values()].sort((a, b) =>
      a.source === 'personal' ? -1 : b.source === 'personal' ? 1 : a.label.localeCompare(b.label, 'ru'));
  }, [notes]);

  if (notes.length === 0)
    return (
      <div style={{ padding: '20px 12px', color: C.textMuted, fontSize: 13, fontFamily: FONT.sans, lineHeight: 1.6 }}>
        Пока нет заметок. Создай первую или попроси Claude законспектировать что-нибудь.
      </div>
    );

  return (
    <div style={{ padding: '8px 8px 20px' }}>
      {groups.map(g => (
        <CollapseGroup
          key={g.source}
          defaultOpen
          title={
            <span style={{ display: 'flex', alignItems: 'center', gap: 7 }}>
              <SourceDot source={g.source} />
              <span style={{ fontSize: 12.5, fontWeight: 500, color: C.textPrimary }}>{g.label}</span>
            </span>
          }
          tail={<span style={{ fontSize: 11, color: C.textMuted }}>{g.notes.length}</span>}
        >
          {g.notes.map(n => {
            const active = n.id === selectedId;
            return (
              <button
                key={n.id}
                onClick={() => onSelect(n.id)}
                title={n.title}
                style={{
                  width: '100%', textAlign: 'left', border: 'none', cursor: 'pointer',
                  padding: '5px 8px 5px 24px', borderRadius: R.sm, fontFamily: FONT.sans,
                  fontSize: 12.5, marginBottom: 1,
                  background: active ? C.accentMuted : 'transparent',
                  color: active ? C.textHeading : C.textSecondary,
                  fontWeight: active ? 500 : 400,
                  whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
                }}
              >{n.title}</button>
            );
          })}
        </CollapseGroup>
      ))}
    </div>
  );
}
