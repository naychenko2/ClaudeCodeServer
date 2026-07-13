import { useState } from 'react';
import type { CSSProperties, MouseEvent, ReactNode } from 'react';
import { C, R, FONT, SHADOW, Z } from '../../lib/design';

// Единое выпадающее меню: карточка + подложка для закрытия по клику вне.
// Позиционирование задаёт родитель (обёртка position:relative); Menu рисует карточку абсолютно.
export function Menu({ onClose, align = 'right', top = 30, minWidth = 200, children }: {
  onClose: () => void;
  align?: 'left' | 'right';
  top?: number;
  minWidth?: number;
  children: ReactNode;
}) {
  return (
    <>
      <div style={{ position: 'fixed', inset: 0, zIndex: Z.dropdown }} onClick={onClose} />
      <div style={{
        position: 'absolute', top, [align]: 0, zIndex: Z.dropdown + 1,
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
        boxShadow: SHADOW.dropdown, padding: 5, minWidth, display: 'flex', flexDirection: 'column',
      }}>
        {children}
      </div>
    </>
  );
}

// Единый пункт выпадающего меню.
export function MenuItem({ icon, label, onClick, danger, disabled }: {
  icon?: ReactNode;
  label: ReactNode;
  onClick?: (e: MouseEvent) => void;
  danger?: boolean;
  disabled?: boolean;
}) {
  const [hover, setHover] = useState(false);
  const color = disabled ? C.textMuted : (danger ? C.danger : C.textPrimary);
  const style: CSSProperties = {
    display: 'flex', alignItems: 'center', gap: 10, width: '100%', textAlign: 'left',
    background: hover && !disabled ? C.bgSelected : 'none', border: 'none', borderRadius: R.md,
    padding: '9px 10px', cursor: disabled ? 'default' : 'pointer', color, fontSize: 13.5, fontFamily: FONT.sans,
  };
  return (
    <button
      onClick={onClick}
      disabled={disabled}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={style}
    >
      {icon && (
        <span style={{ display: 'inline-flex', alignItems: 'center', width: 15, height: 15, flexShrink: 0, color: 'inherit' }}>
          {icon}
        </span>
      )}
      {label}
    </button>
  );
}
