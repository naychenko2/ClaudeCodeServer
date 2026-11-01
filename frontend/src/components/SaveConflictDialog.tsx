import { useState } from 'react';
import { AlertTriangle } from 'lucide-react';
import { C, FONT, FS, MODAL_W, R, SP } from '../lib/design';
import { relTime } from '../lib/gitFormat';
import { DiffView } from './DiffView';
import { Button } from './ui/Button';
import { Modal } from './ui/Modal';
import { Notice } from './ui/Notice';
import { UserAvatar } from './ui/UserAvatar';
import { useIsMobileModal } from './ui/useIsMobileModal';

// Данные конфликта по контракту К8: тело 409 на сохранение файла или заметки.
export interface SaveConflict {
  code: 'file_changed' | 'note_changed';
  sha256: string;                                  // актуальный хеш версии в проекте
  author?: { userId?: string; displayName: string };
  changedAt?: string;                              // ISO
  sourceLabel?: string;                            // «чат „Меню на зиму“» / «приватный чат»; не пришёл — не показываем
}

interface Props {
  conflict: SaveConflict;
  fileName: string;
  // Имя копии, которое получится при «Сохранить мою копией» (conflictCopyName)
  copyName: string;
  // Unified-diff «в проекте → ваша версия»; loading — скелетон; noDiff — Office и draw.io
  diff?: string | null;
  diffLoading?: boolean;
  noDiff?: boolean;
  mineAt?: string;                                 // время вашей версии для варианта без диффа
  again?: boolean;                                 // повторный конфликт
  copyError?: boolean;
  busy?: boolean;
  onSaveCopy: () => void;
  onReplace: () => void;
  onTakeTheirs: () => void;
  onClose: () => void;
}

const DIFF_MAX_H = 260;

// Диалог конфликта сохранения. Главное действие — «Сохранить мою копией»: ничего не теряется.
// Десктоп: взять чужое · заменить · копия (primary справа). Мобила: столбиком, копия первой.
export function SaveConflictDialog({
  conflict, fileName, copyName, diff, diffLoading, noDiff, mineAt, again, copyError, busy,
  onSaveCopy, onReplace, onTakeTheirs, onClose,
}: Props) {
  const isMobile = useIsMobileModal();
  const [expanded, setExpanded] = useState(false);
  const author = conflict.author?.displayName || 'Другой участник';
  const when = conflict.changedAt ? relTime(conflict.changedAt) : '';
  const title = conflict.code === 'note_changed' ? 'Заметку изменили, пока вы правили' : 'Файл изменили, пока вы правили';
  const what = conflict.code === 'note_changed' ? 'заметку' : 'файл';

  const copy = <Button variant="primary" size="md" fullWidth disabled={busy} onClick={onSaveCopy}>Сохранить мою копией</Button>;
  const replace = <Button variant="secondary" size="md" fullWidth disabled={busy} onClick={onReplace}>Заменить своей</Button>;
  const take = <Button variant="ghost" size="md" fullWidth disabled={busy} onClick={onTakeTheirs}>Взять чужую версию</Button>;

  const footer = (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, width: '100%' }}>
      <div
        data-testid="conflict-actions"
        style={{ display: 'flex', flexDirection: isMobile ? 'column' : 'row', gap: SP.md, width: '100%' }}
      >
        {isMobile ? <>{copy}{replace}{take}</> : <>{take}{replace}{copy}</>}
      </div>
      <div style={{ fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.sans }}>
        Копия: <span style={{ fontFamily: FONT.mono, color: C.textSecondary, overflowWrap: 'anywhere' }}>{copyName}</span>
      </div>
    </div>
  );

  const showDiff = !isMobile || expanded;

  return (
    <Modal width={MODAL_W.wide} title={title} footer={footer} onClose={onClose}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.base, color: C.textSecondary, fontFamily: FONT.sans }}>
          <UserAvatar userId={conflict.author?.userId ?? author} displayName={author} size={20} />
          <span style={{ minWidth: 0 }}>
            <b style={{ color: C.textPrimary }}>{author}</b>
            {when && ` · ${when}`}
            {conflict.sourceLabel && ` · ${conflict.sourceLabel}`}
            {' — '}{what}{' '}
            <span style={{ fontFamily: FONT.mono, overflowWrap: 'anywhere' }}>{fileName}</span>
          </span>
        </div>

        {again && (
          <Notice tone="warning" icon={AlertTriangle}>
            {noDiff ? 'Файл изменили ещё раз. Выберите заново.' : 'Файл изменили ещё раз — показываем свежую версию. Выберите заново.'}
          </Notice>
        )}
        {copyError && (
          <Notice tone="danger" icon={AlertTriangle}>
            Копию сохранить не удалось — ваш текст остался в редакторе. Попробуйте ещё раз.
          </Notice>
        )}

        {noDiff ? (
          <div style={{ fontSize: FS.base, color: C.textSecondary, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', gap: SP.xs }}>
            <span>ваша версия{mineAt ? ` · ${mineAt}` : ''}</span>
            <span>в проекте · {author}{when ? ` · ${when}` : ''}</span>
          </div>
        ) : (
          <>
            {isMobile && (
              <Button variant="ghost" size="md" fullWidth ariaExpanded={expanded} onClick={() => setExpanded(v => !v)}>
                {expanded ? 'Скрыть разницу' : 'Показать разницу'}
              </Button>
            )}
            {showDiff && (
              <div data-testid="conflict-diff">
                <div style={{ fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.sans, marginBottom: SP.xs }}>В проекте → ваша версия</div>
                <div style={{
                  maxHeight: DIFF_MAX_H, overflow: 'auto', background: C.bgCard,
                  border: `1px solid ${C.border}`, borderRadius: R.md,
                }}>
                  {diffLoading ? (
                    <div aria-busy="true" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, padding: SP.md }}>
                      {[70, 90, 55, 80].map(w => (
                        <div key={w} style={{ height: 10, width: `${w}%`, background: C.bgPanel, borderRadius: R.sm }} />
                      ))}
                    </div>
                  ) : diff ? <DiffView diff={diff} /> : (
                    <div style={{ padding: SP.md, fontSize: FS.sm, color: C.textMuted, fontFamily: FONT.sans }}>Различий в тексте нет.</div>
                  )}
                </div>
              </div>
            )}
          </>
        )}

        <div style={{ fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.sans }}>Обе версии останутся в истории.</div>
      </div>
    </Modal>
  );
}
