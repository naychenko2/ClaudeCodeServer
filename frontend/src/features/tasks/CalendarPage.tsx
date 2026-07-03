// Раздел «Календарь» хаба: все задачи пользователя по всем проектам.
// Виды Месяц / Неделя / Агенда, фильтр по группам проектов, «+ Задача».

import { useEffect, useMemo, useState } from 'react';
import type { AuthState, Project, ProjectGroup, Task } from '../../types';
import type { HubTab } from '../../components/HubTabs';
import { HubHeader } from '../../components/HubHeader';
import { PillSwitch } from '../../components/Toolbar';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { api } from '../../lib/api';
import { ensureTasksLoaded, todayIso, useTasks } from '../../lib/tasks';
import { CalendarMonth } from './CalendarMonth';
import { CalendarWeek } from './CalendarWeek';
import { CalendarAgenda } from './CalendarAgenda';
import { NewTaskDialog } from './NewTaskDialog';

interface Props {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTab) => void;
  // Открыть задачу в воркспейсе её проекта (вкладка «Задачи»)
  onOpenTask: (project: Project, taskId: string) => void;
}

type CalView = 'month' | 'week' | 'agenda';
const VIEW_KEY = 'cc_cal_view';

function useIsMobile() {
  const [mobile, setMobile] = useState(() => window.matchMedia('(max-width: 767px)').matches);
  useEffect(() => {
    const mq = window.matchMedia('(max-width: 767px)');
    const handler = (e: MediaQueryListEvent) => setMobile(e.matches);
    mq.addEventListener('change', handler);
    return () => mq.removeEventListener('change', handler);
  }, []);
  return mobile;
}

// Иконки мобильного переключателя вида
function ViewIcon({ view }: { view: CalView }) {
  const common = { width: 16, height: 16, viewBox: '0 0 24 24', fill: 'none' as const, stroke: 'currentColor', strokeWidth: 2, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const };
  if (view === 'month') {
    return (
      <svg {...common}>
        <rect x="3" y="3" width="7" height="7" rx="1.5" /><rect x="14" y="3" width="7" height="7" rx="1.5" />
        <rect x="3" y="14" width="7" height="7" rx="1.5" /><rect x="14" y="14" width="7" height="7" rx="1.5" />
      </svg>
    );
  }
  if (view === 'week') {
    return (
      <svg {...common}>
        <rect x="3" y="4" width="18" height="17" rx="2" /><path d="M3 9h18M9 9v12M15 9v12M8 2v4M16 2v4" />
      </svg>
    );
  }
  return (
    <svg {...common}>
      <line x1="8" y1="6" x2="21" y2="6" /><line x1="8" y1="12" x2="21" y2="12" /><line x1="8" y1="18" x2="21" y2="18" />
      <circle cx="4" cy="6" r="0.5" /><circle cx="4" cy="12" r="0.5" /><circle cx="4" cy="18" r="0.5" />
    </svg>
  );
}

const VIEW_LABEL: Record<CalView, string> = { month: 'Месяц', week: 'Неделя', agenda: 'Агенда' };

