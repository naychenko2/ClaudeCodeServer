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

  // Сторож на случай, когда onMouseLeave не пришёл вовсе: кнопку перестроили или
  // убрали прямо под курсором, и подсказка залипала на экране. Сторож только
  // ГАСИТ — ставить ключ он не вправе: рельс на экране две, и по метке чужой
  // кнопки зона нарисовала бы место для панели, на которую наводят у соседа.
  // Переход на соседнюю иконку своей рельсы гашения не вызывает: её onMouseEnter
  // приходит раньше mousemove и ключ к этому моменту уже новый.
  useEffect(() => {
    if (key == null) return;
    const onMove = (e: MouseEvent) => {
      const el = (e.target as Element | null)?.closest?.('[data-rail-item]');
      if (el?.getAttribute('data-rail-item') === key) { stop(); return; }
      leave();
    };
    // Окно потеряло фокус (Alt+Tab, переход в другое приложение) — курсор больше
    // не наш, держать подсказку не за что
    const onBlur = () => clear();
    document.addEventListener('mousemove', onMove);
    window.addEventListener('blur', onBlur);
    return () => {
      document.removeEventListener('mousemove', onMove);
      window.removeEventListener('blur', onBlur);
    };
  }, [key, stop, leave, clear]);

  useEffect(() => stop, [stop]);

  return { key, enter, leave, clear };
}
