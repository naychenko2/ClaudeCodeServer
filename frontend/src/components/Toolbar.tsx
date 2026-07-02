import type { CSSProperties, ReactNode, MouseEvent } from 'react';
import { C, TB } from '../lib/design';
import { IconButton } from './ui/IconButton';

// Компактные текстовые кнопки тулбара (выравниваются по 32px-линии icon-кнопок)
export const tbBtnPrimary: CSSProperties = {
  border: 'none', background: C.accent, color: C.onAccent,
  borderRadius: 8, padding: '0 14px', height: 32, fontSize: 13, fontWeight: 600,
  cursor: 'pointer', fontFamily: 'inherit', display: 'flex', alignItems: 'center', flexShrink: 0,
};
export const tbBtnGhost: CSSProperties = {
  background: 'none', border: `1px solid ${C.border}`, color: C.textSecondary,
  borderRadius: 8, padding: '0 12px', height: 32, fontSize: 13, fontWeight: 600,
  cursor: 'pointer', fontFamily: 'inherit', display: 'flex', alignItems: 'center', flexShrink: 0,
};

// === Контейнер тулбара: единая высота, фон, бордер ===
export function Toolbar({ isMobile, noBorder, bg, children, style }: {
  isMobile?: boolean;
  noBorder?: boolean;
  bg?: string;
  children: ReactNode;
  style?: CSSProperties;
}) {
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: TB.gap,
      height: isMobile ? TB.heightMobile : TB.heightDesktop,
      padding: `0 ${isMobile ? TB.padXMobile : TB.padX}px`,
      background: bg ?? TB.bg,
      borderBottom: noBorder ? 'none' : TB.borderBottom,
      boxSizing: 'border-box', flexShrink: 0,
      ...style,
    }}>
      {children}
    </div>
  );
}

// === Icon-кнопка тулбара — тонкая обёртка над общим ui/IconButton ===
// Сохранена для обратной совместимости API (isMobile → размер тач-таргета).
export function ToolbarIconButton({ onClick, title, isMobile, color, disabled, active, children }: {
  onClick?: (e: MouseEvent) => void;
  title?: string;
  isMobile?: boolean;
  color?: string;
  disabled?: boolean;
  active?: boolean;
  children: ReactNode;
}) {
  return (
    <IconButton
      onClick={onClick} title={title} disabled={disabled} active={active} color={color}
      size={isMobile ? 'lg' : 'md'}
    >
      {children}
    </IconButton>
  );
}

// === Pill / сегмент-переключатель: единый стиль дорожки и активного сегмента ===
export function PillSwitch<T extends string>({ value, options, onChange, fill, isMobile }: {
  value: T;
  options: { value: T; label: string }[];
  onChange: (v: T) => void;
  fill?: boolean;
  isMobile?: boolean;
}) {
  return (
    <div style={{
      display: 'flex', gap: 3, background: TB.pillTrack,
      borderRadius: TB.pillRadius + 1, padding: 3,
      flexShrink: 0, width: fill ? '100%' : undefined, boxSizing: 'border-box',
    }}>
      {options.map(opt => {
        const active = value === opt.value;
        return (
          <button key={opt.value} onClick={() => onChange(opt.value)}
            style={{
              flex: fill ? 1 : undefined, padding: isMobile ? '8px 12px' : '6px 12px',
              minHeight: isMobile ? 40 : 32,
              borderRadius: TB.pillRadius - 2, border: 'none', cursor: 'pointer',
              fontSize: 13, fontWeight: 600, fontFamily: 'inherit', whiteSpace: 'nowrap',
              transition: 'background 0.15s, color 0.15s',
              background: active ? TB.pillThumbBg : 'transparent',
              color: active ? C.textHeading : C.textSecondary,
              boxShadow: active ? TB.pillThumbShadow : 'none',
            }}
          >
            {opt.label}
          </button>
        );
      })}
    </div>
  );
}
