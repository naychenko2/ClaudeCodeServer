import type { ReactNode } from 'react';
import { SP } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';

/**
 * Обёртка сеток раздела. Числа ширины живут ЗДЕСЬ, в одном месте: карточку мы уже
 * свели в один компонент, а две копии обёртки успели разъехаться — у ленты минимум
 * оказался таким, что на узком экране колонка оставалась одна вместо двух.
 *
 * Расчёт для нижней границы целевых устройств: 360 CSS минус поля страницы (16×2) —
 * это 328 доступных. Две колонки с зазором SP.md влезают, пока минимум ≤ 158.
 */
export function VideoGrid({ minWidth, children }: {
  /** Минимальная ширина колонки на десктопе. */
  minWidth: number;
  children: ReactNode;
}) {
  const isMobile = useIsMobile();

  return (
    <div style={{
      display: 'grid', gap: SP.md,
      gridTemplateColumns: `repeat(auto-fill, minmax(${isMobile ? MOBILE_MIN : minWidth}px, 1fr))`,
    }}>
      {children}
    </div>
  );
}

// Один минимум на обе сетки: на таком экране разница «у ролика название длиннее»
// всё равно не окупается — обе подписи переносятся в две строки
const MOBILE_MIN = 148;
