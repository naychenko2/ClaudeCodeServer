// Раздел «Календарь» хаба: все задачи пользователя по всем проектам.
// Виды Месяц / Неделя / Агенда, фильтр по группам проектов, «+ Задача».

import { useEffect, useMemo, useRef, useState } from 'react';
import { Plus } from 'lucide-react';
import type { AuthState, Project, ProjectGroup, Task } from '../../types';
import type { HubTab } from '../../components/HubTabs';
import { navPush, navReplace, parseHash, type NavSnapshot } from '../../lib/nav';
import { HubHeader } from '../../components/HubHeader';
import { PillSwitch } from '../../components/Toolbar';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { api } from '../../lib/api';
import { addDaysIso, DEFAULT_BOARD_COLUMNS, ensureTasksLoaded, expandRecurringTasks, todayIso, toIsoDate, useTasks } from '../../lib/tasks';
import { useIsMobile } from '../../lib/breakpoints';
import { AgendaIcon, BoardIcon, IconViewSwitcher, MonthIcon, WeekIcon } from './bits';
import { TaskDetailsModal } from './TaskDetailsModal';
import { CalendarMonth } from './CalendarMonth';
import { CalendarWeek } from './CalendarWeek';
import { CalendarAgenda } from './CalendarAgenda';
import { TaskBoard } from './board/TaskBoard';
import { NewTaskDialog } from './NewTaskDialog';

interface Props {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTab) => void;
  // Открыть задачу в воркспейсе её проекта (вкладка «Задачи»)
  onOpenTask: (project: Project, taskId: string) => void;
}

type CalView = 'month' | 'week' | 'agenda' | 'board';
const VIEW_KEY = 'cc_cal_view';

// Горизонт проекции повторов для агенды (у неё нет верхней границы навигации)
const AGENDA_HORIZON_DAYS = 90;

// Видимое окно дат для текущего вида — в него разворачиваем будущие повторы
function visibleRange(view: CalView, navDate: string): { from: string; to: string } {
  if (view === 'agenda') {
    const today = todayIso();
    return { from: today, to: addDaysIso(today, AGENDA_HORIZON_DAYS) };
  }
  if (view === 'week') {
    const [y, m, d] = navDate.split('-').map(Number);
    const offset = (new Date(y, m - 1, d).getDay() + 6) % 7;   // Пн = 0
    const start = addDaysIso(navDate, -offset);
    return { from: start, to: addDaysIso(start, 6) };
  }
  // month: та же сетка 6 недель, что и в CalendarMonth.monthCells
  const [y, m] = [Number(navDate.slice(0, 4)), Number(navDate.slice(5, 7)) - 1];
  const first = new Date(y, m, 1);
  const start = toIsoDate(new Date(y, m, 1 - ((first.getDay() + 6) % 7)));
  return { from: start, to: addDaysIso(start, 41) };
}

// Иконки видов — общие для мобильного и десктопного переключателей (bits.tsx)
function ViewIcon({ view, size = 16 }: { view: CalView; size?: number }) {
  if (view === 'month') return <MonthIcon size={size} />;
  if (view === 'week') return <WeekIcon size={size} />;
  if (view === 'board') return <BoardIcon size={size} />;
  return <AgendaIcon size={size} />;
}

const VIEW_LABEL: Record<CalView, string> = { month: 'Месяц', week: 'Неделя', agenda: 'Агенда', board: 'Доска' };

