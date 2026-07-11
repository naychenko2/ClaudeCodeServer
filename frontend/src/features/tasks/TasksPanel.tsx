// Список задач проекта в левой панели воркспейса.
// Подвкладки «Список» (группировка по статусу) и «По дате» (готовые скрыты).

import { useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import type { Project, Task, TaskStatus } from '../../types';
import { C, FONT, R } from '../../lib/design';
import {
  STATUS_DOT, STATUS_LABEL, daysFromToday, ensureTasksLoaded, useTasks,
} from '../../lib/tasks';
import type { BoardGroupBy } from '../../lib/tasks';
import { TaskCard } from './TaskCard';
import { NewTaskDialog } from './NewTaskDialog';
import { BoardToolbar } from './board/BoardToolbar';
import { BoardIcon, ByDateIcon, IconViewSwitcher, ListIcon } from './bits';

// Группировки доски внутри проекта (без «по проекту»)
const PROJECT_GROUP_OPTIONS: BoardGroupBy[] = ['none', 'priority', 'assignee', 'due'];

interface Props {
  project: Project;
  selectedTaskId: string | null;
  // autoEdit — открыть карточку сразу в редактировании (свежесозданная задача)
  onSelect: (task: Task, autoEdit?: boolean) => void;
  isMobile?: boolean;
  // Режим доски: доска рендерится в основной области воркспейса (за флагом task-board)
  boardMode?: boolean;
  onBoardMode?: (on: boolean) => void;
  onEditColumns?: () => void;   // открыть редактор колонок (десктоп-тулбар в сайдбаре)
}

type GroupTab = 'status' | 'date';
type PanelTab = GroupTab | 'board';

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
  { key: 'overdue', label: 'Просрочено', dot: C.danger },
  { key: 'today',   label: 'Сегодня',    dot: C.accent },
  { key: 'week',    label: 'Эта неделя', dot: C.warning },
  { key: 'later',   label: 'Позже',      dot: C.textMuted },
  { key: 'none',    label: 'Без срока',  dot: C.textMuted },
];

// «По дате»: готовые задачи скрыты (как в макете)
function groupByDate(tasks: Task[]): Group[] {
  const active = tasks.filter(t => t.status !== 'done');
  return DATE_GROUPS
    .map(g => ({ ...g, tasks: active.filter(t => dateGroupKey(t) === g.key) }))
    .filter(g => g.tasks.length > 0);
}

export function TasksPanel({ project, selectedTaskId, onSelect, isMobile, boardMode, onBoardMode, onEditColumns }: Props) {
  const allTasks = useTasks();
  const [loading, setLoading] = useState(true);
  const [groupTab, setGroupTab] = useState<GroupTab>('status');
  const [showCreate, setShowCreate] = useState(false);

  // Значение переключателя: доска или одна из группировок списка
  const panelTab: PanelTab = boardMode ? 'board' : groupTab;
  const onPanelTab = (v: PanelTab) => {
    if (v === 'board') { onBoardMode?.(true); return; }
    onBoardMode?.(false);
    setGroupTab(v);
  };
  const tabOptions = (base: { value: PanelTab; label: string; icon: ReactNode }[]) => base;

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

  return (
    // flex:1 + minHeight:0 — шапка (переключатель и кнопка) закреплена, скроллится только список:
    // процентная высота во вложенных flex-колонках может резолвиться в auto, и тогда ехал весь блок
    <div style={{ display: 'flex', flexDirection: 'column', flex: '1 1 auto', minHeight: 0, height: '100%', overflow: 'hidden' }}>
      {/* Подвкладки «Список | По дате | Доска» (доска — за флагом): единый стиль сегментов
          с иконкой сверху (как в календаре) — и на мобиле, и на десктопе */}
      <div style={{ padding: isMobile ? '10px 14px 4px' : '0 16px 4px', flexShrink: 0 }}>
        <IconViewSwitcher<PanelTab>
          value={panelTab}
          options={tabOptions([
            { value: 'status', label: 'Список', icon: <ListIcon size={16} /> },
            { value: 'date', label: 'По дате', icon: <ByDateIcon size={16} /> },
            { value: 'board', label: 'Доска', icon: <BoardIcon size={16} /> },
          ])}
          onChange={onPanelTab}
        />
      </div>

      {/* Кнопка создания — закреплена сверху, не уползает при длинном списке */}
      <div style={{ padding: isMobile ? '8px 14px 4px' : '8px 12px 4px', flexShrink: 0 }}>
        <button
          onClick={() => setShowCreate(true)}
          style={{
            width: '100%', boxSizing: 'border-box',
            padding: '11px 14px',
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
      </div>

      {/* Список (в режиме доски скрыт — доска рендерится в основной области) */}
      <div style={{ flex: 1, overflowY: 'auto', padding: isMobile ? '4px 14px 16px' : '4px 12px 16px' }}>
        {boardMode ? (
          isMobile ? (
            <div style={{ padding: '28px 8px 8px', textAlign: 'center', fontFamily: FONT.sans, fontSize: 13, color: C.textMuted, lineHeight: 1.5 }}>
              Доска задач открыта.<br />Перетаскивайте карточки между колонками.
              <button
                onClick={() => onBoardMode?.(true)}
                style={{
                  display: 'inline-flex', alignItems: 'center', gap: 6, marginTop: 16, padding: '9px 16px', cursor: 'pointer',
                  border: 'none', borderRadius: R.lg, background: C.accent, color: C.onAccent,
                  fontFamily: FONT.sans, fontSize: 13.5, fontWeight: 700,
                }}
              >
                Открыть доску
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round"><path d="M9 6l6 6-6 6" /></svg>
              </button>
            </div>
          ) : (
            // Десктоп: управление доской (группировка/фильтры/поиск/колонки) — в сайдбаре
            <div style={{ padding: '10px 2px' }}>
              <BoardToolbar layout="sidebar" groupOptions={PROJECT_GROUP_OPTIONS} onEditColumns={onEditColumns} />
            </div>
          )
        ) : loading ? (
          <div style={{ padding: 24, textAlign: 'center', color: C.textMuted, fontFamily: FONT.sans, fontSize: 13 }}>
            Загрузка…
          </div>
        ) : tasks.length === 0 ? (
          <div style={{ padding: '28px 8px 8px', textAlign: 'center' }}>
            <div style={{ fontFamily: FONT.sans, fontSize: 13.5, color: C.textMuted, lineHeight: 1.5 }}>
              В проекте пока нет задач
            </div>
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
          </>
        )}
      </div>

      {showCreate && (
        <NewTaskDialog
          defaultProjectId={project.id}
          onCreated={(task, configure) => {
            setShowCreate(false);
            // «Создать и настроить» — открыть карточку сразу в редактировании;
            // просто «Создать» — остаёмся на месте, задача появляется в списке
            if (configure) onSelect(task, true);
          }}
          onClose={() => setShowCreate(false)}
        />
      )}
    </div>
  );
}
