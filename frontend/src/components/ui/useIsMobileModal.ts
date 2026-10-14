import { useMediaQuery } from '../../hooks/useMediaQuery';
import { MOBILE_MAX } from '../../lib/breakpoints';

const MOBILE_BP = MOBILE_MAX + 1; // единый порог с раскладкой (см. lib/breakpoints)

// Брейкпоинт мобилы для диалогов (тот же, что в Modal): узкий экран → шторка/вертикальные действия.
// Для кастомных футеров (напр. диалог с тремя исходами).
// Хук вынесен из компонентного файла ModalActions: экспорт хука рядом с компонентом
// ломает fast refresh (см. eslint.config.js, примечание к react-refresh/only-export-components).
export function useIsMobileModal() {
  return useMediaQuery(`(max-width: ${MOBILE_BP - 1}px)`);
}
