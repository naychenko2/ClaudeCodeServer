import type { ReactNode, CSSProperties, MouseEvent } from 'react';
import { ChevronLeft } from 'lucide-react';
import { C, R } from '../../lib/design';

// Единая кнопка «назад» для тулбаров: chevron-влево + кликабельный текст.
// Клик по всей кнопке (стрелка + текст) выполняет возврат — одинаково во всех шапках.
export function BackButton({ onClick, title, children, iconColor = C.textSecondary, iconSize = 16, style }: {
  onClick: (e: MouseEvent) => void;
  title?: string;
  children?: ReactNode;
  iconColor?: string;
  iconSize?: number;
  style?: CSSProperties;
}) {
  return (
    <button
      onClick={onClick}
      title={title ?? 'Назад'}
      style={{
        display: 'flex', alignItems: 'center', gap: 7,
        background: 'none', border: 'none', cursor: 'pointer',
        padding: 0, minWidth: 0, fontFamily: 'inherit', textAlign: 'left',
        borderRadius: R.md, flexShrink: 0, ...style,
      }}
    >
      <ChevronLeft size={iconSize} strokeWidth={2} color={iconColor} style={{ flexShrink: 0 }} />
      {children}
    </button>
  );
}
