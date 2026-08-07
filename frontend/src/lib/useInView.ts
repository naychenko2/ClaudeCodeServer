import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';

// Lazy-обнаружение видимости элемента через IntersectionObserver.
//
// Контекст: ChatImage раньше фетчил base64 картинки в useEffect при монтировании. Лента чата
// не виртуализирована — все картинки монтируются разом и фетчатся одновременно, забивая пул
// браузера. Этот хук гейтит «надо грузить?» по фактическому попаданию во viewport (с буфером
// rootMargin для префетча), чтобы невидимые картинки не фетчились.
//
// root: null (viewport, по умолчанию) работает и для скролл-контейнера (overflow:auto): элемент,
// прокрученный за видимую область, имеет getBoundingClientRect за пределами viewport →
// isIntersecting=false. При желании точного rootMargin от границ скролл-области можно передать
// root (ref на контейнер) — сейчас этого не требуется, prop drilling не заводим.

export function useInView(opts?: {
  rootMargin?: string;
  threshold?: number;
  root?: RefObject<Element | null>;
}): [ref: (node: HTMLElement | null) => void, inView: boolean] {
  const { rootMargin = '300px', threshold = 0, root } = opts ?? {};

  // Нет IO (SSR/старый браузер/node-тесты) — грузим сразу: семафор всё равно ограничит
  // параллельность, lazy-слой отключается (частичная митигация исходной проблемы).
  const noIO = typeof IntersectionObserver === 'undefined';

  const [inView, setInView] = useState(false);
  const observerRef = useRef<IntersectionObserver | null>(null);

  const ref = useCallback((node: HTMLElement | null) => {
    observerRef.current?.disconnect();
    observerRef.current = null;
    if (node && !noIO) {
      const observer = new IntersectionObserver(
        entries => { for (const e of entries) setInView(e.isIntersecting); },
        { root: root?.current ?? null, rootMargin, threshold },
      );
      observer.observe(node);
      observerRef.current = observer;
    }
  }, [noIO, root, rootMargin, threshold]);

  useEffect(() => () => { observerRef.current?.disconnect(); }, []);

  if (noIO) return [() => {}, true];
  return [ref, inView];
}
