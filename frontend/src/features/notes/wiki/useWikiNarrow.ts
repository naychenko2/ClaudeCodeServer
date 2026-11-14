// M0 · «Узко» для панелей вики (макет wiki-panels-v1, принцип П1).
//
// Узко = ширина КОНТЕЙНЕРА, а не окна: та же панель живёт и в боковой зоне воркспейса
// (340–420px), и на весь экран. Узкая раскладка включается, когда контейнер уже MOBILE_MAX
// или когда устройство мобильное (useIsMobile). Новых порогов нет — только MOBILE_MAX.
//
// Тач-размеры (minHeight: 40) остаются на isMobile: узкая панель на десктопе — это всё ещё мышь.

import { type RefCallback } from 'react';
import { MOBILE_MAX, useContainerWidth, useIsMobile } from 'aihome_shell/kit';

// Решение об узкости — чистая функция, вынесена из хука: проверяется тестом без DOM.
// До первого замера (width === null) — не узко: широкая раскладка самый частый случай
// и она же безопасная при отсутствии замера (тот же договор, что у useNarrowContainer).
export function isNarrow(width: number | null, isMobile: boolean): boolean {
  return isMobile || (width !== null && width < MOBILE_MAX);
}

// [ref, narrow]: ref вешается на измеряемый контейнер панели, narrow — признак раскладки.
// Возврат подстроен под сигнатуру useContainerWidth: это callback-ref, не RefObject.
export function useWikiNarrow<T extends HTMLElement>(): [RefCallback<T>, boolean] {
  const [ref, width] = useContainerWidth<T>();
  const isMobile = useIsMobile();
  return [ref, isNarrow(width, isMobile)];
}
