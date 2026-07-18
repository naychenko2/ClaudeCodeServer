import { useMemo, useState, useEffect } from 'react';
import { SquareCheckBig } from 'lucide-react';
import type { Task } from '../../types';
import { C, FONT } from '../../lib/design';
import {
  ensureTasksLoaded, useTasks, openTaskInSection,
  daysFromToday, dueLabel, todayIso, PRIORITY_COLOR,
} from '../../lib/tasks';
import type { HubTab } from '../../components/HubTabs';
import { NewTaskDialog } from '../tasks/NewTaskDialog';
import { WidgetCard, WidgetAction, WidgetEmpty, MiniSegment } from './WidgetCard';

// Вкладки среза: сегодня / будущие / просроченные
type TaskTab = 'today' | 'soon' | 'overdue';
const SHOWN = 5;

function TaskRow({ task }: { task: Task }) {
  const overdue = task.dueDate ? daysFromToday(task.dueDate) < 0 : false;
  return (
    <button
      onClick={() => openTaskInSection(task)}
      style={{
        display: 'flex', alignItems: 'center', gap: 9, width: '100%', textAlign: 'left',
        background: 'none', border: 'none', borderRadius: 8, padding: '7px 8px',
        margin: '0 -8px', cursor: 'pointer', minWidth: 0,
      }}
      onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
      onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}
    >
      <span style={{
        width: 8, height: 8, borderRadius: '50%', flexShrink: 0,
        background: PRIORITY_COLOR[task.priority] ?? C.textMuted,
      }} />
      <span style={{
        fontFamily: FONT.sans, fontSize: 13, color: C.textPrimary, flex: 1, minWidth: 0,
        whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
      }}>
        {task.title}
      </span>
      {task.dueDate && (
        <span style={{
          fontFamily: FONT.sans, fontSize: 11.5, flexShrink: 0,
          color: overdue ? C.dangerText : C.textMuted,
        }}>
          {dueLabel(task.dueDate)}
        </span>
      )}
    </button>
  );
}

// «Задачи»: срезы Сегодня / Скоро / Просрочено со счетчиками во вкладках-тогле.
// Компактно: одна вкладка видна за раз, до 5 задач. Данные — глобальный стор tasks.ts.
export function TasksWidget({ onHubTab }: { onHubTab: (t: HubTab) => void }) {
  const tasks = useTasks();
  useEffect(() => { void ensureTasksLoaded(); }, []);
  const [tab, setTab] = useState<TaskTab>('today');
  const [newTaskOpen, setNewTaskOpen] = useState(false);

  const { today, soon, overdue } = useMemo(() => {
    const iso = todayIso();
    const open = tasks.filter(t => t.status !== 'done' && t.dueDate);
    return {
      today: open.filter(t => t.dueDate === iso),
      // Будущие — ближайшие первыми
      soon: open.filter(t => t.dueDate! > iso).sort((a, b) => a.dueDate!.localeCompare(b.dueDate!)),
      // Просроченные — свежепросроченные первыми
      overdue: open.filter(t => t.dueDate! < iso).sort((a, b) => b.dueDate!.localeCompare(a.dueDate!)),
    };
  }, [tasks]);

  const lists: Record<TaskTab, Task[]> = { today, soon, overdue };
  const emptyText: Record<TaskTab, string> = {
    today: 'На сегодня задач нет — можно выдохнуть.',
    soon: 'Ближайших задач со сроком нет.',
    overdue: 'Просроченных нет — красавчик.',
  };
  const shown = lists[tab].slice(0, SHOWN);

  return (
    <WidgetCard
      icon={<SquareCheckBig size={16} strokeWidth={2} />}
      title="Задачи"
      onCreate={() => setNewTaskOpen(true)}
      createTitle="Новая задача"
      action={
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 10 }}>
          <MiniSegment<TaskTab>
            value={tab}
            onChange={setTab}
            options={[
              { value: 'today', label: `Сегодня ${today.length}`, title: 'Срок — сегодня' },
              { value: 'soon', label: `Скоро ${soon.length}`, title: 'Будущие сроки' },
              {
                value: 'overdue', label: `Просрочено ${overdue.length}`, title: 'Срок прошел',
                dot: overdue.length > 0 ? C.danger : undefined,
              },
            ]}
          />
          <WidgetAction label="Все →" onClick={() => onHubTab('calendar')} />
        </span>
      }
    >
      {shown.length === 0
        ? <WidgetEmpty text={emptyText[tab]} />
        : (
          <div style={{ display: 'flex', flexDirection: 'column' }}>
            {shown.map(t => <TaskRow key={t.id} task={t} />)}
          </div>
        )}
      {newTaskOpen && (
        <NewTaskDialog
          onCreated={(task, configure) => {
            setNewTaskOpen(false);
            // Список обновится сам по realtime task_changed; «настроить» — открыть задачу
            if (configure) openTaskInSection(task);
          }}
          onClose={() => setNewTaskOpen(false)}
        />
      )}
    </WidgetCard>
  );
}
