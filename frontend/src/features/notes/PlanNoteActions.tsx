import { useContext, useState, type CSSProperties } from 'react';
import { Check } from 'lucide-react';
import { C, FONT, FS, R, SP, useSubsystem, Button, IconButton, ChatProjectContext } from 'aihome_shell/kit';
import { saveChatNote, openNoteById } from './saveToNote';
import { IconNotes } from './shared';
import type { PlanChipCtx, PlanButtonCtx } from '../../lib/subsystems/registryCore';

// Заметка из плана чата: чип в навигаторе плана (PlanSection) и иконка-кнопка в
// карточке согласования (PlanReviewView). Вынесено из обоих компонентов — каркас
// рисует вклады слота plan-action.

// Копия стиля чипа навигатора плана (navChip из PlanSection): вклад обязан
// выглядеть ровно как прежний чип.
const navChip: CSSProperties = {
  height: 28, padding: '0 10px', borderRadius: R.md, cursor: 'pointer',
  display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0,
  fontFamily: FONT.sans, fontSize: 12, fontWeight: 600, whiteSpace: 'nowrap',
  border: `1px solid ${C.border}`, background: C.bgInset, color: C.textSecondary,
};

export function PlanSaveChip({ plan, projectId }: PlanChipCtx) {
  const notesOn = useSubsystem('notes');
  const [savedId, setSavedId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  if (!notesOn) return null;
  const save = () => {
    if (busy) return;
    if (savedId) { openNoteById(savedId); return; }
    setBusy(true);
    saveChatNote({ text: plan, projectId, titlePrefix: 'План: ' })
      .then(n => { setSavedId(n.id); setTimeout(() => setSavedId(null), 6000); })
      .catch(() => {})
      .finally(() => setBusy(false));
  };
  return (
    <button onClick={save} title={savedId ? 'Сохранено — открыть заметку' : 'Сохранить план в заметку'}
      style={savedId
        ? { ...navChip, background: C.successBg, border: `1px solid ${C.successBg}`, color: C.successText }
        : { ...navChip, opacity: busy ? 0.6 : 1 }}>
      <IconNotes size={13} />
      {savedId ? 'открыть' : 'в заметку'}
    </button>
  );
}

export function PlanSaveButton({ plan, online }: PlanButtonCtx) {
  const project = useContext(ChatProjectContext);
  const notesOn = useSubsystem('notes');
  const [savedId, setSavedId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  if (!online || !notesOn) return null;
  const save = () => {
    if (busy || savedId) return;
    setBusy(true);
    saveChatNote({ text: plan, projectId: project?.id, titlePrefix: 'План: ' })
      .then(n => { setSavedId(n.id); setTimeout(() => setSavedId(null), 6000); })
      .catch(() => {})
      .finally(() => setBusy(false));
  };
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xxs, marginLeft: 'auto', flexShrink: 0 }}>
      {savedId && (
        <Button variant="ghost" size="xs" onClick={() => openNoteById(savedId)}
          style={{ color: C.successText, fontSize: FS.xs }}>
          Открыть
        </Button>
      )}
      <IconButton
        size="xs"
        tone="muted"
        ariaLabel={savedId ? 'Сохранено в заметки' : 'Сохранить план в заметку'}
        disabled={busy}
        onClick={save}
      >
        {savedId
          ? <Check size={14} color={C.success} strokeWidth={3} style={{ flexShrink: 0 }} />
          : <IconNotes size={14} />}
      </IconButton>
    </span>
  );
}
