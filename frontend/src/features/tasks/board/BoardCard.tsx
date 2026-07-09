// Карточка задачи на Kanban-доске: sortable-обёртка над общим TaskCard.
// Drag всей карточкой; клик (без перемещения) открывает задачу.

import { useSortable } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import type { Task } from '../../../types';
import { TaskCard } from '../TaskCard';

export function BoardCard({ task, projectName, onOpen }: {
  task: Task;
  projectName?: string;
  onOpen: () => void;
}) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id: task.id });
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
      <TaskCard task={task} projectName={projectName} onClick={onOpen} />
    </div>
  );
}
