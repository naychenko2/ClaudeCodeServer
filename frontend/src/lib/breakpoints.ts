import { useEffect, useState } from 'react';

// Единый порог мобильной раскладки для всего приложения.
// Ширина ≤ MOBILE_MAX → одноколоночная мобильная раскладка (сайдбар в drawer),
// выше — двухпанельная (планшет/десктоп).
//
// Значение 600 (а не 768), чтобы раскладные экраны получали полноценную двухпанельную
// раскладку: Samsung Galaxy Fold 7 в развёрнутом виде отдаёт ~673px CSS — при пороге 699
// он ошибочно уходил в мобильную раскладку, порог 600 возвращает ему двухпанельную.
// Обычные телефоны в портрете (<500) и Fold в сложенном виде остаются мобильными.
// Побочный эффект порога 600: телефон в ландшафте (~640px) тоже станет двухпанельным.
export const MOBILE_MAX = 600;
export const MOBILE_QUERY = `(max-width: ${MOBILE_MAX}px)`;

// Порог планшета: ≤ TABLET_MAX — компактные десктоп-раскладки (иконки без подписей,
// упрощённые сетки), выше — полный десктоп. Единый источник вместо хардкодов 1024/1200.
export const TABLET_MAX = 1199;
export const TABLET_QUERY = `(max-width: ${TABLET_MAX}px)`;

// Максимальная доля ширины окна, которую боковая панель может занять в потоке.
// Больше — панель открывается оверлеем (drawer), не двигая контент. На панели
// шириной `w` это даёт порог окна `w / PANEL_INLINE_MAX_SHARE`: для типовой
// панели 300px порог ~1000px — совпадает с бывшим литералом TABLET_INLINE_MIN
// в PanelZone, который теперь производная от токена через useWindowWidth().
export const PANEL_INLINE_MAX_SHARE = 0.3;

// Реактивный флаг «мобильная раскладка» (matchMedia, обновляется при resize/повороте).
export function useIsMobile(): boolean {
  const [m, setM] = useState(() =>
    typeof window !== 'undefined' ? window.matchMedia(MOBILE_QUERY).matches : false);
  useEffect(() => {
    const mq = window.matchMedia(MOBILE_QUERY);
    const h = (e: MediaQueryListEvent) => setM(e.matches);
    // eslint-disable-next-line react-hooks/set-state-in-effect -- подписка matchMedia: синхронизация начального значения
    setM(mq.matches);
    mq.addEventListener('change', h);
    return () => mq.removeEventListener('change', h);
  }, []);
  return m;
}

// Основной указатель — палец или стилус, наведения нет. Отдельно от ширины: планшет
// бывает шире ноутбука, а узкая панель на десктопе остаётся мышиной. Нужен там, где
// действие показывается по наведению: на таком устройстве состояния hover не бывает
// вовсе, и кнопка просто недостижима.
export const COARSE_QUERY = '(pointer: coarse)';
export function useIsTouch(): boolean {
  const [t, setT] = useState(() =>
    typeof window !== 'undefined' ? window.matchMedia(COARSE_QUERY).matches : false);
  useEffect(() => {
    const mq = window.matchMedia(COARSE_QUERY);
    const h = (e: MediaQueryListEvent) => setT(e.matches);
    setT(mq.matches);
    mq.addEventListener('change', h);
    return () => mq.removeEventListener('change', h);
  }, []);
  return t;
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
