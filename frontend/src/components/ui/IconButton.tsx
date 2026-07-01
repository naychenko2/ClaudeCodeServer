import { useState } from 'react';
import type { CSSProperties, MouseEvent, ReactNode } from 'react';
import { C, R, TB, SHADOW } from '../../lib/design';

// Единая квадратная icon-кнопка (действие-иконка) для всех тулбаров, сайдбаров и шапок.
// Заменяет ~десяток инлайновых реализаций с размерами 22..44 и радиусами 6..12.

export type IconButtonSize = 'xs' | 'sm' | 'md' | 'lg';
export type IconButtonTone = 'muted' | 'accent' | 'danger';
export type IconButtonVariant = 'ghost' | 'soft';

// Единая шкала: 24(плотные строки списков/дерева) / 28 / 32 / 40(тач). Радиус — R.sm/R.md, для тач R.lg.
const SIZE: Record<IconButtonSize, { box: number; radius: number }> = {
  xs: { box: 24, radius: R.sm },
  sm: { box: 28, radius: R.md },
  md: { box: 32, radius: R.md },
  lg: { box: 40, radius: R.lg },
};

const TONE: Record<IconButtonTone, { idle: string; hoverBg: string; hoverColor: string }> = {
  muted:  { idle: TB.iconColor, hoverBg: TB.iconHoverBg, hoverColor: TB.iconColorHover },
  accent: { idle: C.accent,     hoverBg: C.accentLight,  hoverColor: C.accent },
  danger: { idle: C.textMuted,  hoverBg: C.dangerBg,     hoverColor: C.danger },
};

// Единый focus-visible ring (клавиатура) — инжектим один раз.
const FOCUS_CLASS = 'cc-iconbtn';
if (typeof document !== 'undefined' && !document.getElementById('cc-iconbtn-style')) {
  const el = document.createElement('style');
  el.id = 'cc-iconbtn-style';
  el.textContent = `.${FOCUS_CLASS}:focus-visible{outline:none;box-shadow:${SHADOW.focus};}`;
  document.head.appendChild(el);
}

interface Props {
  onClick?: (e: MouseEvent) => void;
  title?: string;
  disabled?: boolean;
  active?: boolean;
  size?: IconButtonSize;
  tone?: IconButtonTone;
  variant?: IconButtonVariant;   // ghost — прозрачный; soft — подложка C.bgPanel
  color?: string;                // переопределить цвет иконки в покое
  style?: CSSProperties;
  children: ReactNode;           // svg
}

export function IconButton({
  onClick, title, disabled, active, size = 'md', tone = 'muted', variant = 'ghost', color, style, children,
}: Props) {
  const [hover, setHover] = useState(false);
  const s = SIZE[size];
  const t = TONE[tone];
  const base = variant === 'soft' ? C.bgPanel : 'transparent';
  const bg = disabled ? base : (active ? C.accentMuted : (hover ? t.hoverBg : base));
  const fg = disabled ? C.border : (active ? C.accent : (hover ? t.hoverColor : (color ?? t.idle)));
  return (
    <button
      className={FOCUS_CLASS}
      onClick={onClick}
      title={title}
      disabled={disabled}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        width: s.box, height: s.box, flexShrink: 0, padding: 0,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        border: 'none', borderRadius: s.radius, cursor: disabled ? 'default' : 'pointer',
        background: bg, color: fg,
        transition: 'background 0.12s, color 0.12s, box-shadow 0.12s',
        ...style,
      }}
    >
      {children}
    </button>
  );
}
