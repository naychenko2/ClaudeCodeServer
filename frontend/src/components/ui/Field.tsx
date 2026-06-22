import { useState, useRef, useEffect } from 'react';
import type { ReactNode, CSSProperties, KeyboardEvent } from 'react';
import { C, R, FONT, FIELD, SHADOW } from '../../lib/design';

// === Подпись поля (uppercase-лейбл формы) ===
export function FieldLabel({ children }: { children: ReactNode }) {
  return (
    <label style={{
      fontSize: 12, fontWeight: 600, color: C.textSecondary,
      textTransform: 'uppercase', letterSpacing: '0.05em',
    }}>
      {children}
    </label>
  );
}

// === Обёртка «лейбл + контрол + подсказка» ===
export function Field({ label, hint, children }: { label?: ReactNode; hint?: ReactNode; children: ReactNode }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      {label && <FieldLabel>{label}</FieldLabel>}
      {children}
      {hint && <span style={{ fontSize: 11.5, color: C.textMuted }}>{hint}</span>}
    </div>
  );
}

// Базовый стиль контрола ввода с учётом фокуса
function controlStyle(focused: boolean, mono?: boolean, extra?: CSSProperties): CSSProperties {
  return {
    background: FIELD.background,
    border: `1px solid ${focused ? FIELD.borderFocus : C.border}`,
    borderRadius: FIELD.borderRadius,
    padding: '10px 13px',
    fontSize: FIELD.fontSize,
    color: FIELD.color,
    outline: 'none',
    fontFamily: mono ? FONT.mono : 'inherit',
    width: '100%',
    boxSizing: 'border-box',
    boxShadow: focused ? SHADOW.focus : 'none',
    transition: 'border-color 0.15s, box-shadow 0.15s',
    ...extra,
  };
}

interface TextFieldProps {
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  type?: string;
  mono?: boolean;
  autoFocus?: boolean;
  disabled?: boolean;
  letterSpacing?: string;
  onEnter?: () => void;
  style?: CSSProperties;
}

// === Однострочное поле ввода с focus-ring ===
export function TextField({ value, onChange, placeholder, type = 'text', mono, autoFocus, disabled, letterSpacing, onEnter, style }: TextFieldProps) {
  const [focused, setFocused] = useState(false);
  return (
    <input
      type={type}
      value={value}
      onChange={(e) => onChange(e.target.value)}
      placeholder={placeholder}
      autoFocus={autoFocus}
      disabled={disabled}
      onFocus={() => setFocused(true)}
      onBlur={() => setFocused(false)}
      onKeyDown={onEnter ? (e: KeyboardEvent) => { if (e.key === 'Enter') onEnter(); } : undefined}
      style={controlStyle(focused, mono, { letterSpacing, ...style })}
    />
  );
}

interface TextAreaProps {
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  autoGrow?: boolean;
  minHeight?: number;
  disabled?: boolean;
  style?: CSSProperties;
}

// === Многострочное поле с авто-ростом высоты ===
export function TextArea({ value, onChange, placeholder, autoGrow, minHeight = 80, disabled, style }: TextAreaProps) {
  const [focused, setFocused] = useState(false);
  const ref = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    if (!autoGrow) return;
    const el = ref.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${el.scrollHeight}px`;
  }, [value, autoGrow]);

  return (
    <textarea
      ref={ref}
      value={value}
      onChange={(e) => onChange(e.target.value)}
      placeholder={placeholder}
      disabled={disabled}
      onFocus={() => setFocused(true)}
      onBlur={() => setFocused(false)}
      style={controlStyle(focused, false, {
        minHeight, resize: 'none', overflow: autoGrow ? 'hidden' : 'auto', lineHeight: 1.5, ...style,
      })}
    />
  );
}

interface IconFieldProps {
  icon?: ReactNode;
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  type?: string;
  mono?: boolean;
  disabled?: boolean;
  letterSpacing?: string;
  height?: number;
  radius?: number;
  fontSize?: number;
  style?: CSSProperties;
}

// === Поле с иконкой-префиксом (логин, поиск): бордер на обёртке, инпут без рамки ===
export function IconField({
  icon, value, onChange, placeholder, type = 'text', mono, disabled,
  letterSpacing, height = 50, radius = R.xxl, fontSize = 15, style,
}: IconFieldProps) {
  const [focused, setFocused] = useState(false);
  return (
    <div style={{
      display: 'flex', alignItems: 'center', background: C.bgWhite,
      border: `1px solid ${focused ? C.accent : C.border}`,
      borderRadius: radius, padding: '0 14px', height,
      boxShadow: focused ? SHADOW.focus : 'none',
      transition: 'border-color 0.15s, box-shadow 0.15s',
      boxSizing: 'border-box', ...style,
    }}>
      {icon && (
        <span style={{ color: focused ? C.accent : C.textMuted, marginRight: 9, display: 'flex', flexShrink: 0, transition: 'color 0.15s' }}>
          {icon}
        </span>
      )}
      <input
        type={type}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        disabled={disabled}
        onFocus={() => setFocused(true)}
        onBlur={() => setFocused(false)}
        style={{
          border: 'none', background: 'none', flex: 1, fontSize,
          color: C.textHeading, fontFamily: mono ? FONT.mono : 'inherit',
          letterSpacing, outline: 'none', opacity: disabled ? 0.6 : 1, minWidth: 0,
        }}
      />
    </div>
  );
}