export function CalendarPage({ auth, onLogout, onHubTab, onOpenTask }: Props) {
  const isMobile = useIsMobile();
  const allTasks = useTasks();
  const [view, setView] = useState<CalView>(() => {
    const v = localStorage.getItem(VIEW_KEY);
    return v === 'week' || v === 'agenda' ? v : 'month';
  });
  useEffect(() => { localStorage.setItem(VIEW_KEY, view); }, [view]);

  const [navDate, setNavDate] = useState(todayIso());
  const [projects, setProjects] = useState<Project[]>([]);
  const [groups, setGroups] = useState<ProjectGroup[]>([]);
  const [groupFilter, setGroupFilter] = useState<string>('all');   // 'all' | groupId | 'none'
  const [showCreate, setShowCreate] = useState(false);

  useEffect(() => {
    void ensureTasksLoaded();
    api.projects.list().then(setProjects).catch(() => {});
    api.projectGroups.list().then(setGroups).catch(() => {});
  }, []);

  const projectsById = useMemo(() => new Map(projects.map(p => [p.id, p])), [projects]);

  // Фильтр по группе проектов
  const tasks = useMemo(() => {
    if (groupFilter === 'all') return allTasks;
    return allTasks.filter(t => {
      const p = projectsById.get(t.projectId);
      if (!p) return false;
      return groupFilter === 'none' ? !p.groupId : p.groupId === groupFilter;
    });
  }, [allTasks, groupFilter, projectsById]);

  const hasUngrouped = projects.some(p => !p.groupId);

  const handleOpenTask = (task: Task) => {
    const project = projectsById.get(task.projectId);
    if (project) onOpenTask(project, task.id);
  };

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
    <div style={{
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

  const currentView = view === 'month' ? (
    <CalendarMonth tasks={tasks} projectsById={projectsById} navDate={navDate} onNavigate={setNavDate} onOpenTask={handleOpenTask} isMobile={isMobile} />
  ) : view === 'week' ? (
    <CalendarWeek tasks={tasks} projectsById={projectsById} navDate={navDate} onNavigate={setNavDate} onOpenTask={handleOpenTask} isMobile={isMobile} />
  ) : (
    <CalendarAgenda tasks={tasks} projectsById={projectsById} onOpenTask={handleOpenTask} isMobile={isMobile} />
  );

  return (
    <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <HubHeader value="calendar" onTab={onHubTab} auth={auth} onLogout={onLogout} />

      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
        <div style={{ maxWidth: 1180, margin: '0 auto', padding: isMobile ? '12px 16px 0' : '20px 32px 0', boxSizing: 'border-box' }}>
          {isMobile ? (
            <>
              {/* Мобильный переключатель вида: иконка + подпись */}
              <div style={{ display: 'flex', gap: 8, marginBottom: 12 }}>
                {(['month', 'week', 'agenda'] as CalView[]).map(v => {
                  const active = view === v;
                  return (
                    <button
                      key={v}
                      onClick={() => setView(v)}
                      style={{
                        flex: 1, display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 5,
                        padding: '11px 0 9px', cursor: 'pointer',
                        border: 'none', borderRadius: R.xl,
                        background: active ? C.accentLight : 'transparent',
                        color: active ? C.accent : C.textSecondary,
                        fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600,
                        transition: 'background 0.15s, color 0.15s',
                      }}
                    >
                      <ViewIcon view={v} />
                      {VIEW_LABEL[v]}
                    </button>
                  );
                })}
              </div>
              {filters}
            </>
          ) : (
            <>
              {/* Заголовок + переключатель вида + «Задача» */}
              <div style={{ display: 'flex', alignItems: 'center', gap: 14, marginBottom: 14 }}>
                <h1 style={{ margin: 0, fontFamily: FONT.serif, fontSize: 28, fontWeight: 500, color: C.textHeading, flex: 1 }}>
                  Календарь
                </h1>
                <PillSwitch<CalView>
                  value={view}
                  options={[
                    { value: 'month', label: 'Месяц' },
                    { value: 'week', label: 'Неделя' },
                    { value: 'agenda', label: 'Агенда' },
                  ]}
                  onChange={setView}
                />
                <button
                  onClick={() => setShowCreate(true)}
                  style={{
                    display: 'flex', alignItems: 'center', gap: 7,
                    padding: '0 18px', height: 38, cursor: 'pointer',
                    border: 'none', borderRadius: R.lg,
                    background: C.accent, color: C.onAccent,
                    fontFamily: FONT.sans, fontSize: 13.5, fontWeight: 700,
                    boxShadow: SHADOW.button,
                  }}
                >
                  <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.6" strokeLinecap="round">
                    <line x1="12" y1="5" x2="12" y2="19" /><line x1="5" y1="12" x2="19" y2="12" />
                  </svg>
                  Задача
                </button>
              </div>
              {filters}
            </>
          )}

          {currentView}
        </div>
      </div>

      {/* Мобила: FAB «+» */}
      {isMobile && (
        <button
          onClick={() => setShowCreate(true)}
          title="Новая задача"
          style={{
            position: 'fixed', right: 18, bottom: 'calc(20px + env(safe-area-inset-bottom))', zIndex: 20,
            width: 54, height: 54, borderRadius: 18,
            background: C.accent, border: 'none', cursor: 'pointer',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            boxShadow: SHADOW.fab,
          }}
        >
          <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke={C.onAccent} strokeWidth="2.4" strokeLinecap="round">
            <line x1="12" y1="5" x2="12" y2="19" /><line x1="5" y1="12" x2="19" y2="12" />
          </svg>
        </button>
      )}

      {showCreate && (
        <NewTaskDialog
          onCreated={() => setShowCreate(false)}
          onClose={() => setShowCreate(false)}
        />
      )}
    </div>
  );
}
