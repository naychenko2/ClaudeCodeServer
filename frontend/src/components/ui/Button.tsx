import type { CSSProperties, ReactNode, MouseEvent } from 'react';
import { C, R, SHADOW, FONT } from '../../lib/design';

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'ghostAccent' | 'danger' | 'dashed';
export type ButtonSize = 'sm' | 'md' | 'lg';

const SIZE: Record<ButtonSize, CSSProperties> = {
  sm: { padding: '7px 13px', fontSize: 13, borderRadius: R.lg, minHeight: 32 },
  md: { padding: '11px 16px', fontSize: 14, borderRadius: R.xl, minHeight: 40 },
  lg: { padding: '0 16px', fontSize: 16, borderRadius: R.xxl, minHeight: 52 },
};

function variantStyle(variant: ButtonVariant, loading: boolean): CSSProperties {
  switch (variant) {
    case 'primary':
      return { background: loading ? C.accentSoft : C.accent, color: C.onAccent, border: 'none' };
    case 'secondary':
      return { background: C.bgPanel, color: C.textSecondary, border: 'none' };
    case 'ghost':
      return { background: 'transparent', color: C.textSecondary, border: `1px solid ${C.border}` };
    case 'ghostAccent':
      return { background: 'transparent', color: C.accent, border: `1.5px solid ${C.accent}` };
    case 'danger':
      return { background: C.danger, color: C.onAccent, border: 'none' };
    case 'dashed':
      return { background: 'transparent', color: C.accent, border: `1.5px dashed ${C.dashed}` };
  }
}

function Spinner({ size = 16 }: { size?: number }) {
  return (
    <span style={{
      width: size, height: size, flexShrink: 0, display: 'inline-block',
      border: '2.5px solid currentColor', borderTopColor: 'transparent',
      borderRadius: '50%', opacity: 0.9, animation: 'cc-spin 0.8s linear infinite',
    }} />
  );
}

interface ButtonProps {
  variant?: ButtonVariant;
  size?: ButtonSize;
  fullWidth?: boolean;
  loading?: boolean;
  disabled?: boolean;
  glow?: boolean;            // свечение под основной кнопкой (логин)
  leftIcon?: ReactNode;
  onClick?: (e: MouseEvent) => void;
  type?: 'button' | 'submit';
  title?: string;
  style?: CSSProperties;
  children: ReactNode;
}

export function Button({
  variant = 'primary', size = 'md', fullWidth, loading = false,
  disabled, glow, leftIcon, onClick, type = 'button', title, style, children,
}: ButtonProps) {
  const isDisabled = disabled || loading;
  return (
    <button
      type={type}
      title={title}
      disabled={isDisabled}
      onClick={onClick}
      style={{
        ...SIZE[size],
        ...variantStyle(variant, loading),
        width: fullWidth ? '100%' : undefined,
        fontWeight: 600,
        fontFamily: FONT.sans,
        cursor: isDisabled ? 'not-allowed' : 'pointer',
        opacity: disabled && !loading ? 0.7 : 1,
        display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8,
        boxShadow: glow && variant === 'primary' ? SHADOW.button : 'none',
        transition: 'background 0.15s, color 0.15s, opacity 0.15s',
        ...style,
      }}
    >
      {loading ? <Spinner /> : leftIcon}
      {children}
    </button>
  );
}
