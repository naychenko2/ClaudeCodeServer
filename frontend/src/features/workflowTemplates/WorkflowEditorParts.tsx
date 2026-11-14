// Плашки и футер редактора шаблона процесса (F21): вынесены из WorkflowTemplatesEditor,
// чтобы их разметку можно было проверять без сети и состояния редактора.

import { AlertTriangle, Info, RotateCcw, Trash2 } from 'lucide-react';
import { C, FS, SP } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Button, Notice } from '../../components/ui';
import {
  BUILTIN_DELETE_HINT, BUILTIN_NOTICE, conflictTitle, errorsHeading, showWriteButton,
  type ErrorItem, type SaveFailure,
} from './workflowEditorLogic';

export interface EditorNoticesProps {
  isBuiltIn: boolean;
  readOnly: boolean;
  canReset: boolean;
  failure: SaveFailure | null;
  conflictVersion: number | null;
  errorItems: ErrorItem[];
  onReset: () => void;
  onReload: () => void;
  onSaveAsNew: () => void;
  onJumpToStage: (index: number) => void;
}

export function EditorNotices(p: EditorNoticesProps): React.ReactElement {
  const { failure } = p;
  // Тач-цель на мобиле (D2): «Сбросить к исходному» — md (≥40 px), на десктопе без изменений.
  const isMobile = useIsMobile();
  return (
    <>
      {p.isBuiltIn && !p.readOnly && (
        <Notice tone="info" icon={Info}
          action={p.canReset ? (
            <Button size={isMobile ? 'md' : 'sm'} variant="secondary" onClick={p.onReset}
              leftIcon={<RotateCcw size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}>Сбросить к исходному</Button>
          ) : undefined}
        >{BUILTIN_NOTICE}</Notice>
      )}
      {failure?.kind === 'conflict' && (
        <Notice tone="danger" icon={AlertTriangle} title={conflictTitle(p.conflictVersion)}
          action={(
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm }}>
              <Button size="sm" variant="secondary" onClick={p.onReload}>
                {p.conflictVersion ? `Загрузить v${p.conflictVersion} — мои правки пропадут` : 'Загрузить версию сервера — мои правки пропадут'}
              </Button>
              <Button size="sm" variant="secondary" onClick={p.onSaveAsNew}>Сохранить мои правки новым шаблоном</Button>
            </div>
          )}
        />
      )}
      {failure && failure.kind === 'failed' && (
        <Notice tone="danger" icon={AlertTriangle} title={failure.message}>
          {failure.generalErrors.map((e, i) => <div key={i}>{e}</div>)}
        </Notice>
      )}
      {p.errorItems.length > 0 && (
        <Notice tone="danger" icon={AlertTriangle} title={errorsHeading(p.errorItems.length)} data-testid="template-error-summary">
          <ul style={{ margin: 0, paddingLeft: SP.lg }}>
            {p.errorItems.map((it, i) => (
              <li key={i}>
                {it.stageIndex !== null ? (
                  <button
                    type="button" onClick={() => p.onJumpToStage(it.stageIndex as number)}
                    style={{
                      background: 'none', border: 'none', padding: 0, cursor: 'pointer', font: 'inherit',
                      color: 'inherit', textDecoration: 'underline', textAlign: 'left',
                    }}
                  >{it.text}</button>
                ) : it.text}
              </li>
            ))}
          </ul>
        </Notice>
      )}
    </>
  );
}

export interface EditorFooterProps {
  isNew: boolean;
  isBuiltIn: boolean;
  isMobile: boolean;
  canWrite: boolean;
  readOnly: boolean;
  canSave: boolean;
  dirty: boolean;
  busy: boolean;
  onDiscard: () => void;
  onSave: () => void;
  onDelete: () => void;
}

export function EditorFooter(p: EditorFooterProps): React.ReactElement {
  return (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, alignItems: 'center' }}>
      {!p.isNew && p.canWrite && !p.readOnly && !p.isMobile && (
        <Button
          variant="ghost" size="sm"
          ariaDisabled={p.isBuiltIn}
          title={p.isBuiltIn ? BUILTIN_DELETE_HINT : undefined}
          onClick={p.onDelete}
          leftIcon={<Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
        >Удалить</Button>
      )}
      <div style={{ flex: 1 }} />
      {p.dirty && !p.busy && !p.readOnly && <span style={{ fontSize: FS.xs, color: C.textMuted }}>Есть несохранённые изменения</span>}
      {showWriteButton(p.canWrite, p.readOnly) && (
        <>
          <Button variant="secondary" disabled={!p.dirty || p.busy} onClick={p.onDiscard}>Отменить</Button>
          <Button onClick={p.onSave} disabled={!p.canSave} loading={p.busy}>
            {p.busy ? 'Сохраняю…' : 'Сохранить'}
          </Button>
        </>
      )}
    </div>
  );
}
