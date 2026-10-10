import { useState } from 'react';
import { C } from '../../lib/design';
import { Dot } from './Dot';

// Период «дыхания» — тот же, что у .cc-live-dot в index.css
const BREATH_MS = 2400;

// Живая точка: процесс идёт, а сколько осталось — неизвестно. Замена бегущей полосе и
// спиннеру там, где движение не должно тянуть взгляд: точка медленно разгорается и гаснет.
// Форма — общий Dot (SVG, круглый на любом DPR); фаза привязана к часам, а не к монтированию,
// поэтому несколько точек в ленте дышат в такт. prefers-reduced-motion — неподвижна.
// label — подпись для скринридера («Выполняется»)
export function LiveDot({ color = C.accent, size = 6, label = 'Выполняется' }: {
  color?: string;
  size?: number;
  label?: string;
}) {
  const [delay] = useState(() => -(Date.now() % BREATH_MS));
  return (
    <span role="status" aria-label={label} className="cc-live-dot"
      style={{ display: 'inline-flex', flexShrink: 0, animationDelay: `${delay}ms` }}>
      <Dot color={color} size={size} />
    </span>
  );
}
