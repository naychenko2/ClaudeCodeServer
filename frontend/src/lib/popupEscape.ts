// Счётчик активных попапов, открытых ВНУТРИ модалки (RoutePicker, его вложенные пикеры,
// и любые будущие всплывающие панели). Пока счётчик > 0, Modal игнорирует Escape и не
// закрывает модальное окно — пусть сначала закроется попап. Это заменяет старые capture-
// listeners в RoutePicker (`document.addEventListener('keydown', onKey, true)`), которые
// опирались на порядок capture-фазы для предотвращения закрытия Modal. Теперь Modal
// сам знает, что у него есть «слой выше», и не трогает его по Escape.

let _depth = 0;

export function incPopupDepth(): () => void {
  _depth++;
  let released = false;
  return () => {
    if (released) return;
    released = true;
    _depth = Math.max(0, _depth - 1);
  };
}

export function getPopupDepth(): number {
  return _depth;
}
