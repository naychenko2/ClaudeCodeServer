import { useEffect, useState } from 'react';
import { Button } from './Button';
import type { ButtonVariant } from './Button';

const MOBILE_BP = 768;

// Брейкпоинт мобилы для диалогов (тот же, что в Modal): <768 → шторка/вертикальные действия.
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

// Единая пара действий для футера диалога.
//  • Десктоп/планшет: в ряд (Отмена слева, основное справа), кнопки md.
//  • Мобила (шторка): в колонку, основное действие сверху и крупнее (lg), «Отмена» снизу.
export function ModalActions({
  confirmLabel, onConfirm, confirmVariant = 'primary',
  loading, confirmDisabled, cancelLabel = 'Отмена', onCancel,
}: ModalActionsProps) {
  const isMobile = useIsMobileModal();

  const confirmBtn = (
    <Button
      variant={confirmVariant}
      size={isMobile ? 'lg' : 'md'}
      fullWidth
      loading={loading}
      disabled={confirmDisabled}
      onClick={onConfirm}
    >
      {confirmLabel}
    </Button>
  );
  const cancelBtn = (
    <Button
      variant="secondary"
      size={isMobile ? 'lg' : 'md'}
      fullWidth
      onClick={onCancel}
    >
      {cancelLabel}
    </Button>
  );

  if (isMobile) {
    // Основное действие — сверху и заметнее, отмена — снизу
    return <>{confirmBtn}{cancelBtn}</>;
  }
  return (
    <div style={{ display: 'flex', gap: 10, width: '100%' }}>
      {cancelBtn}
      {confirmBtn}
    </div>
  );
}
