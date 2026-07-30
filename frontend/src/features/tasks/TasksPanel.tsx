// Список задач проекта в левой панели воркспейса.
// Подвкладки «Список» (группировка по статусу) и «По дате» (готовые скрыты).

import { useEffect, useMemo, useState } from 'react';
import { ChevronRight, Plus, SearchX } from 'lucide-react';
import type { ReactNode } from 'react';
import type { Project, Task, TaskStatus } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { EmptyState } from '../../components/EmptyState';
import { Button } from '../../components/ui/Button';
import { IconSegmented, PanelHeaderSlot, useHasPanelHeader } from '../../components/ui';
import {
  STATUS_DOT, STATUS_LABEL, daysFromToday, ensureTasksLoaded, useTasks,
} from '../../lib/tasks';
import type { BoardGroupBy } from '../../lib/tasks';
import { TaskCard } from './TaskCard';
import { NewTaskDialog } from './NewTaskDialog';
import { BoardToolbar } from './board/BoardToolbar';
import { BoardIcon, ByDateIcon, ListIcon, PillViewSwitcher } from './bits';
import {
  TasksListFilterButton, applyTaskFilters, EMPTY_TASK_FILTERS, type TaskListFilters,
} from './TasksListFilter';

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
  // Управляемая группировка списка (когда вид задач влияет на центральную область —
  // воркспейс). Без пропа — панель держит состояние сама (мобила).
  groupTab?: GroupTab;
  onGroupTab?: (t: GroupTab) => void;
  // Фильтры списка (Статус/Исполнитель/Приоритет/Срок). Поднимаются в WorkspacePage,
  // чтобы переживать пересборку панели при смене раскладки. Без пропа — панель
  // держит состояние сама.
  filters?: TaskListFilters;
  onFilters?: (f: TaskListFilters) => void;
}

type GroupTab = 'status' | 'date';
type PanelTab = GroupTab | 'board';

interface Group { key: string; label: string; dot?: string; tasks: Task[] }

const STATUS_GROUP_ORDER: TaskStatus[] = ['inProgress', 'todo', 'done'];

