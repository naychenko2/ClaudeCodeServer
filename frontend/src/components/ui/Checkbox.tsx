import { forwardRef, useState } from 'react';
import type { KeyboardEvent } from 'react';
import { Check } from 'lucide-react';
import { C, PANEL_ANIM, R, SHADOW } from '../../lib/design';

export interface CheckboxProps {
  checked: boolean;
  onChange: (v: boolean) => void;
  disabled?: boolean;
  /** Доступное имя: без него скринридер читает безымянный «checkbox». */
  ariaLabel?: string;
}

// === Чекбокс ===
// Третье появление шаблона «квадрат с галкой» в проекте (DocsScopeDialog рисует руками,
// PromptSnapshotDialog ставит голый `<input type="checkbox">`) — по гайду это уже паттерн,
// заводим примитиву. Тач-цель 40×40 при визуальном квадрате 18: на пальце одной высоты
// текста мало, и без резерва клик промахивался по смежным строкам списка.
// Клавиатура: Space и Enter переключают (стандарт HTML), Tab входит/выходит из фокуса.
export const Checkbox = forwardRef<HTMLButtonElement, CheckboxProps>(function Checkbox(
  { checked, onChange, disabled, ariaLabel },
  ref,
) {
  const [focused, setFocused] = useState(false);

  const handleKeyDown = (e: KeyboardEvent<HTMLButtonElement>) => {
    if (disabled) return;
    if (e.key === ' ' || e.key === 'Enter') {
      e.preventDefault();
      onChange(!checked);
    }
  };

  return (
    <button
      ref={ref}
      type="button"
      role="checkbox"
      aria-checked={checked}
      aria-label={ariaLabel}
      disabled={disabled}
      tabIndex={disabled ? -1 : 0}
      onClick={() => onChange(!checked)}
      onKeyDown={handleKeyDown}
      onFocus={() => setFocused(true)}
      onBlur={() => setFocused(false)}
      style={{
        width: 40, height: 40,
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
        padding: 0, border: 'none', borderRadius: R.md,
        background: 'transparent', cursor: disabled ? 'default' : 'pointer',
        flexShrink: 0, opacity: disabled ? 0.6 : 1,
        outline: 'none',
        boxShadow: focused ? SHADOW.focus : 'none',
      }}
    >
      <span
        aria-hidden
        style={{
          width: 18, height: 18, borderRadius: R.sm,
          border: `1.5px solid ${checked ? C.accent : C.border}`,
          background: checked ? C.accent : C.bgWhite,
          display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
          transition: `background ${PANEL_ANIM}, border-color ${PANEL_ANIM}`,
        }}
      >
        {checked && <Check size={13} strokeWidth={3} color={C.onAccent} />}
      </span>
    </button>
  );
});
