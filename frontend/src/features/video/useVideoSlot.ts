import { useEffect, useRef } from 'react';
import { setVideoSlot, type SlotBox, type VideoSlotKind } from '../../lib/videoStage';

/**
 * Отдать место под кадр: панель и центральный остров рисуют ПУСТОЙ прямоугольник,
 * а живой iframe кладёт поверх один общий оверлей (VideoStageFrame в App).
 *
 * Так эфир переживает переход между проектами: страница перемонтируется вместе с
 * панелью и островом, но кадр в App этого не замечает. Раньше он умирал вместе со
 * страницей — «переключил проект, и передача началась заново».
 *
 * Геометрию меряем в петле requestAnimationFrame, а не одним ResizeObserver:
 * прямоугольник в координатах ВЬЮПОРТА едет не только от размера самой панели, но
 * и от чужих движений — открытия соседней панели, ресайза колонок, прокрутки,
 * анимации появления. Наблюдателей на все эти источники не напасёшься, а чтение
 * getBoundingClientRect у двух узлов на кадр дёшево. Равные значения стор не
 * публикует, поэтому лишних перерисовок петля не даёт.
 */
export function useVideoSlot<F extends HTMLElement = HTMLDivElement, C extends HTMLElement = HTMLDivElement>(
  kind: VideoSlotKind,
  active: boolean,
) {
  // Куда встаёт кадр
  const frameRef = useRef<F | null>(null);
  // Чем его обрезать: у короткой панели кадр вылезает за её тело, а fixed-оверлей
  // без клипа лёг бы поверх соседей. Не задан — режем по самому кадру.
  const clipRef = useRef<C | null>(null);

  useEffect(() => {
    if (!active) {
      setVideoSlot(kind, null);
      return;
    }
    let raf = 0;
    const tick = () => {
      raf = requestAnimationFrame(tick);
      const frameEl = frameRef.current;
      const clipEl = clipRef.current ?? frameEl;
      if (!frameEl || !clipEl) { setVideoSlot(kind, null); return; }
      const f = box(frameEl.getBoundingClientRect());
      const c = box(clipEl.getBoundingClientRect());
      // Нулевой размер — место есть в разметке, но его не видно (панель схлопнута,
      // ушла в drawer, скрыта родителем). Кадру там делать нечего.
      if (f.w <= 0 || f.h <= 0 || c.w <= 0 || c.h <= 0) { setVideoSlot(kind, null); return; }
      setVideoSlot(kind, { frame: f, clip: c });
    };
    raf = requestAnimationFrame(tick);
    return () => {
      cancelAnimationFrame(raf);
      setVideoSlot(kind, null);
    };
  }, [kind, active]);

  return { frameRef, clipRef };
}

// Округляем до пикселя: дробное дрожание субпиксельной раскладки иначе
// публиковалось бы как «геометрия изменилась» на каждом кадре петли
function box(r: DOMRect): SlotBox {
  return { x: Math.round(r.left), y: Math.round(r.top), w: Math.round(r.width), h: Math.round(r.height) };
}
