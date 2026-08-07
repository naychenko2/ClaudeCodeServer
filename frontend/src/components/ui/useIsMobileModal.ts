import { useEffect, useState } from 'react';
import { MOBILE_MAX } from '../../lib/breakpoints';

const MOBILE_BP = MOBILE_MAX + 1; // единый порог с раскладкой (см. lib/breakpoints)

// Брейкпоинт мобилы для диалогов (тот же, что в Modal): узкий экран → шторка/вертикальные действия.
// Для кастомных футеров (напр. диалог с тремя исходами).
// Хук вынесен из компонентного файла ModalActions: экспорт хука рядом с компонентом
// ломает fast refresh (см. eslint.config.js, примечание к react-refresh/only-export-components).
export function useIsMobileModal() {
  const [mobile, setMobile] = useState(() =>
    typeof window !== 'undefined'
      ? window.matchMedia(`(max-width: ${MOBILE_BP - 1}px)`).matches
      : false
  );
  useEffect(() => {
    const mq = window.matchMedia(`(max-width: ${MOBILE_BP - 1}px)`);
    const handler = (e: MediaQueryListEvent) => setMobile(e.matches);
    // eslint-disable-next-line react-hooks/set-state-in-effect -- подписка matchMedia: мобильный брейкпоинт
    setMobile(mq.matches);
    if (mq.addEventListener) mq.addEventListener('change', handler);
    else mq.addListener(handler);
    return () => {
      if (mq.removeEventListener) mq.removeEventListener('change', handler);
      else mq.removeListener(handler);
    };
  }, []);
  return mobile;
}
