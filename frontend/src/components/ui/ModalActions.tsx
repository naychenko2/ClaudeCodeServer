import { useEffect, useState } from 'react';
import { Button } from './Button';
import type { ButtonVariant } from './Button';
import { MOBILE_MAX } from '../../lib/breakpoints';

const MOBILE_BP = MOBILE_MAX + 1; // единый порог с раскладкой (см. lib/breakpoints)

// Брейкпоинт мобилы для диалогов (тот же, что в Modal): узкий экран → шторка/вертикальные действия.
// Экспортируется для кастомных футеров (напр. диалог с тремя исходами).
export function useIsMobileModal() {
  const [mobile, setMobile] = useState(() =>
    typeof window !== 'undefined'
      ? window.matchMedia(`(max-width: ${MOBILE_BP - 1}px)`).matches
      : false
  );
  useEffect(() => {
    const mq = window.matchMedia(`(max-width: ${MOBILE_BP - 1}px)`);
    const handler = (e: MediaQueryListEvent) => setMobile(e.matches);
    setMobile(mq.matches);
    if (mq.addEventListener) mq.addEventListener('change', handler);
    else mq.addListener(handler);
    return () => {
      if (mq.removeEventListener) mq.removeEventListener('change', handler);
      else mq.removeListener(handler);
    };
  }, []);
  return mobile;
}

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
