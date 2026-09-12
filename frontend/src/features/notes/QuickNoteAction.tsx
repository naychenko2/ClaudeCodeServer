import { useState } from 'react';
import { NotebookPen } from 'lucide-react';
import { ensureNotesLoaded } from '../../lib/notes';
import { NewNoteDialog } from './NewNoteDialog';
import { openNote } from './openNote';
import type { QuickActionNoteCtx } from '../../lib/subsystems/registryCore';

// Кнопка «Новая заметка» на дашборде: каркас (QuickActions) отдаёт свой ActionButton
// через контекст, фича держит состояние диалога. Вынесено из QuickActions — тот
// больше не импортирует диалог заметок напрямую.
export function QuickNoteAction({ ActionButton }: QuickActionNoteCtx) {
  const [open, setOpen] = useState(false);
  const openNew = () => { void ensureNotesLoaded(); setOpen(true); };
  return (
    <>
      <ActionButton icon={<NotebookPen size={15} strokeWidth={2} />} label="Новая заметка" onClick={openNew} />
      {open && (
        <NewNoteDialog
          onCreated={id => { setOpen(false); openNote(id); }}
          onClose={() => setOpen(false)}
        />
      )}
    </>
  );
}
