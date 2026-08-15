import { useCallback, useState, type RefCallback } from 'react';

// Раскладка по ширине КОНТЕЙНЕРА, а не окна: одна и та же панель живёт и в модалке,
// и в боковой зоне воркспейса, и на весь экран — isMobile/медиазапрос про её реальную
// ширину ничего не знают.
//
// Возвращает callback-ref (повесить на измеряемый элемент) и текущую ширину: null —
// замера ещё не было. Замер идёт в момент появления узла (фаза коммита, до кадра) —
// раскладка не мигает.

// Замер узла и подписка на его ресайз; возвращает отцепку. Вынесено из хука отдельной
// функцией, чтобы поведение проверялось тестом без DOM.
export function observeWidth(el: HTMLElement, onWidth: (width: number) => void): () => void {
  onWidth(el.clientWidth);
  if (typeof ResizeObserver === 'undefined') return () => {};   // jsdom/vitest — живём без наблюдателя
  const ro = new ResizeObserver(() => onWidth(el.clientWidth));
  ro.observe(el);
  return () => ro.disconnect();
}

export function useContainerWidth<T extends HTMLElement>(): [RefCallback<T>, number | null] {
  const [width, setWidth] = useState<number | null>(null);

  // Именно callback-ref, а не useLayoutEffect с пустыми зависимостями: узел может
  // появиться ПОЗЖЕ монтирования (панель ушла с мобильной ветки на десктопную — там
  // ref'нутого элемента в DOM не было), и замер обязан отработать в этот момент.
  const ref = useCallback<RefCallback<T>>(node => {
    if (!node) return;   // React 19 зовёт с null только там, где отцепку не вернули
    return observeWidth(node, setWidth);
  }, []);

  return [ref, width];
}

// Тот же замер, но сразу ответом «контейнер уже порога». До первого замера — false
// (широкая раскладка): она же и на широком экране, самый частый случай.
export function useNarrowContainer<T extends HTMLElement>(threshold: number): [RefCallback<T>, boolean] {
  const [ref, width] = useContainerWidth<T>();
  return [ref, width !== null && width < threshold];
}
