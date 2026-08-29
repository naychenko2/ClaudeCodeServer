import { useRef, useState, type PointerEvent as ReactPointerEvent } from 'react';
import { startPointerDrag } from '../lib/pointerDrag';

/**
 * Пропорция двух островов в центре и ресайз между ними (чат | файл, чат | видео).
 *
 * Раскладка одна и та же везде, где центр делится надвое: слева резиновый чат,
 * справа второй остров, между ними IslandSplitter. Пропорция считается из пиксельных
 * ширин, а не из процентов, потому что у обоих островов есть минимум (200px): в
 * процентах на узком окне один из них схлопывался бы в полоску.
 *
 * Не персистится намеренно: ширина половин — сиюминутная настройка под задачу, а вот
 * САМ режим (рядом или во всю ширину) переживает перезапуск, и живёт он там, где
 * принадлежит содержимому (видео — `videoStage`, файл — состояние страницы).
 */
export function useCenterSplit(min = 200) {
  const containerRef = useRef<HTMLDivElement>(null);
  const [flex, setFlex] = useState(1);
  const [dragging, setDragging] = useState(false);

  const handleDrag = (e: ReactPointerEvent) => {
    e.preventDefault();
    const container = containerRef.current;
    if (!container) return;
    const rect = container.getBoundingClientRect();
    setDragging(true);
    startPointerDrag(
      ev => {
        const leftW = Math.max(min, Math.min(rect.width - min, ev.clientX - rect.left));
        setFlex(leftW / (rect.width - leftW));
      },
      { onEnd: () => setDragging(false) },
    );
  };

  return { containerRef, flex, dragging, handleDrag, min };
}