function groupByStatus(tasks: Task[]): Group[] {
  return STATUS_GROUP_ORDER
    .map(s => {
      let groupTasks = tasks.filter(t => t.status === s);
      // «Готово» — недавно завершённые сверху: обратный хронологический порядок
      // по completedAt (фолбэк updatedAt/createdAt для старых задач без completedAt)
      if (s === 'done') {
        groupTasks = [...groupTasks].sort((a, b) => {
          const at = a.completedAt ?? a.updatedAt ?? a.createdAt;
          const bt = b.completedAt ?? b.updatedAt ?? b.createdAt;
          return at < bt ? 1 : at > bt ? -1 : 0;
        });
      }
      return { key: s, label: STATUS_LABEL[s], dot: STATUS_DOT[s], tasks: groupTasks };
    })
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

export function TasksPanel({ project, selectedTaskId, onSelect, isMobile, boardMode, onBoardMode, onEditColumns, groupTab: groupTabProp, onGroupTab, filters: filtersProp, onFilters: onFiltersProp }: Props) {
  const allTasks = useTasks();
  const [loading, setLoading] = useState(true);
  // Группировка списка: управляемая сверху (cc-panels) или локальная (старый сайдбар)
  const [localGroupTab, setLocalGroupTab] = useState<GroupTab>('status');
  const groupTab = groupTabProp ?? localGroupTab;
  const setGroupTab = onGroupTab ?? setLocalGroupTab;
  const [showCreate, setShowCreate] = useState(false);
  // Фильтры списка: управляемые сверху (WorkspacePage) или локальные
  const [localFilters, setLocalFilters] = useState<TaskListFilters>(EMPTY_TASK_FILTERS);
  const filters = filtersProp ?? localFilters;
  const onFilters = onFiltersProp ?? setLocalFilters;

  // Панель в карточке с шапкой — контролы уезжают туда (компактный ряд иконок);
  // без шапки (мобила) остаётся полноразмерная строка действий в теле панели.
  const inHeader = useHasPanelHeader();

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
  // Фильтры применяются ДО группировки (работают в обеих — «Список» и «По дате»).
  // В режиме доски не применяются: там свой BoardToolbar.
  const filteredTasks = useMemo(
    () => boardMode ? tasks : applyTaskFilters(tasks, filters),
    [tasks, filters, boardMode],
  );

  const groups = groupTab === 'status' ? groupByStatus(filteredTasks) : groupByDate(filteredTasks);

  return (
    // flex:1 + minHeight:0 — шапка (переключатель и кнопка) закреплена, скроллится только список:
    // процентная высота во вложенных flex-колонках может резолвиться в auto, и тогда ехал весь блок
    <div style={{ display: 'flex', flexDirection: 'column', flex: '1 1 auto', minHeight: 0, height: '100%', overflow: 'hidden' }}>
      {/* Контролы в шапке карточки: [фильтр] [вид] [+ новая задача].
          Создание — последним и залитым accent: это главное действие панели,
          и в ряду нейтральных иконок оно должно читаться первым. Funnel скрыт
          в режиме «Доска» (там свой BoardToolbar). */}
      {inHeader && (
        <PanelHeaderSlot>
          {panelTab !== 'board' && (
            <TasksListFilterButton
              variant="icon"
              filters={filters}
              onFilters={onFilters}
              total={tasks.length}
              found={filteredTasks.length}
            />
          )}
          <IconSegmented<PanelTab>
            value={panelTab}
            options={tabOptions([
              { value: 'status', label: 'Список', icon: <ListIcon size={14} /> },
              { value: 'date', label: 'По дате', icon: <ByDateIcon size={14} /> },
              { value: 'board', label: 'Доска', icon: <BoardIcon size={14} /> },
            ])}
            onChange={onPanelTab}
          />
          <Button
            variant="primary" size="xs" title="Новая задача"
            leftIcon={<Plus size={13} strokeWidth={ICON_STROKE} />}
            onClick={() => setShowCreate(true)}
          >
            Задача
          </Button>
        </PanelHeaderSlot>
      )}

      {/* Без шапки (мобила): та же тройка контролов, но полноразмерная — кнопка
          «Новая задача» на всю ширину, под ней переключатель видов пилюлей. */}
      {!inHeader && (
        <>
          {/* Строка действий: «Новая задача» (flex:1) + «Фильтр» (Funnel) — НАД
              переключателем видов (правка пользователя к макету А). Funnel скрыт
              в режиме доски (там свой BoardToolbar); Plus виден во всех режимах. */}
          <div style={{ padding: isMobile ? '8px 12px 4px' : '8px 12px 4px', display: 'flex', gap: 7, alignItems: 'stretch', flexShrink: 0 }}>
            <button
              onClick={() => setShowCreate(true)}
              style={{
                flex: 1, display: 'inline-flex', alignItems: 'center', justifyContent: 'center', gap: 6,
                padding: isMobile ? '9px 12px' : '8px 12px',
                border: `1px solid ${C.border}`, borderRadius: R.lg,
                background: C.bgWhite, color: C.accent,
                fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600, cursor: 'pointer',
              }}
            >
              <Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              Новая задача
            </button>
            {!boardMode && (
              <TasksListFilterButton
                variant="sidebar"
                filters={filters}
                onFilters={onFilters}
                total={tasks.length}
                found={filteredTasks.length}
                isMobile={isMobile}
              />
            )}
          </div>

          <div style={{ padding: isMobile ? '4px 14px 4px' : '0 16px 4px', flexShrink: 0 }}>
            <PillViewSwitcher<PanelTab>
              value={panelTab}
              options={tabOptions([
                { value: 'status', label: 'Список', icon: <ListIcon size={16} /> },
                { value: 'date', label: 'По дате', icon: <ByDateIcon size={16} /> },
                { value: 'board', label: 'Доска', icon: <BoardIcon size={16} /> },
              ])}
              onChange={onPanelTab}
            />
          </div>
        </>
      )}

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
                <ChevronRight size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
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
        ) : filteredTasks.length === 0 ? (
          // Фильтры отсеяли всё — предлагаем сбросить (стандартный EmptyState)
          <EmptyState
            icon={<SearchX size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
            title="По фильтрам ничего не найдено"
            action={<Button variant="secondary" size="sm" onClick={() => onFilters(EMPTY_TASK_FILTERS)}>Сбросить фильтры</Button>}
          />
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
