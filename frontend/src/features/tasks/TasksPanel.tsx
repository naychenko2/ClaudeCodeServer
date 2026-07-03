// Список задач проекта в левой панели воркспейса.
// Подвкладки «Список» (группировка по статусу) и «По дате» (готовые скрыты).

import { useEffect, useMemo, useState } from 'react';
import type { Project, Task, TaskStatus } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { PillSwitch } from '../../components/Toolbar';
import {
  STATUS_DOT, STATUS_LABEL, daysFromToday, ensureTasksLoaded, useTasks,
} from '../../lib/tasks';
import { TaskCard } from './TaskCard';
import { NewTaskDialog } from './NewTaskDialog';
import { ByDateIcon, IconViewSwitcher, ListIcon } from './bits';

interface Props {
  project: Project;
  selectedTaskId: string | null;
  onSelect: (task: Task) => void;
  isMobile?: boolean;
}

type GroupTab = 'status' | 'date';

interface Group { key: string; label: string; dot?: string; tasks: Task[] }

const STATUS_GROUP_ORDER: TaskStatus[] = ['inProgress', 'todo', 'done'];

function groupByStatus(tasks: Task[]): Group[] {
  return STATUS_GROUP_ORDER
    .map(s => ({
      key: s,
      label: STATUS_LABEL[s],
      dot: STATUS_DOT[s],
      tasks: tasks.filter(t => t.status === s),
    }))
    .filter(g => g.tasks.length > 0);
}

function dateGroupKey(t: Task): string {
  if (!t.dueDate) return 'none';
  const diff = daysFromToday(t.dueDate);
  if (diff < 0) return 'overdue';
  if (diff === 0) return 'today';
  if (diff < 7) return 'week';
  return 'later';
}

const DATE_GROUPS: { key: string; label: string; dot?: string }[] = [
  { key: 'overdue', label: 'Просрочено', dot: '#B4452F' },
  { key: 'today',   label: 'Сегодня',    dot: '#D97757' },
  { key: 'week',    label: 'Эта неделя', dot: '#C9923E' },
  { key: 'later',   label: 'Позже',      dot: '#9A8F7E' },
  { key: 'none',    label: 'Без срока',  dot: '#9A8F7E' },
];

// «По дате»: готовые задачи скрыты (как в макете)
function groupByDate(tasks: Task[]): Group[] {
  const active = tasks.filter(t => t.status !== 'done');
  return DATE_GROUPS
    .map(g => ({ ...g, tasks: active.filter(t => dateGroupKey(t) === g.key) }))
    .filter(g => g.tasks.length > 0);
}

export function TasksPanel({ project, selectedTaskId, onSelect, isMobile }: Props) {
  const allTasks = useTasks();
  const [loading, setLoading] = useState(true);
  const [groupTab, setGroupTab] = useState<GroupTab>('status');
  const [showCreate, setShowCreate] = useState(false);

  useEffect(() => {
    let alive = true;
    ensureTasksLoaded().finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; };
  }, []);

  const tasks = useMemo(
    () => allTasks.filter(t => t.projectId === project.id),
    [allTasks, project.id],
  );

  const groups = groupTab === 'status' ? groupByStatus(tasks) : groupByDate(tasks);

  const newTaskButton = (
    <button
      onClick={() => setShowCreate(true)}
      style={{
        width: '100%', boxSizing: 'border-box',
        padding: '11px 14px', marginTop: 4,
        border: `1.5px dashed ${C.dashed}`, borderRadius: R.xl,
        background: 'transparent', color: C.accent,
        fontFamily: FONT.sans, fontSize: 13.5, fontWeight: 600, cursor: 'pointer',
        display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 7,
      }}
    >
      <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round">
        <line x1="12" y1="5" x2="12" y2="19" /><line x1="5" y1="12" x2="19" y2="12" />
      </svg>
      Новая задача
    </button>
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      {/* Подвкладки «Список | По дате»: на мобиле — сегменты с иконкой сверху
          (как Месяц/Неделя/Агенда в календаре), на десктопе — pill-переключатель */}
      <div style={{ padding: isMobile ? '10px 14px 4px' : '0 16px 4px', flexShrink: 0 }}>
        {isMobile ? (
          <IconViewSwitcher<GroupTab>
            value={groupTab}
            options={[
              { value: 'status', label: 'Список', icon: <ListIcon size={16} /> },
              { value: 'date', label: 'По дате', icon: <ByDateIcon size={16} /> },
            ]}
            onChange={setGroupTab}
          />
        ) : (
          <PillSwitch<GroupTab>
            value={groupTab}
            options={[
              { value: 'status', label: 'Список', icon: <ListIcon /> },
              { value: 'date', label: 'По дате', icon: <ByDateIcon /> },
            ]}
            onChange={setGroupTab}
            fill
          />
        )}
      </div>

      {/* Список */}
      <div style={{ flex: 1, overflowY: 'auto', padding: isMobile ? '8px 14px 16px' : '8px 12px 16px' }}>
        {loading ? (
          <div style={{ padding: 24, textAlign: 'center', color: C.textMuted, fontFamily: FONT.sans, fontSize: 13 }}>
            Загрузка…
          </div>
        ) : tasks.length === 0 ? (
          <div style={{ padding: '28px 8px 8px', textAlign: 'center' }}>
            <div style={{ fontFamily: FONT.sans, fontSize: 13.5, color: C.textMuted, marginBottom: 14, lineHeight: 1.5 }}>
              В проекте пока нет задач
            </div>
            {newTaskButton}
          </div>
        ) : (
          <>
            {groups.map(group => (
              <div key={group.key} style={{ marginBottom: 10 }}>
                {/* Заголовок группы: точка + название + счётчик */}
                <div style={{ display: 'flex', alignItems: 'center', gap: 6, padding: '6px 4px 7px' }}>
                  {group.dot && <span style={{ width: 7, height: 7, borderRadius: '50%', background: group.dot, flexShrink: 0 }} />}
                  <span style={{
                    fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textSecondary,
                    textTransform: 'uppercase', letterSpacing: '0.06em',
                  }}>
                    {group.label}
                  </span>
                  <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted }}>{group.tasks.length}</span>
                </div>
                <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
                  {group.tasks.map(task => (
                    <TaskCard
                      key={task.id}
                      task={task}
                      selected={task.id === selectedTaskId}
                      onClick={() => onSelect(task)}
                      compact={!isMobile}
                    />
                  ))}
                </div>
              </div>
            ))}
            {newTaskButton}
          </>
        )}
      </div>

      {showCreate && (
        <NewTaskDialog
          defaultProjectId={project.id}
          onCreated={task => { setShowCreate(false); onSelect(task); }}
          onClose={() => setShowCreate(false)}
        />
      )}
    </div>
  );
}
