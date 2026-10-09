import { useEffect, useRef, useState, type ReactNode } from 'react';
import { C, FONT, FS, R, SHADOW, SP, Z } from '../../lib/design';
import { useCanHover } from '../../lib/pointer';

// Подсказка по наведению в стиле приложения — вместо браузерного title, который
// не оформить и не наполнить разметкой (цветные метки, строки списком).
//
// Плашка встаёт под якорем и прижимается к его левому (align="start") или правому
// (align="end") краю: у правой кромки экрана берут "end", иначе плашка уедет за край.
// Только для наведения: пальцем наведения нет (эмулированный mouseenter при тапе без
// mouseleave оставлял бы плашку висеть), поэтому на таче она не поднимается вовсе —
// там всё нужное должно быть доступно по нажатию. Имя для скринридера плашка не несёт:
// aria-label ставит сам якорь.
export function Tooltip({ content, align = 'start', disabled, children }: {
  content: ReactNode;
  align?: 'start' | 'end';
  // Погасить, не трогая наведения: например, пока у якоря открыт собственный поповер
  disabled?: boolean;
  children: ReactNode;
}) {
  const canHover = useCanHover();
  const [open, setOpen] = useState(false);
  const timer = useRef<number | null>(null);
  const stop = () => { if (timer.current != null) { clearTimeout(timer.current); timer.current = null; } };
  useEffect(() => stop, []);

  // Задержка как у нативной подсказки: курсор, проходящий мимо, плашку не дёргает
  const show = () => { if (!canHover) return; stop(); timer.current = window.setTimeout(() => setOpen(true), 350); };
  const hide = () => { stop(); setOpen(false); };

  return (
    <span onMouseEnter={show} onMouseLeave={hide} onMouseDown={hide}
      style={{ position: 'relative', display: 'inline-flex', flexShrink: 0 }}>
      {children}
      {open && !disabled && (
        <span role="tooltip" style={{
          position: 'absolute', top: `calc(100% + ${SP.sm - 1}px)`, zIndex: Z.dropdown,
          ...(align === 'end' ? { right: 0 } : { left: 0 }),
          background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.md,
          boxShadow: SHADOW.dropdown, padding: `${SP.xs + 1}px ${SP.sm + 2}px`,
          fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 500, color: C.textHeading,
          whiteSpace: 'nowrap', pointerEvents: 'none',
        }}>
          {content}
        </span>
      )}
    </span>
  );
}
