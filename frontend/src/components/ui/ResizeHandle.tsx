import { useEffect, useRef, useState } from 'react';
import type { KeyboardEvent, PointerEvent as ReactPointerEvent } from 'react';
import { SHADOW, Z } from '../../lib/design';
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
  // Кольцо фокуса — только при фокусе с клавиатуры (:focus-visible), не от клика мышью.
  const [focusVisible, setFocusVisible] = useState(false);
  // Снятие слушателей текущего перетаскивания: размонтирование посреди drag обязано
  // их снять, иначе onChange уйдёт в уже несуществующую панель.
  const stopDrag = useRef<(() => void) | null>(null);
  useEffect(() => () => stopDrag.current?.(), []);
  const clamp = (w: number) => Math.max(min, Math.min(max, Math.round(w)));

  const onPointerDown = (e: ReactPointerEvent) => {
    e.preventDefault();
    stopDrag.current?.();
    // Захват указателя: события идут сюда, даже если курсор ушёл в iframe или за окно.
    try { e.currentTarget.setPointerCapture(e.pointerId); } catch { /* указатель уже отпущен */ }
    const startX = e.clientX;
    const startW = value;
    setDrag(true);
    const move = (ev: PointerEvent) => onChange(clamp(startW + (startX - ev.clientX)));
    const detach = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      window.removeEventListener('pointercancel', up);
      stopDrag.current = null;
    };
    const up = () => {
      detach();
      setDrag(false);
    };
    stopDrag.current = detach;
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
      onFocus={e => { setFocus(true); setFocusVisible(e.currentTarget.matches(':focus-visible')); }}
      onBlur={() => { setFocus(false); setFocusVisible(false); }}
      style={{
        position: 'absolute', left: 0, top: 0, bottom: 0, zIndex: Z.panelEdge,
        display: 'flex', outline: 'none',
        boxShadow: focusVisible ? SHADOW.focus : undefined,
      }}
    >
      <Splitter active={drag || focus} onMouseDown={onPointerDown} />
    </div>
  );
}
