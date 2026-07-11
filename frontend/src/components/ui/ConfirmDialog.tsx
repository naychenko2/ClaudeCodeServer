import { useState } from 'react';
import type { ReactNode } from 'react';
import { Modal } from './Modal';
import { ModalActions } from './ModalActions';
import type { ButtonVariant } from './Button';
import { MODAL_W } from '../../lib/design';

interface ConfirmDialogProps {
  title: string;
  subtitle?: ReactNode;
  confirmLabel?: string;
  confirmVariant?: ButtonVariant;   // primary | danger
  onConfirm: () => void | Promise<void>;
  onCancel: () => void;
}

// Компактный диалог подтверждения — единая замена window.confirm().
// Тонкая обёртка над Modal + ModalActions (образец — confirm-модалка удаления в ChatList).
// Асинхронный onConfirm показывает спиннер на кнопке до завершения.
export function ConfirmDialog({
  title, subtitle, confirmLabel = 'Подтвердить', confirmVariant = 'primary',
  onConfirm, onCancel,
}: ConfirmDialogProps) {
  const [busy, setBusy] = useState(false);

  const handleConfirm = () => {
    const result = onConfirm();
    if (result instanceof Promise) {
      setBusy(true);
      // Закрытие диалога — забота вызывающего (в onConfirm/после него)
      void result.finally(() => setBusy(false));
    }
  };

  return (
    <Modal
      title={title}
      subtitle={subtitle}
      width={MODAL_W.confirm}
      onClose={onCancel}
      footer={
        <ModalActions
          confirmLabel={confirmLabel}
          confirmVariant={confirmVariant}
          loading={busy}
          onConfirm={handleConfirm}
          onCancel={onCancel}
        />
      }
    />
  );
}
