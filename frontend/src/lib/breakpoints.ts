import { useEffect, useState } from 'react';

// Единый порог мобильной раскладки для всего приложения.
// Ширина ≤ MOBILE_MAX → одноколоночная мобильная раскладка (сайдбар в drawer),
// выше — двухпанельная (планшет/десктоп).
//
// Значение 699 (а не 768), чтобы раскладные экраны получали полноценную раскладку
// с сайдбаром: Galaxy Fold в развёрнутом виде при увеличенном «Масштабе экрана»
// Samsung отдаёт ~716px CSS. Обычные телефоны в портрете (<500) и Fold в сложенном
// виде остаются мобильными.
export const MOBILE_MAX = 699;
export const MOBILE_QUERY = `(max-width: ${MOBILE_MAX}px)`;

// Реактивный флаг «мобильная раскладка» (matchMedia, обновляется при resize/повороте).
export function useIsMobile(): boolean {
  const [m, setM] = useState(() =>
    typeof window !== 'undefined' ? window.matchMedia(MOBILE_QUERY).matches : false);
  useEffect(() => {
    const mq = window.matchMedia(MOBILE_QUERY);
    const h = (e: MediaQueryListEvent) => setM(e.matches);
    setM(mq.matches);
    mq.addEventListener('change', h);
    return () => mq.removeEventListener('change', h);
  }, []);
  return m;
}

// Текущая ширина окна — для мест, где нужен не только флаг, но и само значение
// (например, отдельный порог планшета).
export function useWindowWidth(): number {
  const [w, setW] = useState(() => (typeof window !== 'undefined' ? window.innerWidth : 0));
  useEffect(() => {
    const h = () => setW(window.innerWidth);
    window.addEventListener('resize', h);
    return () => window.removeEventListener('resize', h);
  }, []);
  return w;
}
