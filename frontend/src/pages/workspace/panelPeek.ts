import { useCallback, useEffect, useRef, useState } from 'react';
import type { PanelKey } from './panelCatalog';

// Превью панели по наведению на её иконку в рельсе: заглянуть в список чатов или
// изменений, ничего не открывая. Уход курсора убирает попап, клик по булавке
// закрепляет панель в раскладке.
//
// Пауза перед ПОКАЗОМ: курсор часто идёт к иконке, чтобы просто нажать её, и
// попап, выскакивающий мгновенно, каждый раз лез бы под руку. Проведённый мимо
// курсор его вообще не вызывает.
const SHOW_MS = 500;
// Пауза перед СКРЫТИЕМ — не украшение: между иконкой и попапом лежит зазор
// холста, и без неё попап схлопывался бы ровно по дороге к нему. Отсюда же
// hold() — попап под курсором отменяет уже назначенное скрытие.
const HIDE_MS = 160;

export interface PanelPeek {
  // Какую панель показывать попапом (null — никакую)
  key: PanelKey | null;
  // Курсор вошёл на иконку панели — показать после паузы
  show: (k: PanelKey) => void;
  // Курсор ушёл с иконки или с попапа — скрыть с паузой (и отменить показ,
  // если тот ещё не случился)
  hide: () => void;
  // Курсор дошёл до попапа — отменить назначенное скрытие
  hold: () => void;
  // Убрать попап немедленно и отменить назначенный показ (панель закрепили или
  // кликнули по иконке — попапу тут делать нечего)
  clear: () => void;
}

export function usePanelPeek(): PanelPeek {
  const [key, setKey] = useState<PanelKey | null>(null);
  // Один таймер на обе паузы: показ и скрытие взаимоисключающи, а два таймера
  // пришлось бы гасить крест-накрест в каждом обработчике
  const timer = useRef<number | null>(null);

  const stopTimer = useCallback(() => {
    if (timer.current != null) { clearTimeout(timer.current); timer.current = null; }
  }, []);
  const later = useCallback((ms: number, next: PanelKey | null) => {
    stopTimer();
    timer.current = window.setTimeout(() => { timer.current = null; setKey(next); }, ms);
  }, [stopTimer]);

  const show = useCallback((k: PanelKey) => { later(SHOW_MS, k); }, [later]);
  const hide = useCallback(() => { later(HIDE_MS, null); }, [later]);
  const hold = useCallback(() => { stopTimer(); }, [stopTimer]);
  const clear = useCallback(() => { stopTimer(); setKey(null); }, [stopTimer]);

  useEffect(() => stopTimer, [stopTimer]);

  return { key, show, hide, hold, clear };
}
