import { useState } from 'react';
import type { CSSProperties, MouseEvent, ReactNode } from 'react';
import { C, R, FONT, SHADOW, Z } from '../../lib/design';

// Единое выпадающее меню: карточка + подложка для закрытия по клику вне.
// Два режима позиционирования:
//  - обычный: absolute внутри родителя position:relative; bottom (если задан) вместо
//    top — карточка растёт ВВЕРХ (для триггеров у нижнего края панели);
//  - anchor: fixed по DOMRect триггера — для меню внутри скролл-контейнеров
//    (overflow списка обрезал бы absolute). Направление — от maxHeight: вверх,
//    если под якорем не хватает места; по горизонтали клампится в окно.
// Закрытие по Esc/скроллу в anchor-режиме — на вызывающей стороне (поведение, не контрол).
export function Menu({ onClose, align = 'right', top = 30, bottom, minWidth = 200, anchor, maxHeight = 300, children }: {
  onClose: () => void;
  align?: 'left' | 'right';
  top?: number;
  bottom?: number;
  minWidth?: number;
  // rect кнопки-триггера; задан — режим fixed (align/top/bottom игнорируются)
  anchor?: DOMRect;
  // высота меню для выбора направления в anchor-режиме (вверх, если снизу не влезает)
  maxHeight?: number;
  children: ReactNode;
}) {
  let pos: CSSProperties;
  if (anchor) {
    const openUp = anchor.bottom + 6 + maxHeight > window.innerHeight && anchor.top > maxHeight;
    const left = Math.max(8, Math.min(anchor.right - minWidth, window.innerWidth - minWidth - 8));
    pos = {
      position: 'fixed', left,
      ...(openUp ? { bottom: window.innerHeight - anchor.top + 6 } : { top: anchor.bottom + 6 }),
    };
  } else {
    pos = { position: 'absolute', ...(bottom != null ? { bottom } : { top }), [align]: 0 };
  }
  return (
    <>
      <div style={{ position: 'fixed', inset: 0, zIndex: Z.dropdown }} onClick={onClose} />
      <div style={{
        ...pos, zIndex: Z.dropdown + 1,
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
