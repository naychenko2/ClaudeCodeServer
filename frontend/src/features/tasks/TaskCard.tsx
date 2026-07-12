// Карточка задачи в списках (сайдбар проекта, агенда, список дня в календаре)

import type { Task } from '../../types';
import { C, FONT, SHADOW } from '../../lib/design';
import { projectColor } from '../../lib/tasks';
import { AssigneeBadge, DueChip, LabelChip, PriorityFlag, SubtaskCheck } from './bits';
import { TaskPersonaBadge } from './TaskPersonaBadge';

interface Props {
  task: Task;
  selected?: boolean;
  onClick: () => void;
  // compact — узкий сайдбар планшета/десктопа: только чип срока в нижней строке
  compact?: boolean;
  // Имя проекта — показывается в кросс-проектных контекстах (календарь)
  projectName?: string;
}

export function TaskCard({ task, selected, onClick, compact, projectName }: Props) {
  const color = projectColor(task.projectId);
  const done = task.status === 'done';
  const doneSubs = task.subtasks.filter(s => s.isDone).length;

  return (
    <div
      onClick={onClick}
      style={{
        display: 'flex', gap: 10,
        background: C.bgWhite,
        border: `1px solid ${selected ? C.accent : C.borderLight}`,
        boxShadow: selected ? `0 0 0 1px ${C.accent}` : SHADOW.card,
        borderRadius: 12,
        padding: '11px 12px',
        cursor: 'pointer',
        transition: 'border-color 0.12s, box-shadow 0.12s',
      }}
    >
      {/* Цветная полоса проекта слева */}
      <div style={{ width: 3, borderRadius: 2, background: color.main, flexShrink: 0, alignSelf: 'stretch' }} />

      <div style={{ flex: 1, minWidth: 0 }}>
        {/* Флаг приоритета + заголовок + исполнитель */}
        <div style={{ display: 'flex', alignItems: 'flex-start', gap: 7 }}>
          <span style={{ marginTop: 2, display: 'flex' }}><PriorityFlag priority={task.priority} /></span>
          <span style={{
            flex: 1, minWidth: 0,
            fontFamily: FONT.sans, fontSize: 13.5, fontWeight: 600, lineHeight: 1.35,
            color: done ? C.textMuted : C.textPrimary,
            textDecoration: done ? 'line-through' : 'none',
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>
            {task.title}
          </span>
          {/* Значок Claude/Я — только когда исполнитель не персона (персона
              переезжает в нижнюю строку чипов, чтобы не теснить заголовок) */}
          {!task.personaId && (
            <AssigneeBadge assignee={compact && task.assignee === 'me' ? undefined : task.assignee} />
          )}
        </div>

        {/* Нижняя строка: чипы */}
        {(task.personaId || task.dueDate || (!compact && (task.subtasks.length > 0 || task.labels.length > 0)) || projectName) && (
          <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', marginTop: 8 }}>
            {task.personaId && <TaskPersonaBadge personaId={task.personaId} />}
            <DueChip task={task} />
            {task.recurrence && (
              <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke={C.textMuted} strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
                <path d="M17 1l4 4-4 4" />
                <path d="M3 11V9a4 4 0 0 1 4-4h14" />
                <path d="M7 23l-4-4 4-4" />
                <path d="M21 13v2a4 4 0 0 1-4 4H3" />
              </svg>
            )}
            {!compact && task.subtasks.length > 0 && (
              <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
                <SubtaskCheck done={false} size={12} />
                <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted }}>
                  {doneSubs}/{task.subtasks.length}
                </span>
              </span>
            )}
            {!compact && task.labels.map(l => <LabelChip key={l} label={l} />)}
            {projectName && (
              <span style={{ display: 'inline-flex', alignItems: 'center', gap: 5, fontFamily: FONT.sans, fontSize: 11, fontWeight: 600, color: C.textSecondary }}>
                <span style={{ width: 7, height: 7, borderRadius: '50%', background: color.main, flexShrink: 0 }} />
                {projectName}
              </span>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
