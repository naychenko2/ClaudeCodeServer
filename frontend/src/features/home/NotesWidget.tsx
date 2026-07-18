import { useEffect, useState } from 'react';
import { Share2 } from 'lucide-react';
import type { NoteSummary } from '../../types';
import { api } from '../../lib/api';
import { C, FONT } from '../../lib/design';
import { ensureNotesLoaded } from '../../lib/notes';
import type { HubTab } from '../../components/HubTabs';
import { NewNoteDialog } from '../notes/NewNoteDialog';
import { WidgetCard, WidgetAction, WidgetEmpty, relTime } from './WidgetCard';

// Открыть заметку через общий SPA-канал (обработчик #/notes/{id} в App)
export function openNote(id: string): void {
  window.dispatchEvent(new CustomEvent('cc-open-url', {
    detail: { url: `#/notes/${encodeURIComponent(id)}` },
  }));
}

// «Заметки»: последние измененные по всем источникам.
export function NotesWidget({ onHubTab }: { onHubTab: (t: HubTab) => void }) {
  const [notes, setNotes] = useState<NoteSummary[]>([]);
  const [newOpen, setNewOpen] = useState(false);

  useEffect(() => {
    api.notes.list().then(setNotes).catch(() => {});
  }, []);

  // Стор заметок нужен диалогу (автодополнение папок) — подгружаем при открытии
  const openNew = () => { void ensureNotesLoaded(); setNewOpen(true); };

  const recent = [...notes]
    .sort((a, b) => (b.updatedAt ?? '').localeCompare(a.updatedAt ?? ''))
    .slice(0, 5);

  return (
    <WidgetCard
      icon={<Share2 size={16} strokeWidth={2} />}
      title="Заметки"
      onCreate={openNew}
      createTitle="Новая заметка"
      action={<WidgetAction label="Все заметки →" onClick={() => onHubTab('notes')} />}
    >
      {recent.length === 0
        ? <WidgetEmpty text="Заметок пока нет." />
        : (
          <div style={{ display: 'flex', flexDirection: 'column' }}>
            {recent.map(n => (
              <button
                key={n.id}
                onClick={() => openNote(n.id)}
                style={{
                  display: 'flex', alignItems: 'center', gap: 9, width: '100%', textAlign: 'left',
                  background: 'none', border: 'none', borderRadius: 8, padding: '7px 8px',
                  margin: '0 -8px', cursor: 'pointer', minWidth: 0,
                }}
                onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
                onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}
              >
                <span style={{
                  fontFamily: FONT.sans, fontSize: 13, color: C.textPrimary, flex: 1, minWidth: 0,
                  whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
                }}>
                  {n.title}
                </span>
                <span style={{
                  fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted, flexShrink: 0,
                  maxWidth: 110, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
                }}>
                  {n.sourceLabel}
                </span>
                <span style={{ fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted, flexShrink: 0 }}>
                  {relTime(n.updatedAt)}
                </span>
              </button>
            ))}
          </div>
        )}
      {newOpen && (
        <NewNoteDialog
          onCreated={id => { setNewOpen(false); openNote(id); }}
          onClose={() => setNewOpen(false)}
        />
      )}
    </WidgetCard>
  );
}
