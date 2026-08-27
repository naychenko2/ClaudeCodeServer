import { useEffect } from 'react';
import { createPortal } from 'react-dom';
import type { ReactNode, CSSProperties } from 'react';
import { X } from 'lucide-react';
import { C, R, FONT, SHADOW, SP, Z } from '../../lib/design';
import { getPopupDepth } from '../../lib/popupEscape';
import { IconButton } from './IconButton';
import { ICON_SIZE, ICON_STROKE } from './icons';
import { useIsMobileModal } from './useIsMobileModal';

interface ModalProps {
  width?: number;
  title?: ReactNode;
  subtitle?: ReactNode;      // опциональное описание/подзаголовок под заголовком
  footer?: ReactNode;        // зона действий (кнопки) — единообразно во всех диалогах
  onClose: () => void;
  closeOnBackdrop?: boolean;
  // Спрятать встроенный крестик — потребитель рисует свой (например, в ConfirmDialog,
  // где вся навигация живёт в футере и лишняя иконка в шапке только мешает).
  hideCloseButton?: boolean;
  children?: ReactNode;
  cardStyle?: CSSProperties;
}

// Единое модальное окно.
//  • Десктоп/планшет (>=768): центрированная карточка с мягким появлением.
//  • Мобила (<768): bottom-sheet — выезжает снизу, drag-handle сверху,
//    контент скроллится, действия (footer) прижаты к низу, учтён safe-area.
// Закрытие по Escape, по клику на оверлей и по крестику в углу карточки.
// API обратно совместимо: потребители, рисующие свою шапку без крестика,
// могут спрятать встроенный через hideCloseButton.
export function Modal({
  width = 440, title, subtitle, footer, onClose,
  closeOnBackdrop = true, hideCloseButton = false, children, cardStyle,
}: ModalProps) {
  const isMobile = useIsMobileModal();

  useEffect(() => {
    // preventDefault — сигнал нижележащим слоям (оверлей «Стены» и любой будущий),
    // что Escape уже обработан: без него одно нажатие закрывало и модалку, и слой под ней.
    // Дополнительно — пока над модалкой открыт попап (RoutePicker и т. п.), Escape
    // адресуется ему: иначе первый же Escape в открытой панели закрывал бы модалку
    // целиком и стирал её черновик.
    const handler = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented) return;
      if (getPopupDepth() > 0) return;
      e.preventDefault();
      onClose();
    };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [onClose]);

  // Крестик в углу карточки. Внутри карточки (currentTarget у оверлея — другой),
  // поэтому клик по нему НЕ сработает как closeOnBackdrop, и stopPropagation не нужен.
  // Размер sm: не вылезает за кромку и не перекрывает заголовок.
  const closeButton = hideCloseButton ? null : (
    <IconButton
      size="sm"
      tone="muted"
      variant="ghost"
      ariaLabel="Закрыть"
      onClick={onClose}
      style={{ flexShrink: 0, marginTop: -4, marginRight: -4 }}
    >
      <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
    </IconButton>
  );

  // Шапка: заголовок + опциональный подзаголовок. Жмётся во flex-колонку слева,
  // крестик — справа; если заголовка нет, крестик всё равно показывается
  // (success/error-фазы диалогов скрывают заголовок, но крестик нужен).
  const titleBlock = (title || subtitle) && (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 5, flexShrink: 1, flex: 1, minWidth: 0 }}>
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

  // Ряд шапки. На десктопе — заголовок слева, крестик справа. На мобиле крестик
  // уже стоит в handle-row шторки, поэтому в шапке остаётся только titleBlock
  // (без крестика и без лишнего ряда из одного крестика, когда title/subtitle пусты).
  const headerRow = isMobile
    ? titleBlock
    : (titleBlock || closeButton) && (
        <div style={{ display: 'flex', alignItems: 'flex-start', gap: SP.sm, flexShrink: 0 }}>
          {titleBlock}
          {closeButton}
        </div>
      );

  const overlayBase: CSSProperties = {
    position: 'fixed', inset: 0, background: C.overlay,
    display: 'flex', justifyContent: 'center', zIndex: Z.modal,
  };

  if (isMobile) {
    return createPortal(
      <div
        className="cc-overlay"
        style={{ ...overlayBase, alignItems: 'flex-end' }}
        // closeOnBackdrop=false во время loading — клик по оверлею закрывать нельзя
        // (диалог запирает UI, иначе человек останется без ответа). Но осознанно
        // оставляем Escape и крестик рабочими даже в loading: это явное намерение
        // закрыть, и лучше снять диалог и показать ответ устаревшим, чем запереть
        // окно навсегда (так уже ловил QA: «не закрывается»).
        onPointerDown={(e) => { if (closeOnBackdrop && e.target === e.currentTarget) onClose(); }}
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
          {/* Drag-handle слева + крестик справа — оба вне скролл-зоны.
              Заголовок и контент живут в скролл-блоке ниже. */}
          <div style={{
            display: 'flex', alignItems: 'center', justifyContent: 'space-between',
            padding: '10px 12px 4px 16px', flexShrink: 0,
          }}>
            <div style={{ width: 38, height: 4, borderRadius: 2, background: C.track }} />
            {closeButton}
          </div>
          {/* Скроллируемый контент */}
          <div style={{
            padding: '8px 18px 16px', display: 'flex', flexDirection: 'column', gap: 16,
            overflowY: 'auto', flex: 1, WebkitOverflowScrolling: 'touch',
          }}>
            {headerRow}
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
      </div>,
      document.body,
    );
  }

  // Планшет/десктоп — центрированная карточка
  return createPortal(
    <div
      className="cc-overlay"
      style={{ ...overlayBase, alignItems: 'center', padding: 16 }}
      onPointerDown={(e) => { if (closeOnBackdrop && e.target === e.currentTarget) onClose(); }}
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
          // flex:1 нужен модалкам с заданной высотой карточки (cardStyle.height):
          // без него контент занимает своё, и футер повисает посередине вместо низа.
          // Карточке по контенту это ничего не меняет — расти всё равно не от чего.
          overflowY: 'auto', flex: 1, minHeight: 0,
        }}>
          {headerRow}
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
    </div>,
    document.body,
  );
}
