import { useCallback, useRef, useState } from 'react';

// Высота одиночной панели колонки. Нужна сплиттеру ширины: панель с высотой по
// контенту (короткий список чатов) не достаёт до низа, а сплиттер тянулся на всю
// зону — его grip висел в пустоте посреди холста, далеко от панели, которую он
// двигает. Со знанием высоты сплиттер укорачивается до панели, и grip встаёт
// напротив неё.
//
// Замер живёт в состоянии, а не в CSS-переменной: сплиттер — сосед колонки, а не
// её потомок, и общего DOM-предка у них в этом компоненте нет (зона возвращает
// фрагмент). Перерисовок это почти не стоит: высота панели по контенту меняется
// редко — от ресайза окна она не зависит.
export function useSoloPanelHeight(): [(el: HTMLElement | null) => void, number | null] {
  const [height, setHeight] = useState<number | null>(null);
  const observer = useRef<ResizeObserver | null>(null);

  const ref = useCallback((el: HTMLElement | null) => {
    observer.current?.disconnect();
    observer.current = null;
    // Панель растянулась или уехала — сплиттер снова во всю высоту
    if (!el) { setHeight(null); return; }
    setHeight(el.offsetHeight);
    if (typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(() => setHeight(el.offsetHeight));
    ro.observe(el);
    observer.current = ro;
  }, []);

  return [ref, height];
}
