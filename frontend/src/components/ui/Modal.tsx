import { useEffect } from 'react';
import type { ReactNode, CSSProperties } from 'react';
import { C, R, FONT, SHADOW } from '../../lib/design';

interface ModalProps {
  width?: number;
  title?: ReactNode;
  onClose: () => void;
  closeOnBackdrop?: boolean;
  children: ReactNode;
  cardStyle?: CSSProperties;
}

// Единое модальное окно: затемнённый оверлей + карточка по центру.
// Закрытие по Escape и по клику на оверлей.
export function Modal({ width = 420, title, onClose, closeOnBackdrop = true, children, cardStyle }: ModalProps) {
  useEffect(() => {
    const handler = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [onClose]);

  return (
    <div
      style={{
        position: 'fixed', inset: 0, background: C.overlay,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        padding: 16, zIndex: 1000,
      }}
      onClick={(e) => { if (closeOnBackdrop && e.target === e.currentTarget) onClose(); }}
    >
      <div
        style={{
          background: C.bgMain, borderRadius: R.modal, padding: 28,
          width, maxWidth: '100%', maxHeight: 'calc(100vh - 32px)', overflowY: 'auto',
          boxShadow: SHADOW.modal,
          display: 'flex', flexDirection: 'column', gap: 18,
          boxSizing: 'border-box',
          ...cardStyle,
        }}
      >
        {title && (
          <h2 style={{
            fontFamily: FONT.serif, fontWeight: 500, fontSize: 22, margin: 0,
            color: C.textHeading, letterSpacing: '-0.01em',
          }}>
            {title}
          </h2>
        )}
        {children}
      </div>
    </div>
  );
}
