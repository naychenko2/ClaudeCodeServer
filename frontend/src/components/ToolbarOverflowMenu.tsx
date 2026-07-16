import type { ReactNode, CSSProperties } from 'react';
import { useEffect, useRef, useState, useId } from 'react';
import { createPortal } from 'react-dom';
import { MoreHorizontal } from 'lucide-react';
import { C, R, TB, FONT, SHADOW, Z } from '../lib/design';
import { ToolbarIconButton } from './Toolbar';

// === Единый overflow-примитив тулбаров ===
// «Что не влезло → в меню». На десктопе — дропдаун под триггером, на мобиле —
// боттом-шит (тот же визуальный язык, что у палитры AiLauncher). Заменяет собой
// разрозненные бэспоук-реализации (MobileCombinedBadge / FilterBar / mode-дропдаун).
//
// Два способа наполнения (взаимоисключимы):
//   • items — простой список строк (иконка + подпись [+ описание], пункт или переключатель);
//   • children — произвольное содержимое (секции фильтров и т.п.).
//
// Разметка «primary/overflow» — явная (без авто-измерения ширины): вызывающий тулбар
// сам решает, что оставить в ряду, а что передать сюда.

export type OverflowItem = {
  key: string;
  icon?: ReactNode;
  label: string;
  sublabel?: string;
  onClick?: () => void;
  // undefined — обычный пункт (клик закрывает меню); boolean — строка-переключатель
  // (клик переключает, меню остаётся открытым, справа рисуется свитч по значению).
  toggle?: boolean;
  dot?: boolean;        // точка-индикатор справа («живой» пункт)
  danger?: boolean;
  disabled?: boolean;
};

type TriggerRenderer = (p: { open: boolean; toggle: () => void; ref: (el: HTMLElement | null) => void }) => ReactNode;

export function ToolbarOverflowMenu({
  isMobile,
  items,
  children,
  title,
  triggerIcon,
  triggerLabel,
  triggerTitle = 'Ещё',
  indicator,
  align = 'right',
  renderTrigger,
}: {
  isMobile?: boolean;
  items?: OverflowItem[];
  children?: ReactNode;
  title?: string;
  triggerIcon?: ReactNode;
  triggerLabel?: string;
  triggerTitle?: string;
  // number>0 → счётчик-бейдж; true → точка; ReactNode → как есть; иначе ничего
  indicator?: number | boolean | ReactNode;
  align?: 'left' | 'right';
  renderTrigger?: TriggerRenderer;
}) {
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const triggerElRef = useRef<HTMLElement | null>(null);
  const labelId = useId();

  // Закрытие: клик вне (десктоп-дропдаун) + Esc (везде)
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.stopPropagation(); setOpen(false); } };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => { document.removeEventListener('mousedown', onDown); document.removeEventListener('keydown', onKey); };
  }, [open]);

  const close = () => setOpen(false);
  const toggle = () => setOpen(o => !o);

  const content = children ?? (items ? (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
      {items.map(it => (
        <ItemRow key={it.key} item={it} isMobile={isMobile} onDone={close} />
      ))}
    </div>
  ) : null);

  // --- Триггер ---
  const setTriggerRef = (el: HTMLElement | null) => { triggerElRef.current = el; };
  let trigger: ReactNode;
  if (renderTrigger) {
    trigger = renderTrigger({ open, toggle, ref: setTriggerRef });
  } else if (triggerLabel) {
    // Кнопка с подписью (например «Фильтры») — chip-стиль тулбара
    trigger = (
      <button
        type="button" onClick={toggle} title={triggerTitle}
        aria-haspopup="menu" aria-expanded={open}
        style={{
          display: 'inline-flex', alignItems: 'center', gap: 7, flexShrink: 0,
          height: isMobile ? TB.iconHitMobile : 34, padding: '0 12px',
          borderRadius: R.lg, border: `1px solid ${open ? C.accent : C.border}`,
          background: open ? C.accentLight : C.bgWhite, color: open ? C.accent : C.textSecondary,
          fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, cursor: 'pointer', position: 'relative',
        }}
      >
        {triggerIcon}
        <span>{triggerLabel}</span>
        {typeof indicator === 'number' && indicator > 0 && (
          <span style={countBadgeInline}>{indicator}</span>
        )}
      </button>
    );
  } else {
    // Дефолтный icon-триггер «⋯» с опциональным индикатором
    trigger = (
      <span style={{ position: 'relative', display: 'inline-flex', flexShrink: 0 }}>
        <ToolbarIconButton onClick={toggle} title={triggerTitle} isMobile={isMobile} active={open}>
          {triggerIcon ?? <MoreHorizontal size={18} />}
        </ToolbarIconButton>
        {typeof indicator === 'number' && indicator > 0 && <span style={countBadge}>{indicator}</span>}
        {indicator === true && <span style={dotBadge} />}
      </span>
    );
  }

  return (
    <div ref={rootRef} style={{ position: 'relative', flexShrink: 0, display: 'inline-flex' }}>
      {trigger}

      {open && !isMobile && (
        <div role="menu" aria-labelledby={title ? labelId : undefined} style={dropdownStyle(align)}>
          {title && <div id={labelId} style={sectionTitle}>{title}</div>}
          {content}
        </div>
      )}

      {open && isMobile && createPortal(
        <div style={sheetOverlay} onMouseDown={close}>
          <div
            className="cc-sheet-card"
            style={sheetCard} onMouseDown={e => e.stopPropagation()}
            role="dialog" aria-modal="true" aria-labelledby={title ? labelId : undefined}
          >
            <div style={sheetHandle} />
            {title && <div id={labelId} style={{ ...sectionTitle, padding: '0 12px 8px' }}>{title}</div>}
            <div style={{ paddingBottom: 'env(safe-area-inset-bottom, 0px)' }}>{content}</div>
          </div>
        </div>,
        document.body,
      )}
    </div>
  );
}

