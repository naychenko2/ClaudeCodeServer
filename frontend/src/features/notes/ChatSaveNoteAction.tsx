import { useState } from 'react';
import { Check, AlertCircle } from 'lucide-react';
import { C } from '../../lib/design';
import { useSubsystem } from '../../lib/subsystems';
import { saveChatNote, openNoteById } from './saveToNote';
import { IconNotes } from './shared';
import type { ChatItemSaveNoteCtx } from '../../lib/subsystems/registryCore';

// Кнопка «В заметку» под ответом ассистента: сохранить текст в базу заметок и
// открыть созданную. Вынесена из TextMessageView, чтобы каркас чата не импортировал
// фичу напрямую — он рисует вклад слота chat-item-action. Стили — копия иконки-кнопки
// строки действий поста (postIconBtn/postIconHover из ChatItemView): вклад обязан
// выглядеть ровно как раньше.

const iconBtn: React.CSSProperties = {
  display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
  width: 20, height: 20, borderRadius: 5, border: 'none', background: 'none',
  color: C.textMuted, cursor: 'pointer', fontFamily: 'inherit', padding: 0,
};

const iconHover = {
  onMouseEnter: (e: React.MouseEvent<HTMLButtonElement>) => { e.currentTarget.style.color = C.textHeading; },
  onMouseLeave: (e: React.MouseEvent<HTMLButtonElement>) => { e.currentTarget.style.color = C.textMuted; },
};

export function ChatSaveNoteAction({ text, projectId, online }: ChatItemSaveNoteCtx) {
  const notesOn = useSubsystem('notes');
  const [savedNoteId, setSavedNoteId] = useState<string | null>(null);
  const [savingNote, setSavingNote] = useState(false);
  const [noteError, setNoteError] = useState(false);

  if (!online || !notesOn) return null;

  const saveNote = () => {
    if (savingNote || savedNoteId) return;
    setSavingNote(true);
    setNoteError(false);
    saveChatNote({ text, projectId })
      .then(n => { setSavedNoteId(n.id); setTimeout(() => setSavedNoteId(null), 6000); })
      .catch(() => { setNoteError(true); setTimeout(() => setNoteError(false), 3000); })
      .finally(() => setSavingNote(false));
  };

  return (
    <>
      {savedNoteId && (
        <button onClick={() => openNoteById(savedNoteId)}
          style={{ ...iconBtn, width: 'auto', padding: '0 8px', fontSize: 11, fontWeight: 600, color: C.successText }}
          title="Открыть созданную заметку">
          Открыть
        </button>
      )}
      <button onClick={saveNote} disabled={savingNote} style={{ ...iconBtn, opacity: savingNote ? 0.5 : 1 }}
        title={noteError ? 'Не удалось сохранить' : savedNoteId ? 'Сохранено в заметки' : 'Сохранить в заметку'}
        aria-label="Сохранить в заметку"
        {...iconHover}>
        {savedNoteId
          ? <Check size={14} color={C.success} strokeWidth={3} style={{ flexShrink: 0 }} />
          : noteError
            ? <AlertCircle size={13} color={C.dangerText} strokeWidth={2} style={{ flexShrink: 0 }} />
            : <IconNotes size={13} />}
      </button>
    </>
  );
}
