// Карточка задачи в списках (сайдбар проекта, агенда, список дня в календаре)
//
// Дефекты (волна 2): рядом с флагом приоритета показываем иконку Bug, а в нижней строке
// чипов — плашку «Закрыт без проверки», когда задача закрыта через Outcome=closedWithoutCheck
// (внутренний путь без отдельной Verification). Этим путём обычно снимаются
// дефекты волной командной реализации или авто-правилом заметок.

import type { Task } from '../../types';
import { Bug, Repeat } from 'lucide-react';
import { C, FONT, FS, SHADOW } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
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
  // Дефект: верхний ряд получает Bug-метку; закрытый внутренним путём — доп. плашку
  const isDefect = task.kind === 'defect';
  const closedWithoutCheck = task.outcome === 'closedWithoutCheck';

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
        {/* Флаг приоритета + Bug у дефекта + заголовок + исполнитель */}
        <div style={{ display: 'flex', alignItems: 'flex-start', gap: 7 }}>
          <span style={{ marginTop: 2, display: 'flex' }}><PriorityFlag priority={task.priority} /></span>
          {isDefect && (
            // Жук-метка дефекта. Тон — нейтральный (textMuted), чтобы не путать с
            // цветом приоритета и не спорить с акцентом; подсказка объясняет символ
            <span
              title="Дефект"
              aria-label="Дефект"
              style={{
                marginTop: 2, display: 'inline-flex', alignItems: 'center',
                color: C.textMuted, flexShrink: 0,
              }}
            >
              <Bug size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </span>
          )}
          <span style={{
            flex: 1, minWidth: 0,
            fontFamily: FONT.sans, fontSize: FS.base, fontWeight: 600, lineHeight: 1.35,
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

        {/* Нижняя строка: чипы + доп. плашка «Закрыт без проверки» для дефектов,
            снятых по Outcome=closedWithoutCheck (без отдельного Verification) */}
        {(task.personaId || task.dueDate || closedWithoutCheck || (!compact && (task.subtasks.length > 0 || task.labels.length > 0)) || projectName) && (
          <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', marginTop: 8 }}>
            {task.personaId && <TaskPersonaBadge personaId={task.personaId} />}
            <DueChip task={task} />
            {/* Плашка «Закрыт без проверки»: тот же danger-bg, что у горящего срока,
                приглушённый (только цвет dangerText на прозрачной подложке), чтобы
                не выглядеть как «что-то сломалось» — это просто факт пути */}
            {closedWithoutCheck && (
              <span
                title="Дефект снят внутренним путём — без отдельной проверки"
                style={{
                  display: 'inline-flex', alignItems: 'center',
                  fontFamily: FONT.sans, fontSize: 11, fontWeight: 600,
                  color: C.dangerText,
                  background: C.dangerBg,
                  border: `1px solid ${C.dangerBorder}`,
                  padding: '2px 7px', borderRadius: 6, whiteSpace: 'nowrap',
                }}
              >
                Закрыт без проверки
              </span>
            )}
            {task.recurrence && (
              <Repeat size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0 }} />
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
