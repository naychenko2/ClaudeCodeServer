// Поле «Кусок»: «Начало», «Конец», «до конца». Значение — выделение нити в сторе, общее с волной
// карточки в ленте (panel/piece.ts): протянули по волне — цифры здесь, вписали цифры — выделение там.

import { useEffect, useState } from 'react';
import { X } from 'lucide-react';
import { Checkbox, IconButton, TextField, C, FS, SP } from 'aihome_shell/kit';
import { fmtSelection, fmtTime, TO_END } from '../player/selection';
import { setSelection } from '../thread/threadStore';
import { commitPiece, pieceFromText, pieceText, readPiece, type PieceText } from './piece';
import { Hint, ic, Label } from './primitives';

export interface PieceBinding { sessionId: string; threadId: string; versionId: string }

const sameSel = (a: { start: number; end: number } | null, b: { start: number; end: number } | null) =>
  a === b || (!!a && !!b && a.start === b.start && a.end === b.end);

function Num({ label, value, onChange, disabled }: { label: string; value: string; onChange: (s: string) => void; disabled?: boolean }) {
  return (
    <div style={{ flex: '1 1 110px', minWidth: 0 }}>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, marginBottom: SP.xxs }}>{label}, с</div>
      <TextField value={value} onChange={onChange} placeholder="—" disabled={disabled} />
    </div>
  );
}

export function PieceField({ binding, aside }: { binding: PieceBinding; aside?: string }) {
  const { sessionId, threadId, versionId } = binding;
  const sel = readPiece(sessionId, threadId);
  const [draft, setDraft] = useState<PieceText>(() => pieceText(sel));
  const selKey = sel ? `${sel.start}|${sel.end}` : '';
  useEffect(() => {
    // Выделение сменилось снаружи (волна в ленте) — поле показывает его, если черновик о другом
    const mine = pieceFromText(draft);
    if (mine === undefined || !sameSel(mine, sel)) setDraft(pieceText(sel));
    // eslint-disable-next-line react-hooks/exhaustive-deps -- сверяемся только с новым выделением
  }, [selKey, threadId]);

  const edit = (patch: Partial<PieceText>) => {
    const next = { ...draft, ...patch };
    setDraft(next);
    commitPiece(sessionId, threadId, versionId, next);
  };

  return (
    <div data-field="piece">
      <Label aside={aside}>Кусок</Label>
      <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap', alignItems: 'flex-end' }}>
        <Num label="Начало" value={draft.start} onChange={v => edit({ start: v })} />
        <Num label="Конец" value={draft.toEnd ? '' : draft.end} disabled={draft.toEnd} onChange={v => edit({ end: v })} />
        <label style={{ display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.sm, color: C.textPrimary, paddingBottom: SP.xs }}>
          <Checkbox checked={draft.toEnd} onChange={v => edit({ toEnd: v })} ariaLabel="До конца" />
          до конца
        </label>
      </div>
      {sel ? (
        <div data-piece-linked="" style={{ display: 'flex', alignItems: 'center', gap: SP.xs, marginTop: SP.xxs }}>
          <span style={{ flex: 1, minWidth: 0, fontSize: FS.xs, color: C.textMuted }}>
            = выделение на волне в ленте · {sel.end === TO_END ? `${fmtTime(sel.start)} – до конца` : fmtSelection(sel, sel.end)}
          </span>
          <IconButton size="xs" title="Снять кусок" ariaLabel="Снять кусок" onClick={() => setSelection(sessionId, threadId, null)}>{ic(X)}</IconButton>
        </div>
      ) : (
        <Hint>Протяните по волне карточки в ленте или впишите время: можно «1:06.2»</Hint>
      )}
    </div>
  );
}
