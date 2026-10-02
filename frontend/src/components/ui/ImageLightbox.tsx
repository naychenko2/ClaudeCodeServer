import { useEffect } from 'react';
import { createPortal } from 'react-dom';
import type { ReactNode } from 'react';
import { X } from 'lucide-react';
import { C, SP, Z } from '../../lib/design';
import { getPopupDepth } from '../../lib/popupEscape';
import { IconButton } from './IconButton';
import { ICON_SIZE, ICON_STROKE } from './icons';

// Полноэкранный просмотр картинки: чёрная подложка, кадр по центру, крестик в углу.
// Закрытие — по Escape, по клику мимо кадра и по крестику. children — ряд действий
// под кадром (скачать, сохранить в проект); клики по нему окно не закрывают.
export function ImageLightbox({ src, alt = '', onClose, children }: {
  src: string;
  alt?: string;
  onClose: () => void;
  children?: ReactNode;
}) {
  useEffect(() => {
    // Тот же протокол Escape, что у Modal: обработанный — помечаем, открытый попап — его очередь
    const handler = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented) return;
      if (getPopupDepth() > 0) return;
      e.preventDefault();
      onClose();
    };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [onClose]);

  return createPortal(
    <div
      className="cc-overlay"
      onClick={onClose}
      style={{
        position: 'fixed', inset: 0, zIndex: Z.modal, background: C.mediaBackdrop,
        display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center',
        padding: SP.lg,
      }}
    >
      <div style={{ position: 'absolute', top: `calc(${SP.lg}px + env(safe-area-inset-top))`, right: SP.lg }}
        onClick={e => e.stopPropagation()}>
        <IconButton size="lg" variant="soft" ariaLabel="Закрыть" onClick={onClose}>
          <X size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
        </IconButton>
      </div>
      <img
        src={src}
        alt={alt}
        onClick={e => e.stopPropagation()}
        style={{ maxWidth: '92vw', maxHeight: children ? '76vh' : '88vh', objectFit: 'contain', borderRadius: 8, display: 'block' }}
      />
      {children && (
        <div onClick={e => e.stopPropagation()} style={{ marginTop: SP.lg }}>
          {children}
        </div>
      )}
    </div>,
    document.body,
  );
}
