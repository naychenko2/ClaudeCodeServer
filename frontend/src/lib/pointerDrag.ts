// Общий сценарий перетаскивания сплиттера: подписка на pointermove/pointerup,
// курсор ресайза и запрет выделения текста на время drag'а.
//
// Вынесено из компонентов по двум причинам: код дословно повторялся во всех
// сплиттерах (ширина сайдбара, колонки рельс, split чат|файл), а мутацию
// document.body.style внутри компонента ловит правило react-hooks/immutability.
//
// Возвращать ничего не нужно: обработчики снимаются сами по концу жеста.
export function startPointerDrag(
  onMove: (e: PointerEvent) => void,
  opts?: { cursor?: string; onEnd?: () => void },
): void {
  const end = () => {
    document.removeEventListener('pointermove', onMove);
    document.removeEventListener('pointerup', end);
    document.removeEventListener('pointercancel', end);
    document.body.style.cursor = '';
    document.body.style.userSelect = '';
    opts?.onEnd?.();
  };
  document.body.style.cursor = opts?.cursor ?? 'col-resize';
  document.body.style.userSelect = 'none';
  document.addEventListener('pointermove', onMove);
  document.addEventListener('pointerup', end);
  // pointercancel — обязателен, а не «на всякий случай»: браузер забирает жест себе
  // (прокрутка пальцем, системный свайп, потеря фокуса окна) и pointerup НЕ шлёт вовсе.
  // Без этой строки onEnd не вызывается, и всё, что вызывающий поднял на время жеста —
  // курсор, запрет выделения, перекрывающий экран слой — остаётся висеть навсегда.
  document.addEventListener('pointercancel', end);
}
