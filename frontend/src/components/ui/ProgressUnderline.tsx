import type { CSSProperties, ReactNode } from 'react';
import { C } from '../../lib/design';

// Толщина линии подчёркивания
const LINE_PX = 2;
// Малый процент всё равно виден: линия не короче нескольких пикселей (как MIN_FILL_PX у полосы)
const MIN_PX = 4;

// Прогресс подчёркиванием текста: линия 2 px под children на долю процента, без дорожки.
// Отдельной строки под полосу не нужно — подчёркивается то, что идёт сейчас (текущий этап
// «тесты 1:25», подпись «осталось ≈1:15»). value — проценты 0..100; estimate — оценка, а не
// факт: линия точечная; label — подпись для скринридера. style — для обрезки многоточием
// в узкой строке (линия лежит внутри блока, overflow её не срезает)
export function ProgressUnderline({ value, estimate, label, transition = 'width .5s linear', style, children }: {
  value: number;
  estimate?: boolean;
  label?: string;
  transition?: string;
  style?: CSSProperties;
  children: ReactNode;
}) {
  const pct = Math.max(0, Math.min(100, value));
  return (
    <span
      role="progressbar"
      aria-valuenow={Math.round(pct)}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-label={label}
      style={{ position: 'relative', display: 'inline-block', ...style }}
    >
      {children}
      <span aria-hidden style={{
        position: 'absolute', left: 0, bottom: 0, width: `max(${MIN_PX}px, ${pct}%)`,
        borderTop: `${LINE_PX}px ${estimate ? 'dotted' : 'solid'} ${C.accent}`, transition,
      }} />
    </span>
  );
}