// === Строка меню/шита ===
function ItemRow({ item, isMobile, onDone }: { item: OverflowItem; isMobile?: boolean; onDone: () => void }) {
  const isToggle = item.toggle !== undefined;
  const handle = () => {
    if (item.disabled) return;
    item.onClick?.();
    if (!isToggle) onDone();   // переключатель оставляет меню открытым
  };
  return (
    <button
      type="button" onClick={handle} disabled={item.disabled}
      role={isToggle ? 'menuitemcheckbox' : 'menuitem'} aria-checked={isToggle ? item.toggle : undefined}
      style={{
        display: 'flex', alignItems: 'center', gap: 12, width: '100%', textAlign: 'left',
        border: 'none', background: 'transparent', cursor: item.disabled ? 'default' : 'pointer',
        borderRadius: R.lg, padding: isMobile ? '11px 12px' : '9px 10px',
        minHeight: isMobile ? 44 : undefined, fontFamily: FONT.sans,
        color: item.danger ? C.dangerText : C.textHeading, opacity: item.disabled ? 0.5 : 1,
      }}
      onMouseEnter={e => { if (!item.disabled) e.currentTarget.style.background = C.bgInset; }}
      onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
    >
      {item.icon != null && (
        <span style={{ width: 24, display: 'grid', placeItems: 'center', flex: 'none', color: item.danger ? C.dangerText : C.textSecondary }}>
          {item.icon}
        </span>
      )}
      <span style={{ flex: 1, minWidth: 0 }}>
        <span style={{ display: 'block', fontSize: 14, fontWeight: 600, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{item.label}</span>
        {item.sublabel && <span style={{ display: 'block', fontSize: 11.5, color: C.textMuted, fontWeight: 400 }}>{item.sublabel}</span>}
      </span>
      {isToggle && (
        <span style={{ ...switchTrack, background: item.toggle ? C.accent : C.track }}>
          <span style={{ ...switchThumb, transform: item.toggle ? 'translateX(16px)' : 'translateX(0)' }} />
        </span>
      )}
      {!isToggle && item.dot && <span style={dotBadgeStatic} />}
    </button>
  );
}

// === Стили ===
function dropdownStyle(align: 'left' | 'right'): CSSProperties {
  return {
    position: 'absolute', top: 'calc(100% + 6px)',
    left: align === 'left' ? 0 : undefined, right: align === 'right' ? 0 : undefined,
    minWidth: 240, maxWidth: 320,
    background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.xl,
    boxShadow: SHADOW.dropdown, padding: 6, zIndex: Z.dropdown,
  };
}
const sheetOverlay: CSSProperties = {
  position: 'fixed', inset: 0, background: C.overlay, zIndex: Z.modal,
  display: 'flex', alignItems: 'flex-end', justifyContent: 'center',
};
const sheetCard: CSSProperties = {
  width: '100%', maxWidth: '100%', background: C.bgCard, border: `1px solid ${C.border}`,
  borderTopLeftRadius: R.sheet, borderTopRightRadius: R.sheet, boxShadow: SHADOW.sheet,
  padding: 8, maxHeight: '82vh', overflowY: 'auto',
};
const sheetHandle: CSSProperties = {
  width: 38, height: 4, borderRadius: 999, background: C.border, margin: '6px auto 10px',
};
const sectionTitle: CSSProperties = {
  fontFamily: FONT.mono, fontSize: 10.5, textTransform: 'uppercase', letterSpacing: 0.6,
  color: C.textMuted, padding: '6px 10px 4px',
};
const switchTrack: CSSProperties = {
  width: 34, height: 18, borderRadius: 999, position: 'relative', flex: 'none', transition: 'background .16s',
};
const switchThumb: CSSProperties = {
  position: 'absolute', top: 2, left: 2, width: 14, height: 14, borderRadius: '50%',
  background: C.bgWhite, boxShadow: SHADOW.thumb, transition: 'transform .16s',
};
const countBadge: CSSProperties = {
  position: 'absolute', top: -2, right: -2, minWidth: 15, height: 15, padding: '0 3px',
  borderRadius: 999, background: C.accent, color: C.onAccent, fontSize: 9, fontWeight: 700,
  fontFamily: FONT.mono, display: 'grid', placeItems: 'center', pointerEvents: 'none',
};
const countBadgeInline: CSSProperties = {
  minWidth: 16, height: 16, padding: '0 4px', borderRadius: 999, background: C.accent, color: C.onAccent,
  fontSize: 10, fontWeight: 700, fontFamily: FONT.mono, display: 'grid', placeItems: 'center',
};
const dotBadge: CSSProperties = {
  position: 'absolute', top: 0, right: 0, width: 8, height: 8, borderRadius: '50%',
  background: C.accent, border: `2px solid ${C.bgPanel}`, pointerEvents: 'none',
};
const dotBadgeStatic: CSSProperties = {
  width: 8, height: 8, borderRadius: '50%', background: C.accent, flex: 'none',
};
