// Хук богатого hover-тултипа задачи (десктоп).
// Использование: const hover = useTaskHover();
//   <div {...hover.bind(task, projectName)}>…</div>  +  {hover.popover}
// Хук вынесен из компонентного TaskHoverCard.tsx: экспорт хука рядом с компонентом
// ломает fast refresh (см. eslint.config.js, примечание к react-refresh/only-export-components).
import { useEffect, useRef, useState } from 'react';
import type { Task } from '../../types';
import { HoverCard } from './TaskHoverCard';
import type { TaskHoverAnchor } from './TaskHoverCard';

const OPEN_DELAY = 400;
const CLOSE_DELAY = 180;

// bind(task, projectName?) → onMouseEnter/onMouseLeave для якоря; popover — рендерить рядом
export function useTaskHover() {
  const [anchor, setAnchor] = useState<TaskHoverAnchor | null>(null);
  const openTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const closeTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => () => {
    if (openTimer.current) clearTimeout(openTimer.current);
    if (closeTimer.current) clearTimeout(closeTimer.current);
  }, []);

  const cancelClose = () => {
    if (closeTimer.current) { clearTimeout(closeTimer.current); closeTimer.current = null; }
  };

  const scheduleClose = () => {
    cancelClose();
    closeTimer.current = setTimeout(() => setAnchor(null), CLOSE_DELAY);
  };

  const bind = (task: Task, projectName?: string) => ({
    onMouseEnter: (e: React.MouseEvent<HTMLElement>) => {
      // Только устройства с настоящим hover (не тач)
      if (!window.matchMedia('(hover: hover)').matches) return;
      cancelClose();
      const rect = e.currentTarget.getBoundingClientRect();
      if (openTimer.current) clearTimeout(openTimer.current);
      openTimer.current = setTimeout(() => setAnchor({ taskId: task.id, rect, projectName }), OPEN_DELAY);
    },
    onMouseLeave: () => {
      if (openTimer.current) { clearTimeout(openTimer.current); openTimer.current = null; }
      scheduleClose();
    },
  });

  const popover = anchor ? (
    <HoverCard
      anchor={anchor}
      onKeepAlive={cancelClose}
      onLeave={scheduleClose}
      onClose={() => setAnchor(null)}
    />
  ) : null;

  return { bind, popover };
}
