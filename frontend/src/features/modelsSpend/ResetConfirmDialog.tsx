import { Button, Modal } from '../../components/ui';
import { MODAL_W } from '../../lib/design';

interface ResetConfirmDialogProps {
  open: boolean;
  title: string;
  body: string;
  confirmLabel?: string;
  busy?: boolean;
  // 'danger' — для необратимого сброса; 'primary' — для согласия на общее действие
  // (правка общей цепочки админом и т. п.). По умолчанию 'danger'.
  variant?: 'danger' | 'primary';
  onCancel: () => void;
  onConfirm: () => void;
}

// Диалог подтверждения необратимого сброса настроек моделей к значению по умолчанию —
// переиспользуемая обёртка над Modal (образец — deleteConfirmModal в TaskDetailsPane).
// Тексты и числа собирает вызывающий: диалог сам ничего не считает и не запрашивает.
export function ResetConfirmDialog({
  open, title, body, confirmLabel = 'Сбросить', busy = false,
  variant = 'danger', onCancel, onConfirm,
}: ResetConfirmDialogProps) {
  if (!open) return null;
  return (
    <Modal
      title={title}
      subtitle={body}
      width={MODAL_W.confirm}
      onClose={onCancel}
      footer={
        <>
          <Button variant="ghost" disabled={busy} onClick={onCancel}>Отмена</Button>
          <Button variant={variant} loading={busy} disabled={busy} onClick={onConfirm}>{confirmLabel}</Button>
        </>
      }
    />
  );
}
