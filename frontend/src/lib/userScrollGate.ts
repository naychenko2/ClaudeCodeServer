import { useCallback, useEffect, useRef } from 'react';

// Гейт «пользовательский скролл». Возвращает функцию isUserScroll(): true в течение
// короткого окна после живого жеста пользователя (колесо, тач, перетаскивание полосы
// прокрутки, навигационные клавиши), false — для ПРОГРАММНОГО скролла (присваивание
// scrollTop, ResizeObserver-прижим ленты при streaming/ожидании, scrollIntoView).
//
// Зачем: контекстные меню/попапы закрываются по capture-обработчику 'scroll' на любом
// скролле в документе. Программный скролл ленты чата (когда Claude работает и ленту
// прижимает к низу) тоже порождает scroll-событие — и меню схлопывалось само, хотя
// пользователь его не закрывал. Гейт ставится первым условием в onScroll: закрыть
// разрешается только если скролл сделан пользователем.
//
// Жесты ловим на capture-фазе глобально: скроллить может любой контейнер, и жесты
// (колесо/тач) идут через окно, а не всплывают от скролл-контейнера. Клавиатурная
// прокрутка идёт мимо скролл-контейнера (фокуса у него нет), потому слушаем окно, но
// только навигационные клавиши и не во время набора в поле ввода.
const USER_SCROLL_WINDOW_MS = 250;
const NAV_KEYS = ['PageUp', 'PageDown', 'Home', 'End', 'ArrowUp', 'ArrowDown', ' '];

function isFormField(t: EventTarget | null): boolean {
  if (!(t instanceof HTMLElement)) return false;
  return t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable;
}

export function useUserScrollGate(windowMs = USER_SCROLL_WINDOW_MS): () => boolean {
  const untilRef = useRef(0);
  useEffect(() => {
    const mark = () => { untilRef.current = Date.now() + windowMs; };
    const onKey = (e: KeyboardEvent) => {
      if (!NAV_KEYS.includes(e.key) || isFormField(e.target)) return;
      mark();
    };
    const opts: AddEventListenerOptions = { capture: true, passive: true };
    document.addEventListener('wheel', mark, opts);
    document.addEventListener('touchmove', mark, opts);
    document.addEventListener('mousedown', mark, true); // перетаскивание полосы прокрутки
    window.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('wheel', mark, opts);
      document.removeEventListener('touchmove', mark, opts);
      document.removeEventListener('mousedown', mark, true);
      window.removeEventListener('keydown', onKey);
    };
  }, [windowMs]);
  return useCallback(() => Date.now() <= untilRef.current, []);
}
