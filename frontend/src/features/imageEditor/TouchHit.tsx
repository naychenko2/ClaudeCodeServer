// Невидимая зона касания вокруг компактной кнопки (xs, 24 px) на мобиле: рамка и фон кнопки
// не меняются, а прозрачный слой под ней добирает высоту до 44 и зовёт тот же обработчик.
// Слой лежит ПОД кнопкой (раньше в DOM, кнопка — в позиционированной обёртке поверх),
// поэтому наведение/нажатие самой кнопки и фокус с клавиатуры остаются её собственными (n1, n2).

import type { ReactNode } from 'react';

const BUTTON_XS = 24;
const TOUCH_MIN = 44;
const OUTSET = (TOUCH_MIN - BUTTON_XS) / 2;

export function TouchHit({ on, onTap, children }: { on: boolean; onTap: () => void; children: ReactNode }) {
  if (!on) return <>{children}</>;
  return (
    <span data-touch-hit="true" style={{ position: 'relative', display: 'inline-flex', flexShrink: 0 }}>
      <span aria-hidden="true" onClick={onTap}
        style={{ position: 'absolute', inset: `-${OUTSET}px 0`, cursor: 'pointer' }} />
      <span style={{ position: 'relative', display: 'inline-flex' }}>{children}</span>
    </span>
  );
}
