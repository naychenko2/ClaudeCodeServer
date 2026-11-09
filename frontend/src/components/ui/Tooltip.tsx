import { useLayoutEffect, useEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react';
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
// m-c: потолок ширины плашки (px): у правого края сдвигаем влево на фактическую ширину
// (не шире MAX_W), а не сжимаем до MIN_W — иначе столбик 120 px на 6 строк.
const MAX_W = 260;

// Чистая функция: позиция плашки (position: fixed) по rect якоря, окну и размеру плашки.
//   left   — по align: start — левый край якоря; end — ПРАВЫЙ край плашки к правому краю
//            якоря (m-d: не «вправо от якоря»); ЗАЖАТА в окно: правый край (left + width)
//            не за правую кромку (сдвиг влево на фактическую ширину, m-c), левый не за
//            левую;
//   top    — под якорем (anchor.bottom + gap); у нижней кромки окна — переворот вверх
//            (m-a: anchor.bottom - gap - height), не срез;
//   maxWidth — до правого края окна (с полем), но не шире MAX_W (m-c): перенос строк
//            влезает в окно, но плашка не сжимается в столбик.
// size — фактический размер плашки (замер после рендера, два прохода); width/height
// используются для зажима по краям, не для отрисовки.
// Мутации: «align=end → left = anchor.right» / «без переворота вверх» / «сдвиг на MIN_W»
// красят тесты.
export function tooltipPlacement(
  anchor: { left: number; right: number; bottom: number },
  viewport: { width: number; height: number },
  align: 'start' | 'end',
  gap: number,
  pad: number,
  size: { width: number; height: number },
): { left: number; top: number; maxWidth: number } {
  // m-d: align=end — правый край плашки к правому краю якоря (left = anchor.right - width).
  const desired = align === 'end' ? anchor.right - size.width : anchor.left;
  // m-c: у правого края — сдвиг влево на фактическую ширину (не сжатие до MIN_W).
  let left = desired;
  if (left + size.width > viewport.width - pad) left = viewport.width - pad - size.width;
  // Левый край не за левую кромку окна.
  left = Math.max(pad, left);
  // m-a: у нижней кромки — переворот вверх (top = anchor.bottom - gap - height), не срез.
  let top = anchor.bottom + gap;
  if (anchor.bottom + gap + size.height > viewport.height - pad) {
    top = anchor.bottom - gap - size.height;
  }
  top = Math.max(pad, top);
  // m-c: maxWidth — до правого края окна (с полем), но не шире MAX_W и не уже MIN_W.
  const maxWidth = Math.min(MAX_W, Math.max(MIN_W, viewport.width - pad - left));
  return { left, top, maxWidth };
}

export function Tooltip({ content, align = 'start', disabled, focused, id, style, children }: {
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
  // Стили обёртки-якоря (m-b): например, zIndex, чтобы метка не тонула под hover-кнопками
  // карточки и оставалась наводимой (плашка поднимается от метки, а не от центра).
  style?: CSSProperties;
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

  // Позиция плашки: по rect якоря + фактическому размеру плашки (два прохода). useLayoutEffect —
  // замер ПОСЛЕ вставки, до отрисовки (иначе плашка мигнула бы в неверной позиции).
  // Проход 1: позиция по rect якоря и текущему размеру (size — замер предыдущего прохода,
  // иначе оценка MAX_W×30). Проход 2: замер фактической плашки (width/height); если размер
  // отличается от зажатого — пересчёт позиции (сдвиг влево / переворот по реальному размеру).
  const tipRef = useRef<HTMLSpanElement | null>(null);
  const [size, setSize] = useState<{ width: number; height: number } | null>(null);
  const [placement, setPlacement] = useState<{ left: number; top: number; maxWidth: number } | null>(null);
  useLayoutEffect(() => {
    if (!open) { setPlacement(null); setSize(null); return; }
    const el = anchorRef.current;
    if (!el) return;
    const rect = el.getBoundingClientRect();
    const vw = typeof window !== 'undefined' && window.innerWidth > 0 ? window.innerWidth : 1024;
    const vh = typeof window !== 'undefined' && window.innerHeight > 0 ? window.innerHeight : 768;
    const s = size ?? { width: MAX_W, height: 30 };
    setPlacement(tooltipPlacement({ left: rect.left, right: rect.right, bottom: rect.bottom }, { width: vw, height: vh }, align, SP.sm - 1, EDGE_PAD, s));
  }, [open, align, size]);
  useLayoutEffect(() => {
    if (!open || !placement) return;
    const tip = tipRef.current;
    if (!tip) return;
    const r = tip.getBoundingClientRect();
    const w = Math.round(r.width);
    const h = Math.round(r.height);
    const cur = size;
    // Только если размер реально изменился (иначе зацикливание на суб-пиксельном дрожании).
    if (!cur || cur.width !== w || cur.height !== h) setSize({ width: w, height: h });
  }, [open, placement]);

  return (
    <span ref={anchorRef} onMouseEnter={show} onMouseLeave={hide} onMouseDown={hide}
      onFocus={focusIn} onBlur={focusOut}
      style={{ position: 'relative', display: 'inline-flex', flexShrink: 0, ...style }}>
      {children}
      {open && placement && createPortal(
        <span ref={tipRef} role="tooltip" id={id} style={{
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
