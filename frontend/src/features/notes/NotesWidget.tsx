import { useEffect, useState } from 'react';
import { Share2 } from 'lucide-react';
import type { NoteSummary } from '../../types';
import { api } from '../../lib/api';
import { C, FONT } from '../../lib/design';
import { ensureNotesLoaded } from '../../lib/notes';
import { subsystemTabValue, type HubTabValue } from '../../components/HubTabs';
import { useSubsystem } from '../../lib/subsystems';
import { WidgetCard, WidgetAction, WidgetEmpty, relTime } from '../home/WidgetCard';
import { NewNoteDialog } from './NewNoteDialog';
import { openNote } from './openNote';

// «Заметки»: последние изменённые по всем источникам. Виджет живёт в фиче заметок
// (раньше лежал в features/home и импортировал диалог заметки напрямую) — дашборд
// рисует его вкладом слота home-widget.
// Гейт по подсистеме: выключена — виджет не рендерится.
export function NotesWidget({ onHubTab }: { onHubTab: (t: HubTabValue) => void }) {
  const notesOn = useSubsystem('notes');
  const [notes, setNotes] = useState<NoteSummary[]>([]);
  const [newOpen, setNewOpen] = useState(false);

  // Хук вызываем всегда (Rules of Hooks): при выключенной подсистеме список
  // не грузим — гейт внутри эффекта, а не перед ним.
  useEffect(() => {
    if (!notesOn) return;
    api.notes.list().then(setNotes).catch(() => {});
  }, [notesOn]);

  // Стор заметок нужен диалогу (автодополнение папок) — подгружаем при открытии
  const openNew = () => { void ensureNotesLoaded(); setNewOpen(true); };

  // Гейт по подсистеме — в разметке, а не в хуках: число хуков не зависит от флага.
  if (!notesOn) return null;

  const recent = [...notes]
    .sort((a, b) => (b.updatedAt ?? '').localeCompare(a.updatedAt ?? ''))
    .slice(0, 5);

  return (
    <WidgetCard
      icon={<Share2 size={16} strokeWidth={2} />}
      title="Заметки"
      onCreate={openNew}
      createTitle="Новая заметка"
      action={<WidgetAction label="Все заметки →" onClick={() => onHubTab(subsystemTabValue('notes'))} />}
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
