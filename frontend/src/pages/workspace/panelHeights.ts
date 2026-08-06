import { useCallback, useRef, useState } from 'react';

// Замер высоты панелей, стоящих ПО КОНТЕНТУ. Нужен сплиттеру ширины: колонка из
// таких панелей (короткий список чатов) не достаёт до низа, а сплиттер тянулся на
// всю зону — его grip висел в пустоте посреди холста, далеко от колонки, которую
// он двигает. Со знанием высот сплиттер укорачивается до колонки, и grip встаёт
// напротив неё.
//
// Меряется КАЖДАЯ панель по контенту, а не одна одиночная: в ряду у центра таких
// панелей может быть несколько (высоту они не делят), и длина сплиттера — их сумма
// плюс зазоры.
//
// Замер живёт в состоянии, а не в CSS-переменной: сплиттер — сосед колонки, а не
// её потомок, и общего DOM-предка у них в этом компоненте нет (зона возвращает
// фрагмент). Перерисовок это почти не стоит: высота панели по контенту меняется
// редко — от ресайза окна она не зависит.
export function usePanelHeights<K extends string>(): [
  // Стабильная ref-фабрика: ref одной и той же панели не пересоздаётся между
  // рендерами, иначе React на каждый рендер отцеплял бы ResizeObserver и цеплял
  // заново
  (k: K) => (el: HTMLElement | null) => void,
  Partial<Record<K, number>>,
  // Живой замер панели по ключу (null — панель не по контенту либо ещё не в DOM).
  // Нужен там, где решение принимается ПО СОБЫТИЮ, а не по рендеру: состояние
  // heights обновляется только когда ResizeObserver донесёт изменение, и на момент
  // клика может отставать от того, что на экране.
  (k: K) => number | null,
] {
  const [heights, setHeights] = useState<Partial<Record<K, number>>>({});
  const observers = useRef(new Map<K, ResizeObserver>());
  const nodes = useRef(new Map<K, HTMLElement>());
  const refs = useRef(new Map<K, (el: HTMLElement | null) => void>());

  const refFor = useCallback((k: K) => {
    const known = refs.current.get(k);
    if (known) return known;
    const measure = (el: HTMLElement) => setHeights(cur => (
      cur[k] === el.offsetHeight ? cur : { ...cur, [k]: el.offsetHeight }
    ));
    const ref = (el: HTMLElement | null) => {
      observers.current.get(k)?.disconnect();
      observers.current.delete(k);
      if (el) nodes.current.set(k, el); else nodes.current.delete(k);
      // Панель растянулась или уехала — забываем замер (сплиттер снова во всю высоту)
      if (!el) {
        setHeights(cur => {
          if (cur[k] == null) return cur;
          const next = { ...cur };
          delete next[k];
          return next;
        });
        return;
      }
      measure(el);
      if (typeof ResizeObserver === 'undefined') return;
      const ro = new ResizeObserver(() => measure(el));
      ro.observe(el);
      observers.current.set(k, ro);
    };
    refs.current.set(k, ref);
    return ref;
  }, []);

  const heightOf = useCallback((k: K) => nodes.current.get(k)?.offsetHeight ?? null, []);

  return [refFor, heights, heightOf];
}