export function CalendarPage({ auth, onLogout, onHubTab, onOpenTask }: Props) {
  const isMobile = useIsMobile();
  const allTasks = useTasks();
  const [view, setView] = useState<CalView>(() => {
    // Диплинк #/calendar/board восстанавливает доску; иначе — сохранённый вид
    const t = parseHash();
    if (t?.screen === 'calendar' && t.board) return 'board';
    const v = localStorage.getItem(VIEW_KEY);
    return v === 'week' || v === 'agenda' || v === 'board' ? v : 'month';
  });
  // Последний НЕ-доска вид — куда вернуться по «назад» из доски
  const lastNonBoardView = useRef<CalView>(view === 'board' ? 'month' : view);
  useEffect(() => {
    localStorage.setItem(VIEW_KEY, view);
    if (view !== 'board') lastNonBoardView.current = view;
  }, [view]);

  // Браузерная навигация: доска — отдельная запись истории (#/calendar/board).
  // Монтирование пропускаем — историю экрана сидирует App (иначе гонка перетрёт URL).
  const didMountNav = useRef(false);
  useEffect(() => {
    if (!didMountNav.current) { didMountNav.current = true; return; }
    if (view === 'board') navPush({ screen: 'calendar', board: true });
    else navReplace({ screen: 'calendar' });
  }, [view]);
  // Кнопки «назад/вперёд»: восстанавливаем вид доски из снимка истории
  useEffect(() => {
    const onPop = (e: PopStateEvent) => {
      const s = e.state as NavSnapshot | null;
      if (s?.screen !== 'calendar') return;
      setView(s.board ? 'board' : lastNonBoardView.current);
    };
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, []);

  const [navDate, setNavDate] = useState(todayIso());
  const [projects, setProjects] = useState<Project[]>([]);
  const [groups, setGroups] = useState<ProjectGroup[]>([]);
  const [groupFilter, setGroupFilter] = useState<string>('all');   // 'all' | groupId | 'none'
  // Диалог создания: null — закрыт; date — предзаполненный срок (быстрое создание на день)
  const [createDialog, setCreateDialog] = useState<null | { date?: string }>(null);
  // Личная задача, открытая в модале (id — чтобы карточка жила и обновлялась из стора)
  const [personalTaskId, setPersonalTaskId] = useState<string | null>(null);
  // Открыть модал сразу в редактировании (после «Создать и настроить»)
  const [personalEdit, setPersonalEdit] = useState(false);

  useEffect(() => {
    void ensureTasksLoaded();
    api.projects.list().then(setProjects).catch(() => {});
    api.projectGroups.list().then(setGroups).catch(() => {});
    // Диплинк #/calendar/task/{id} (из тоста/push) — открываем модал личной задачи.
    // Проверяем при монтировании и по событию cc-pending-task (клик по тосту,
    // когда календарь уже на экране)
    const consumePendingTask = () => {
      const pending = sessionStorage.getItem('cc_pending_calendar_task');
      if (pending) {
        sessionStorage.removeItem('cc_pending_calendar_task');
        setPersonalEdit(false);
        setPersonalTaskId(pending);
      }
    };
    consumePendingTask();
    window.addEventListener('cc-pending-task', consumePendingTask);
    return () => window.removeEventListener('cc-pending-task', consumePendingTask);
  }, []);

  const projectsById = useMemo(() => new Map(projects.map(p => [p.id, p])), [projects]);

  // Фильтр по группе проектов; личные задачи (без проекта) — в «Все» и «Без группы»
  const tasks = useMemo(() => {
    if (groupFilter === 'all') return allTasks;
    return allTasks.filter(t => {
      if (!t.projectId) return groupFilter === 'none';
      const p = projectsById.get(t.projectId);
      if (!p) return false;
      return groupFilter === 'none' ? !p.groupId : p.groupId === groupFilter;
    });
  }, [allTasks, groupFilter, projectsById]);

  const hasUngrouped = projects.some(p => !p.groupId);

  // Задачи для календаря: реальные + вычисленные будущие повторы (virtual) в видимом окне
  const calendarTasks = useMemo(() => {
    const { from, to } = visibleRange(view, navDate);
    return expandRecurringTasks(tasks, from, to);
  }, [tasks, view, navDate]);

  const handleOpenTask = (task: Task) => {
    // Клик по виртуальному повтору открывает единственный реальный экземпляр серии
    const realId = task.occurrenceOf ?? task.id;
    const real = allTasks.find(t => t.id === realId) ?? task;
    // Личная задача — детали в модале поверх календаря (воркспейса у неё нет)
    if (!real.projectId) {
      setPersonalEdit(false);
      setPersonalTaskId(realId);
      return;
    }
    const project = projectsById.get(real.projectId);
    if (project) onOpenTask(project, realId);
  };

  // «Создать и настроить» из календаря: личная — модал в редактировании,
  // проектная — переход в проект с открытым редактором (флаг через sessionStorage)
  const handleCreated = (task: Task, configure: boolean) => {
    setCreateDialog(null);
    if (!configure) return;
    if (!task.projectId) {
      setPersonalEdit(true);
      setPersonalTaskId(task.id);
      return;
    }
    const project = projectsById.get(task.projectId);
    if (project) {
      sessionStorage.setItem('cc_pending_task_edit', '1');
      onOpenTask(project, task.id);
    }
  };

  const personalTask = personalTaskId
    ? allTasks.find(t => t.id === personalTaskId) ?? null
    : null;

  const filterChip = (key: string, label: string, dot?: string) => {
    const active = groupFilter === key;
    return (
      <button
        key={key}
        onClick={() => setGroupFilter(key)}
        style={{
          display: 'inline-flex', alignItems: 'center', gap: 7, flexShrink: 0,
          padding: '6px 13px', cursor: 'pointer',
          border: `1px solid ${active ? C.accent : C.border}`,
          borderRadius: 999,
          background: active ? C.accentLight : C.bgWhite,
          fontFamily: FONT.sans, fontSize: 12.5, fontWeight: active ? 700 : 500,
          color: C.textPrimary, whiteSpace: 'nowrap',
          transition: 'border-color 0.12s, background 0.12s',
        }}
      >
        <span style={{ width: 8, height: 8, borderRadius: '50%', background: dot ?? C.textMuted, flexShrink: 0 }} />
        {label}
      </button>
    );
  };

  const filters = (
    <div className="cc-hide-scrollbar" style={{
      display: 'flex', alignItems: 'center', gap: 8,
      overflowX: 'auto', paddingBottom: 2,
    }}>
      {!isMobile && (
        <span style={{
          fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 700, color: C.textMuted,
          textTransform: 'uppercase', letterSpacing: '0.07em', flexShrink: 0, marginRight: 2,
        }}>
          Фильтр
        </span>
      )}
      {filterChip('all', 'Все')}
      {groups.map(g => filterChip(g.id, g.name, g.color))}
      {hasUngrouped && groups.length > 0 && filterChip('none', 'Без группы')}
    </div>
  );

  const currentView = view === 'board' ? (
    <TaskBoard tasks={tasks} columns={DEFAULT_BOARD_COLUMNS} projectsById={projectsById} onOpenTask={handleOpenTask} isMobile={isMobile} />
  ) : view === 'month' ? (
    <CalendarMonth tasks={calendarTasks} projectsById={projectsById} navDate={navDate} onNavigate={setNavDate} onOpenTask={handleOpenTask} onQuickCreate={iso => setCreateDialog({ date: iso })} isMobile={isMobile} />
  ) : view === 'week' ? (
    <CalendarWeek tasks={calendarTasks} projectsById={projectsById} navDate={navDate} onNavigate={setNavDate} onOpenTask={handleOpenTask} isMobile={isMobile} />
  ) : (
    <CalendarAgenda tasks={calendarTasks} projectsById={projectsById} onOpenTask={handleOpenTask} isMobile={isMobile} />
  );

  return (
    <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <HubHeader value="calendar" onTab={onHubTab} auth={auth} onLogout={onLogout} />

      {/* Мобила: переключатель вида и фильтры закреплены над скролл-областью */}
      {isMobile && (
        <div style={{ flexShrink: 0, padding: '12px 16px 10px', borderBottom: `1px solid ${C.borderLight}` }}>
          <div style={{ marginBottom: 12 }}>
            <IconViewSwitcher<CalView>
              value={view}
              options={(['month', 'week', 'agenda', 'board'] as CalView[]).map(v => ({
                value: v, label: VIEW_LABEL[v], icon: <ViewIcon view={v} />,
              }))}
              onChange={setView}
            />
          </div>
          {filters}
        </div>
      )}

      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
        <div style={{ maxWidth: 1180, margin: '0 auto', padding: isMobile ? '0 16px' : '20px 32px 0', boxSizing: 'border-box' }}>
          {!isMobile && (
            <>
              {/* Заголовок + переключатель вида + «Задача» */}
              <div style={{ display: 'flex', alignItems: 'center', gap: 14, marginBottom: 14 }}>
                <h1 style={{ margin: 0, fontFamily: FONT.serif, fontSize: 28, fontWeight: 500, color: C.textHeading, flex: 1 }}>
                  Календарь
                </h1>
                <PillSwitch<CalView>
                  value={view}
                  options={[
                    { value: 'month', label: 'Месяц', icon: <MonthIcon /> },
                    { value: 'week', label: 'Неделя', icon: <WeekIcon /> },
                    { value: 'agenda', label: 'Агенда', icon: <AgendaIcon /> },
                    { value: 'board' as const, label: 'Доска', icon: <BoardIcon /> },
                  ]}
                  onChange={setView}
                />
                <button
                  onClick={() => setCreateDialog({})}
                  style={{
                    display: 'flex', alignItems: 'center', gap: 7,
                    padding: '0 18px', height: 38, cursor: 'pointer',
                    border: 'none', borderRadius: R.lg,
                    background: C.accent, color: C.onAccent,
                    fontFamily: FONT.sans, fontSize: 13.5, fontWeight: 700,
                    boxShadow: SHADOW.button,
                  }}
                >
                  <Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                  Задача
                </button>
              </div>
              {filters}
            </>
          )}

          {currentView}
        </div>
      </div>

      {/* Мобила: FAB «+». ЛЕВЫЙ нижний угол — правый занят глобальным AiLauncher (⌘/Ctrl+K),
          иначе кнопки наложились бы (как и у PersonaEditFab). */}
      {isMobile && (
        <button
          onClick={() => setCreateDialog({})}
          title="Новая задача"
          style={{
            position: 'fixed', left: 18, bottom: 'calc(20px + env(safe-area-inset-bottom))', zIndex: 20,
            width: 54, height: 54, borderRadius: 18,
            background: C.accent, border: 'none', cursor: 'pointer',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            boxShadow: SHADOW.fab,
          }}
        >
          <Plus size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} color={C.onAccent} />
        </button>
      )}

      {createDialog && (
        <NewTaskDialog
          configureLabel="Подробнее"
          defaultDueDate={createDialog.date}
          onCreated={handleCreated}
          onClose={() => setCreateDialog(null)}
        />
      )}

      {personalTask && (
        <TaskDetailsModal
          task={personalTask}
          isMobile={isMobile}
          startInEdit={personalEdit}
          onClose={() => { setPersonalTaskId(null); setPersonalEdit(false); }}
        />
      )}
    </div>
  );
}
