import type { CSSProperties, ReactNode, MouseEvent } from 'react';
import { C, TB } from '../lib/design';
import { IconButton } from './ui/IconButton';

// PillSwitch переехал в ui/PillSwitch.tsx (вошёл в design-kit для внешних модулей);
// реэкспорт сохраняет прежний путь импорта для существующих потребителей.
export { PillSwitch } from './ui/PillSwitch';

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
// Кнопки рельсы — круглые (borderRadius 32), дефолт задаётся здесь, а не в
// каждом вызове через style. Если нужен override — передать style (мержится).
export function ToolbarIconButton({ onClick, title, isMobile, color, disabled, active, style, children }: {
  onClick?: (e: MouseEvent) => void;
  title?: string;
  isMobile?: boolean;
  color?: string;
  disabled?: boolean;
  active?: boolean;
  style?: CSSProperties;
  children: ReactNode;
}) {
  return (
    <IconButton
      onClick={onClick} title={title} disabled={disabled} active={active} color={color}
      size={isMobile ? 'lg' : 'md'} style={style}
    >
      {children}
    </IconButton>
  );
}
