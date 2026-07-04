import { forwardRef, useState } from 'react';
import type { KeyboardEvent } from 'react';
import { C, SHADOW } from '../../lib/design';

interface ToggleProps {
  checked: boolean;
  onChange: (v: boolean) => void;
  disabled?: boolean;
  width?: number;
  height?: number;
  /** Делает тумблер фокусируемым с клавиатуры (Tab) и включает управление стрелками ←/→. */
  focusable?: boolean;
  /** Вызывается при нажатии Enter, когда тумблер в фокусе (например, отправка формы). */
  onEnter?: () => void;
}

// === Переключатель-тумблер (on/off) ===
export const Toggle = forwardRef<HTMLDivElement, ToggleProps>(function Toggle(
  { checked, onChange, disabled, width = 42, height = 25, focusable, onEnter },
  ref,
) {
  const pad = 3;
  const thumb = height - pad * 2;
  const [focused, setFocused] = useState(false);

  const handleKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (disabled) return;
    switch (e.key) {
      case 'ArrowRight':
      case 'ArrowUp':
        e.preventDefault();
        onChange(true);
        break;
      case 'ArrowLeft':
      case 'ArrowDown':
        e.preventDefault();
        onChange(false);
        break;
      case ' ': // Пробел — переключить (стандартное поведение switch)
        e.preventDefault();
        onChange(!checked);
        break;
      case 'Enter':
        e.preventDefault();
        if (onEnter) onEnter();
        else onChange(!checked);
        break;
    }
  };

  return (
    <div
      ref={ref}
      role="switch"
      aria-checked={checked}
      tabIndex={focusable && !disabled ? 0 : undefined}
      onClick={() => !disabled && onChange(!checked)}
      onKeyDown={focusable ? handleKeyDown : undefined}
      onFocus={() => setFocused(true)}
      onBlur={() => setFocused(false)}
      style={{
        width, height, borderRadius: height, padding: pad,
        background: checked ? C.accent : C.track,
        display: 'flex', alignItems: 'center',
        transition: 'background .2s', flexShrink: 0,
        cursor: disabled ? 'default' : 'pointer', opacity: disabled ? 0.7 : 1,
        outline: 'none',
        boxShadow: focusable && focused ? SHADOW.focus : undefined,
      }}
    >
      <div style={{
        width: thumb, height: thumb, borderRadius: '50%',
        background: C.bgWhite, boxShadow: SHADOW.thumb,
        marginLeft: checked ? width - thumb - pad * 2 : 0,
        transition: 'margin .2s',
      }} />
    </div>
  );
});
