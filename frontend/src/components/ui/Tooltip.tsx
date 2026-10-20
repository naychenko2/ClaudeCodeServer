import { useLayoutEffect, useEffect, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { C, FONT, FS, R, SHADOW, SP, Z } from '../../lib/design';
import { useCanHover } from '../../lib/pointer';

// Подсказка по наведению в стиле приложения — вместо браузерного title, который
// не оформить и не наполнить разметкой (цветные метки, строки списком).
//
// Плашка рисуется ПОРТАЛОМ в document.body с position: fixed (как ui/Menu в
// anchor-режиме): absolute внутри карточки обрезалась overflow панели списка, а у
// правого края экрана уезжала за кромку (x 1158 → 1537 при 1280). fixed отсчитывается
// от viewport, только пока ни у одного предка нет transform/filter — а карточка
// держит transform ради свайпа, поэтому портал надёжнее. Позиция — чистая функция
// tooltipPlacement (тестируется без DOM): под якорем, выровнена по align, зажатая в
// окно с полями; перенос строк + maxWidth по окну.
//
// По наведению: пальцем наведения нет (эмулированный mouseenter при тапе без mouseleave
// оставлял бы плашку висеть), поэтому на таче она по наведению не поднимается вовсе —
// там всё нужное должно быть доступно по нажатию. По КЛАВИАТУРЕ (M1) плашка поднимается
// и по фокусу: onFocus/onBlur на обёртке ловят focusin/focusout с обёртки и её потомков,
// а проп focused — когда фокус-цель (кнопка-строка/карточка) — ПРЕДОК обёртки, и её
// focusin до обёртки не доходит (вложенный span метки фокусом быть не может: в <button>
// недопустим). Имя для скринридера плашка не несёт: aria-label/aria-describedby ставит якорь.

// Поля плашки от кромки окна (px): не прилипает к рамке, но и не уезжает за неё.
const EDGE_PAD = 8;
// Минимальная ширина плашки (px): не сжимается до нуля, когда якорь у самой кромки.
const MIN_W = 120;

// Чистая функция: позиция плашки (position: fixed) по rect якоря и ширине окна.
//   top    — под якорем (anchor.bottom + gap);
//   left   — по align (start — левый край якоря, end — правый), ЗАЖАТА в окно:
//            правый край (left + MIN_W) не за правую кромку, левый не за левую;
//   maxWidth — до правого края окна (с полем): перенос строк влезает в окно.
// Мутация «убрать зажим по краю» (Math.max(pad, …) / правый сдвиг) красит тест.
export function tooltipPlacement(
  anchor: { left: number; right: number; bottom: number },
  viewportWidth: number,
  align: 'start' | 'end',
  gap: number,
  pad: number,
): { left: number; top: number; maxWidth: number } {
  const desired = align === 'end' ? anchor.right : anchor.left;
  // Правый край плашки (left + MIN_W) не за правую кромку окна: сдвигаем влево.
  let left = desired;
  if (left + MIN_W > viewportWidth - pad) left = viewportWidth - pad - MIN_W;
  // Левый край не за левую кромку окна.
  left = Math.max(pad, left);
  // Потолок ширины — до правого края окна (с полем): перенос строк влезает в окно.
  const maxWidth = Math.max(MIN_W, viewportWidth - pad - left);
  return { left, top: anchor.bottom + gap, maxWidth };
}

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
  const anchorRef = useRef<HTMLSpanElement | null>(null);
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

  // Позиция плашки: по rect якоря (getBoundingClientRect) + ширина окна. useLayoutEffect —
  // замер ПОСЛЕ вставки якоря в DOM, до отрисовки (иначе плашка мигнула бы в неверной
  // позиции). window.innerWidth — ширина viewport; в тестах fake-DOM задаёт её явно.
  const [placement, setPlacement] = useState<{ left: number; top: number; maxWidth: number } | null>(null);
  useLayoutEffect(() => {
    if (!open) { setPlacement(null); return; }
    const el = anchorRef.current;
    if (!el) return;
    const rect = el.getBoundingClientRect();
    const vw = typeof window !== 'undefined' && window.innerWidth > 0 ? window.innerWidth : 1024;
    setPlacement(tooltipPlacement({ left: rect.left, right: rect.right, bottom: rect.bottom }, vw, align, SP.sm - 1, EDGE_PAD));
  }, [open, align]);

  return (
    <span ref={anchorRef} onMouseEnter={show} onMouseLeave={hide} onMouseDown={hide}
      onFocus={focusIn} onBlur={focusOut}
      style={{ position: 'relative', display: 'inline-flex', flexShrink: 0 }}>
      {children}
      {open && placement && createPortal(
        <span role="tooltip" id={id} style={{
          position: 'fixed', left: placement.left, top: placement.top, maxWidth: placement.maxWidth,
          zIndex: Z.dropdown,
          background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.md,
          boxShadow: SHADOW.dropdown, padding: `${SP.xs + 1}px ${SP.sm + 2}px`,
          fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 500, color: C.textHeading,
          whiteSpace: 'normal', pointerEvents: 'none',
        }}>
          {content}
        </span>,
        document.body
      )}
    </span>
  );
}
