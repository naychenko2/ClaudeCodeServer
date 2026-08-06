import { Button } from './Button';
import type { ButtonVariant } from './Button';

interface ModalActionsProps {
  // Основное действие
  confirmLabel: string;
  onConfirm: () => void;
  confirmVariant?: ButtonVariant;   // primary | danger
  loading?: boolean;
  confirmDisabled?: boolean;
  // Вторичное действие
  cancelLabel?: string;
  onCancel: () => void;
}

// Единая пара действий для футера диалога — на всех ширинах в один ряд:
// «Отмена» слева, основное действие справа. Акцент на основном — за счёт большей доли ширины.
export function ModalActions({
  confirmLabel, onConfirm, confirmVariant = 'primary',
  loading, confirmDisabled, cancelLabel = 'Отмена', onCancel,
}: ModalActionsProps) {
  return (
    <div style={{ display: 'flex', gap: 10, width: '100%' }}>
      <div style={{ flex: 1 }}>
        <Button variant="secondary" size="md" fullWidth onClick={onCancel}>
          {cancelLabel}
        </Button>
      </div>
      <div style={{ flex: 1.5 }}>
        <Button
          variant={confirmVariant}
          size="md"
          fullWidth
          loading={loading}
          disabled={confirmDisabled}
          onClick={onConfirm}
        >
          {confirmLabel}
        </Button>
      </div>
    </div>
  );
}
