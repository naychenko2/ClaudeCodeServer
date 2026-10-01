// Секции «Операция» и «Режим подбора» вкладки «Настройки» панели «Картинки» (ADR-021 §3,
// записка image-editor-v4-panel-proposal.md). «Авто» — нынешний pickOp, строка под пилюлями
// называет фактическую операцию; режим подбора — только у модели «Авто».

import { Button, C, SP } from 'aihome_shell/kit';
import { AUTO_MODEL } from '../api';
import { OUTPAINT_RATIOS } from '../editorInputs';
import { Label, Opt, type Launch } from '../strip/settings/primitives';
import { EDIT_MODES, opBlockReason, opHint, PANEL_OPS, setPanelChoice } from './panelOp';

export function OpSection({ projectId, L }: { projectId: string; L: Launch }) {
  const { op, ratio } = L.choice;
  const [head, word, tail] = opHint(op, L.hasImage, L.hasMask);
  return (
    <>
      <Label>Операция</Label>
      <div data-image-ops="" style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
        {PANEL_OPS.map(o => {
          const why = opBlockReason(o.op, L.hasImage, L.hasMask);
          const on = o.op === op;
          return (
            <Button key={o.op} size="xs" pill variant={on ? 'ghostAccent' : 'secondary'}
              disabled={!!why} title={why || undefined}
              style={{ border: `1px solid ${on ? C.accent : C.border}` }}
              onClick={() => setPanelChoice(projectId, { op: o.op })}>
              {o.label}
            </Button>
          );
        })}
      </div>
      <div data-image-op-hint="" style={{ marginTop: SP.xs, color: C.textMuted }}>
        {head}{word && <b style={{ color: C.textHeading }}>{word}</b>}{tail}
      </div>
      {L.op === 'outpaint' && (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap', marginTop: SP.sm, color: C.textSecondary }}>
          Дорисовать до пропорций:
          {OUTPAINT_RATIOS.map(r => (
            <Button key={r} size="xs" pill variant={r === ratio ? 'ghostAccent' : 'secondary'}
              onClick={() => setPanelChoice(projectId, { ratio: r })}>{r}</Button>
          ))}
        </div>
      )}
    </>
  );
}

// Режим подбора модели сервером; у явной модели его нет — она сама и есть выбор
export function ModeSection({ projectId, L }: { projectId: string; L: Launch }) {
  if (L.model?.id !== AUTO_MODEL || L.quickAction) return null;
  return (
    <>
      <Label>Режим подбора</Label>
      <div data-image-modes="" style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
        {EDIT_MODES.map(([mode, name, hint]) => (
          <Opt key={mode} on={L.choice.mode === mode} name={name} hint={hint}
            onClick={() => setPanelChoice(projectId, { mode })} />
        ))}
      </div>
    </>
  );
}
