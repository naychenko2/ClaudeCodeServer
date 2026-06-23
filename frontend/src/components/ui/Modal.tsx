import { useEffect, useState } from 'react';
import type { ReactNode, CSSProperties } from 'react';
import { C, R, FONT, SHADOW, Z } from '../../lib/design';

interface ModalProps {
  width?: number;
  title?: ReactNode;
  subtitle?: ReactNode;      // опциональное описание/подзаголовок под заголовком
  footer?: ReactNode;        // зона действий (кнопки) — единообразно во всех диалогах
  onClose: () => void;
  closeOnBackdrop?: boolean;
  children?: ReactNode;
  cardStyle?: CSSProperties;
}

const MOBILE_BP = 768;

// Брейкпоинт мобилы определяется внутри Modal — потребители получают
// bottom-sheet автоматически, без прокидывания пропсов.
function useIsMobile() {
  const [mobile, setMobile] = useState(() =>
    typeof window !== 'undefined'
      ? window.matchMedia(`(max-width: ${MOBILE_BP - 1}px)`).matches
      : false
  );
  useEffect(() => {
    const mq = window.matchMedia(`(max-width: ${MOBILE_BP - 1}px)`);
    const handler = (e: MediaQueryListEvent) => setMobile(e.matches);
    setMobile(mq.matches);
    // addEventListener поддерживается современными браузерами; для совместимости — фолбэк
    if (mq.addEventListener) mq.addEventListener('change', handler);
    else mq.addListener(handler);
    return () => {
      if (mq.removeEventListener) mq.removeEventListener('change', handler);
      else mq.removeListener(handler);
    };
  }, []);
  return mobile;
}

// Единое модальное окно.
//  • Десктоп/планшет (>=768): центрированная карточка с мягким появлением.
//  • Мобила (<768): bottom-sheet — выезжает снизу, drag-handle сверху,
//    контент скроллится, действия (footer) прижаты к низу, учтён safe-area.
// Закрытие по Escape и по клику на оверлей. API обратно совместимо.
export function Modal({
  width = 440, title, subtitle, footer, onClose,
  closeOnBackdrop = true, children, cardStyle,
}: ModalProps) {
  const isMobile = useIsMobile();

  useEffect(() => {
    const handler = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [onClose]);

  // Шапка: заголовок + опциональный подзаголовок
  const header = (title || subtitle) && (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 5, flexShrink: 0 }}>
      {title && (
        <h2 style={{
          fontFamily: FONT.serif, fontWeight: 500, fontSize: isMobile ? 21 : 22, margin: 0,
          color: C.textHeading, letterSpacing: '-0.01em', lineHeight: 1.25,
        }}>
          {title}
        </h2>
      )}
      {subtitle && (
        <div style={{ fontSize: 13.5, lineHeight: 1.5, color: C.textSecondary }}>
          {subtitle}
        </div>
      )}
    </div>
  );

  const overlayBase: CSSProperties = {
    position: 'fixed', inset: 0, background: C.overlay,
    display: 'flex', justifyContent: 'center', zIndex: Z.modal,
  };

  if (isMobile) {
    return (
      <div
        className="cc-overlay"
        style={{ ...overlayBase, alignItems: 'flex-end' }}
        onClick={(e) => { if (closeOnBackdrop && e.target === e.currentTarget) onClose(); }}
      >
        <div
          className="cc-sheet-card"
          style={{
            background: C.bgMain,
            borderTopLeftRadius: R.sheet, borderTopRightRadius: R.sheet,
            width: '100%', maxHeight: '92vh',
            display: 'flex', flexDirection: 'column',
            boxShadow: SHADOW.sheet, boxSizing: 'border-box',
            ...cardStyle,
          }}
        >
          {/* Drag-handle */}
          <div style={{ display: 'flex', justifyContent: 'center', padding: '10px 0 4px', flexShrink: 0 }}>
            <div style={{ width: 38, height: 4, borderRadius: 2, background: C.track }} />
          </div>
          {/* Скроллируемый контент */}
          <div style={{
            padding: '8px 18px 16px', display: 'flex', flexDirection: 'column', gap: 16,
            overflowY: 'auto', flex: 1, WebkitOverflowScrolling: 'touch',
          }}>
            {header}
            {children}
          </div>
          {/* Действия — прижаты к низу, учитываем safe-area iOS */}
          {footer && (
            <div style={{
              flexShrink: 0, padding: '12px 18px',
              paddingBottom: 'calc(12px + env(safe-area-inset-bottom))',
              borderTop: `1px solid ${C.borderLight}`, background: C.bgMain,
              display: 'flex', flexDirection: 'column', gap: 10,
            }}>
              {footer}
            </div>
          )}
        </div>
      </div>
    );
  }

  // Планшет/десктоп — центрированная карточка
  return (
    <div
      className="cc-overlay"
      style={{ ...overlayBase, alignItems: 'center', padding: 16 }}
      onClick={(e) => { if (closeOnBackdrop && e.target === e.currentTarget) onClose(); }}
    >
      <div
        className="cc-modal-card"
        style={{
          background: C.bgMain, borderRadius: R.modal,
          width, maxWidth: '100%', maxHeight: 'calc(100vh - 32px)',
          boxShadow: SHADOW.modal, display: 'flex', flexDirection: 'column',
          boxSizing: 'border-box', overflow: 'hidden',
          ...cardStyle,
        }}
      >
        <div style={{
          padding: footer ? '26px 28px 20px' : 28,
          display: 'flex', flexDirection: 'column', gap: 18,
          overflowY: 'auto',
        }}>
          {header}
          {children}
        </div>
        {footer && (
          <div style={{
            flexShrink: 0, padding: '16px 28px',
            borderTop: `1px solid ${C.borderLight}`,
            display: 'flex', gap: 10, justifyContent: 'flex-end',
          }}>
            {footer}
          </div>
        )}
      </div>
    </div>
  );
}
