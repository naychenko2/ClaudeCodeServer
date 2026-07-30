import { useCallback, useEffect, useRef, useState } from 'react';
import type { PanelKey } from './panelCatalog';

// Иконка рельсы под курсором — по ней зона показывает место будущей панели.
//
// Появление мгновенное: курсор дошёл до иконки намеренно. А вот ГАШЕНИЕ с паузой,
// потому что между иконками есть зазоры: ведя мышь вдоль рельсы, курсор то и дело
// оказывается «между», и без паузы призрак мигал бы на каждом миллиметре. Переход
// на соседнюю иконку паузу отменяет — место просто переезжает.
const HIDE_MS = 140;

export interface RailHover {
  key: PanelKey | null;
  // Курсор вошёл на иконку — показать её место немедленно
  enter: (k: PanelKey) => void;
  // Курсор ушёл с иконки — погасить, но не сразу
  leave: () => void;
  // Убрать немедленно (клик, начало перетаскивания)
  clear: () => void;
}

export function useRailHover(): RailHover {
  const [key, setKey] = useState<PanelKey | null>(null);
  const timer = useRef<number | null>(null);

  const stop = useCallback(() => {
    if (timer.current != null) { clearTimeout(timer.current); timer.current = null; }
  }, []);

  const enter = useCallback((k: PanelKey) => { stop(); setKey(k); }, [stop]);
  const leave = useCallback(() => {
    stop();
    timer.current = window.setTimeout(() => { timer.current = null; setKey(null); }, HIDE_MS);
  }, [stop]);
  const clear = useCallback(() => { stop(); setKey(null); }, [stop]);

  useEffect(() => stop, [stop]);

  return { key, enter, leave, clear };
}
