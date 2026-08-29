import { useSyncExternalStore, type CSSProperties } from 'react';

// «Можно ли рассчитывать на наведение» — не по паспорту устройства, а по тому,
// чем человек работает прямо сейчас.
//
// matchMedia('(hover: hover)') описывает ПЕРВИЧНЫЙ указатель: планшет с
// клавиатурой-обложкой и Windows-гибрид отвечают «умею», хотя человек тычет
// пальцем. А тач шлёт эмулированный mouseenter при тапе и НЕ шлёт mouseleave,
// пока не тапнут в другое место, — отсюда обе беды сразу: подписи кнопок
// залипали на экране, а контролы шапки панели, наоборот, схлопывались ровно в
// момент нажатия (кнопка фильтра пропадала из-под пальца).
//
// Поэтому media query здесь — лишь стартовое значение, а дальше следим за
// реальным вводом. pointerdown приходит РАНЬШЕ эмулированных мышиных событий,
// так что переключение успевает к первому же тапу.

let canHover = typeof window === 'undefined' || !window.matchMedia?.('(hover: none)').matches;
const subs = new Set<() => void>();

if (typeof window !== 'undefined') {
  window.addEventListener('pointerdown', (e: PointerEvent) => {
    // Стилус наводить умеет (Surface Pen, S Pen), поэтому «без наведения» — только палец
    const next = e.pointerType !== 'touch';
    if (next === canHover) return;
    canHover = next;
    subs.forEach(f => f());
    // capture: значение должно обновиться до того, как событие дойдёт до компонентов
  }, { capture: true });
}

function subscribe(f: () => void) {
  subs.add(f);
  return () => { subs.delete(f); };
}

export function useCanHover(): boolean {
  // Серверного рендера у нас нет, но getServerSnapshot обязателен по контракту хука
  return useSyncExternalStore(subscribe, () => canHover, () => true);
}

// Щит от нативного long-press на иконочных кнопках. Chrome на Android по удержанию
// на кнопке с <svg> внутри поднимает СВОЁ меню («Скачать / Поделиться / Печать»),
// а iOS — callout с выделением: чужой попап накрывает наш и перебивает нажатие.
// Гасится парой «стиль + onContextMenu»: стиль снимает callout и выделение, а
// preventDefault на contextmenu — само меню (Chrome шлёт это событие по удержанию).
// Ставить обязан КАЖДЫЙ, кто вешает долгое нажатие или иконку в тач-достижимом
// месте — иначе жест работает, но поверх него встаёт браузер.
export const TOUCH_CALLOUT_GUARD: CSSProperties = {
  WebkitTouchCallout: 'none',
  WebkitUserSelect: 'none',
  userSelect: 'none',
  touchAction: 'manipulation',
};
