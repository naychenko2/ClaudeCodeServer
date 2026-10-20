import { useEffect, useRef, useState, type ReactNode } from 'react';
import { C, FONT, FS, R, SHADOW, SP, Z } from '../../lib/design';
import { useCanHover } from '../../lib/pointer';

// Подсказка по наведению в стиле приложения — вместо браузерного title, который
// не оформить и не наполнить разметкой (цветные метки, строки списком).
//
// Плашка встаёт под якорем и прижимается к его левому (align="start") или правому
// (align="end") краю: у правой кромки экрана берут "end", иначе плашка уедет за край.
// По наведению: пальцем наведения нет (эмулированный mouseenter при тапе без mouseleave
// оставлял бы плашку висеть), поэтому на таче она по наведению не поднимается вовсе —
// там всё нужное должно быть доступно по нажатию. По КЛАВИАТУРЕ (M1) плашка поднимается
// и по фокусу: onFocus/onBlur на обёртке ловят focusin/focusout с обёртки и её потомков,
// а проп focused — когда фокус-цель (кнопка-строка/карточка) — ПРЕДОК обёртки, и её
// focusin до обёртки не доходит (вложенный span метки фокусом быть не может: в <button>
// недопустим). Имя для скринридера плашка не несёт: aria-label/aria-describedby ставит якорь.
export function Tooltip({ content, align = 'start', disabled, focused, id, children }: {
  content: ReactNode;
  align?: 'start' | 'end';
  // Погасить, не трогая наведения: например, пока у якоря открыт собственный поповер
  disabled?: boolean;
  // Фокус на якорной кнопке-строке/карточке (M1): её focusin до обёртки не доходит,
  // поэтому фокус-состояние передаёт сам якорь.
  focused?: boolean;
  // id плашки: якорь-кнопка-строка ссылается на неё через aria-describedby (M1, минор) —
  // описание подсказки живёт на интерактивном якоре, а не на неинтерактивном span метки.
  id?: string;
  children: ReactNode;
}) {
  const canHover = useCanHover();
  const [hovered, setHovered] = useState(false);
  const [kb, setKb] = useState(false);
  const timer = useRef<number | null>(null);
  const stop = () => { if (timer.current != null) { clearTimeout(timer.current); timer.current = null; } };
  useEffect(() => stop, []);

  // Задержка как у нативной подсказки: курсор, проходящий мимо, плашку не дёргает
  const show = () => { if (!canHover) return; stop(); timer.current = window.setTimeout(() => setHovered(true), 350); };
  const hide = () => { stop(); setHovered(false); };
  // Фокус (клавиатура): без задержки — фокус здесь deliberate, не «проход мимо».
  // П.48: на тач (canHover=false) focusin — от тапа, а до контракта по фокусу плашка
  // на тач не поднималась вовсе (только hover, а его на тач нет) — не плодим нового
  // поведения: kb-состояние ставим только когда наведение в принципе возможно.
  const focusIn = () => { if (canHover) setKb(true); };
  const focusOut = () => setKb(false);

  const open = (hovered || kb || (focused ?? false)) && !disabled;

  return (
    <span onMouseEnter={show} onMouseLeave={hide} onMouseDown={hide}
      onFocus={focusIn} onBlur={focusOut}
      style={{ position: 'relative', display: 'inline-flex', flexShrink: 0 }}>
      {children}
      {open && (
        <span role="tooltip" id={id} style={{
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
