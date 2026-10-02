// Фактическая ширина элемента (CSS-пиксели): полоса и строка контекста подстраиваются под свою колонку,
// а не под ширину окна — «Файлы» и соседние панели съедают место и на широком экране

import { useEffect, useState, type RefObject } from 'react';

export function useBoxWidth(ref: RefObject<HTMLElement | null>): number {
  const [w, setW] = useState(0);
  useEffect(() => {
    const el = ref.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(() => setW(Math.round(el.getBoundingClientRect().width)));
    ro.observe(el);
    setW(Math.round(el.getBoundingClientRect().width));
    return () => ro.disconnect();
  }, [ref]);
  return w;
}

// Минимальная тач-цель на телефоне (CSS px)
export const TOUCH = 44;
