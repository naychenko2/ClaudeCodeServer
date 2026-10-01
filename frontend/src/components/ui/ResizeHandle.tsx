import { useState } from 'react';
import type { KeyboardEvent, PointerEvent as ReactPointerEvent } from 'react';
import { Splitter } from './Splitter';

// Ручка ширины правой панели за её ЛЕВЫЙ край: тянем влево — панель шире.
// Вид — общий Splitter; сверху — то, чего у него нет: клавиатура и роль separator
// с aria-value*, чтобы ширину можно было менять без мыши и слышать её.
// Родитель панели обязан быть position: relative — ручка прижата к его кромке.
const KEY_STEP = 16;

export function ResizeHandle({ value, min = 340, max = 520, onChange, ariaLabel = 'Ширина панели' }: {
  value: number;
  min?: number;
  max?: number;
  onChange: (w: number) => void;
  ariaLabel?: string;
}) {
  const [drag, setDrag] = useState(false);
  const [focus, setFocus] = useState(false);
  const clamp = (w: number) => Math.max(min, Math.min(max, Math.round(w)));

  const onPointerDown = (e: ReactPointerEvent) => {
    e.preventDefault();
    const startX = e.clientX;
    const startW = value;
    setDrag(true);
    const move = (ev: PointerEvent) => onChange(clamp(startW + (startX - ev.clientX)));
    const up = () => {
      setDrag(false);
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      window.removeEventListener('pointercancel', up);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
    window.addEventListener('pointercancel', up);
  };

  const onKeyDown = (e: KeyboardEvent) => {
    let next: number | null = null;
    if (e.key === 'ArrowLeft') next = value + KEY_STEP;
    else if (e.key === 'ArrowRight') next = value - KEY_STEP;
    else if (e.key === 'Home') next = min;
    else if (e.key === 'End') next = max;
    if (next === null) return;
    e.preventDefault();
    onChange(clamp(next));
  };

  return (
    <div
      role="separator"
      aria-orientation="vertical"
      aria-label={ariaLabel}
      aria-valuenow={value}
      aria-valuemin={min}
      aria-valuemax={max}
      tabIndex={0}
      title={`Потяните, чтобы изменить ширину: ${min}–${max} px`}
      onKeyDown={onKeyDown}
      onFocus={() => setFocus(true)}
      onBlur={() => setFocus(false)}
      style={{
        position: 'absolute', left: 0, top: 0, bottom: 0, zIndex: 3,
        display: 'flex', outline: 'none',
      }}
    >
      <Splitter active={drag || focus} onMouseDown={onPointerDown} />
    </div>
  );
}
