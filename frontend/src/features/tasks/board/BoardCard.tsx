// Карточка задачи на Kanban-доске: sortable-обёртка над общим TaskCard.
// Drag всей карточкой; клик (без перемещения) открывает задачу.
// canDrag=false (читатель общего проекта, F2-Q3): useSortable(disabled) не отдаёт ни
// transform, ни активационных listeners — карточка не едет за курсором вовсе, а не
// возвращается после отпускания; клик внутри TaskCard при этом не тронут.

import { memo, useCallback } from 'react';
import { useSortable } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import type { Task } from '../../../types';
import { TaskCard } from '../TaskCard';

// memo + onOpen, принимающий задачу: onClick для TaskCard строим внутри через useCallback,
// иначе стрелка из BoardCell создавалась бы заново и перерисовывала все карточки колонки
export const BoardCard = memo(function BoardCard({ task, projectName, deviceName, onOpen, canDrag = true }: {
  task: Task;
  projectName?: string;
  deviceName?: string;
  onOpen: (task: Task) => void;
  canDrag?: boolean;
}) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id: task.id, disabled: !canDrag });
  const onClick = useCallback(() => onOpen(task), [onOpen, task]);
  return (
    <div
      ref={setNodeRef}
      style={{
        transform: CSS.Transform.toString(transform),
        transition,
        opacity: isDragging ? 0.4 : 1,
      }}
      {...attributes}
      {...listeners}
    >
      <TaskCard task={task} projectName={projectName} deviceName={deviceName} onClick={onClick} />
    </div>
  );
});
