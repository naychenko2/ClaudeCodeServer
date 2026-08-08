// Долгое нажатие по строке списка — замена правого клика и наведения на тач-раскладке.
// На устройстве без мыши состояния hover не бывает вовсе, поэтому действия, которые
// показываются под курсором, там попросту недостижимы; удержание пальца открывает их.
//
// Хук — на СПИСОК, а не на строку: строки списка рисуются в цикле, и вызвать хук на
// каждую нельзя. Ключом строки служит любая её уникальная строка (путь, id).
//
// Порог 500мс и короткая вибрация — как в дереве «Файлов», откуда жест и пришёл.

import { useCallback, useEffect, useRef, useState } from 'react';

export const LONG_PRESS_MS = 500;

export interface LongPressPoint { x: number; y: number }

export function useLongPress(enabled: boolean, ms: number = LONG_PRESS_MS) {
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const [pressingKey, setPressingKey] = useState<string | null>(null);

  const cancel = useCallback(() => {
    if (timer.current) { clearTimeout(timer.current); timer.current = null; }
    setPressingKey(null);
  }, []);

  // Размонтирование списка не должно оставлять живой таймер: он выстрелит по строке,
  // которой уже нет
  useEffect(() => cancel, [cancel]);

  /** Обработчики касания для строки. На мыши — пустой объект, ничего не навешиваем. */
  const pressProps = useCallback((key: string, onLong: (point: LongPressPoint) => void) => {
    if (!enabled) return {};
    return {
      onTouchStart: (e: React.TouchEvent) => {
        const t = e.touches[0];
        const point = { x: t?.clientX ?? 0, y: t?.clientY ?? 0 };
        if (timer.current) clearTimeout(timer.current);
        setPressingKey(key);
        timer.current = setTimeout(() => {
          timer.current = null;
          navigator.vibrate?.(10);
          setPressingKey(null);
          onLong(point);
        }, ms);
      },
      // Удержание уже отработало → отпускание пальца не должно открывать строку
      onTouchEnd: (e: React.TouchEvent) => { if (!timer.current) e.preventDefault(); cancel(); },
      onTouchMove: cancel,
      onTouchCancel: cancel,
    };
  }, [enabled, ms, cancel]);

  return { pressingKey, pressProps };
}
