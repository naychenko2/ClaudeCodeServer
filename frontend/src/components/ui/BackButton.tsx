import type { ReactNode, CSSProperties, MouseEvent, KeyboardEvent } from 'react';
import { ChevronLeft } from 'lucide-react';
import { C, R } from '../../lib/design';

// Единая кнопка «назад» для тулбаров: chevron-влево + кликабельный текст.
// Клик по всей кнопке (стрелка + текст) выполняет возврат — одинаково во всех шапках.
// Рендерится div'ом с role="button", а не <button>: в children бывают собственные
// кнопки (стек участников группового чата в мобильной шапке), а вложенный
// <button> в <button> — невалидный HTML (React hydration error). Клавиатурная
// доступность сохранена вручную (tabIndex + Enter/Space).
export function BackButton({ onClick, title, children, iconColor = C.textSecondary, iconSize = 16, style }: {
  onClick: (e: MouseEvent) => void;
  title?: string;
  children?: ReactNode;
  iconColor?: string;
  iconSize?: number;
  style?: CSSProperties;
}) {
  const handleKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      onClick(e as unknown as MouseEvent);
    }
  };
  return (
    <div
      role="button"
      tabIndex={0}
      onClick={onClick}
      onKeyDown={handleKeyDown}
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
    </div>
  );
}
