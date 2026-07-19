import { useEffect } from 'react';
import { displayNameOf, type AuthState, type Project } from '../types';
import { C, FONT } from '../lib/design';
import { useIsMobile } from '../lib/breakpoints';
import { ensurePersonasLoaded } from '../lib/personas';
import { HubHeader } from '../components/HubHeader';
import type { HubTab } from '../components/HubTabs';
import { useHomeSummary } from '../features/home/useHomeSummary';
import { ActivityWidget } from '../features/home/ActivityWidget';
import { TasksWidget } from '../features/home/TasksWidget';
import { UsageWidget } from '../features/home/UsageWidget';
import { RecentSessionsWidget } from '../features/home/RecentSessionsWidget';
import { QuickActions } from '../features/home/QuickActions';
import { ProjectsWidget } from '../features/home/ProjectsWidget';
import { NotesWidget } from '../features/home/NotesWidget';
import { TeamWidget } from '../features/home/TeamWidget';
import { WhatsNewWidget } from '../features/home/WhatsNewWidget';
import { NotificationsWidget } from '../features/home/NotificationsWidget';

interface Props {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTab) => void;
  onOpenProject: (p: Project) => void;
}

// Приветствие по времени суток
function greeting(): string {
  const h = new Date().getHours();
  if (h >= 5 && h < 12) return 'Доброе утро';
  if (h >= 12 && h < 17) return 'Добрый день';
  if (h >= 17 && h < 23) return 'Добрый вечер';
  return 'Доброй ночи';
}

// Дашборд «Домой» — стартовый экран: сводка по всем проектам и чатам.
// Открывается кликом по логотипу в шапке (на мобилке — из «⋯ Разделы»).
export function HomePage({ auth, onLogout, onHubTab, onOpenProject }: Props) {
  const isMobile = useIsMobile();
  const { data } = useHomeSummary();
  // Персоны — для подписей «Роль (Имя)» в строках сессий
  useEffect(() => { void ensurePersonasLoaded(); }, []);

  const today = new Date().toLocaleDateString('ru-RU', { weekday: 'long', day: 'numeric', month: 'long' });

  return (
    <div style={{ height: '100dvh', display: 'flex', flexDirection: 'column', background: C.bgMain }}>
      <HubHeader value="home" onTab={onHubTab} auth={auth} onLogout={onLogout} />
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
        <div style={{ maxWidth: 1080, margin: '0 auto', padding: isMobile ? '18px 14px 28px' : '26px 26px 40px' }}>
          {/* Приветствие */}
          <div style={{ marginBottom: isMobile ? 16 : 22 }}>
            <div style={{ fontFamily: FONT.serif, fontSize: isMobile ? 24 : 28, fontWeight: 500, color: C.textHeading }}>
              {greeting()}, {displayNameOf(auth)}
            </div>
            <div style={{ fontFamily: FONT.sans, fontSize: 13.5, color: C.textMuted, marginTop: 4 }}>
              {today}
            </div>
          </div>

          {/* Виджеты: на десктопе — две НЕЗАВИСИМЫЕ колонки (каждая своим потоком,
              без выравнивания рядов — блоки разной высоты не оставляют дыр),
              на мобилке — один столбец */}
          {/* Порядок колонок: слева — «пульс продукта» (действия → уведомления →
              что нового → сейчас работают → использование), справа — «мои пространства»
              (задачи → чаты → проекты → заметки), а «команда» замыкает правую
              колонку. Мобильная лента — важное сверху вниз. */}
          {isMobile ? (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <QuickActions onHubTab={onHubTab} onOpenProject={onOpenProject} />
              <NotificationsWidget onHubTab={onHubTab} />
              <WhatsNewWidget userId={auth.id} />
              <ActivityWidget active={data?.active ?? []} />
              <TasksWidget onHubTab={onHubTab} />
              <RecentSessionsWidget recent={data?.recent ?? []} onHubTab={onHubTab} />
              <ProjectsWidget onHubTab={onHubTab} onOpenProject={onOpenProject} />
              <NotesWidget onHubTab={onHubTab} />
              <TeamWidget onHubTab={onHubTab} />
              <UsageWidget />
            </div>
          ) : (
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: 12, alignItems: 'start' }}>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 12, minWidth: 0 }}>
                <QuickActions onHubTab={onHubTab} onOpenProject={onOpenProject} />
                <NotificationsWidget onHubTab={onHubTab} />
                <WhatsNewWidget userId={auth.id} />
                <ActivityWidget active={data?.active ?? []} />
                <UsageWidget />
              </div>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 12, minWidth: 0 }}>
                <TasksWidget onHubTab={onHubTab} />
                <RecentSessionsWidget recent={data?.recent ?? []} onHubTab={onHubTab} />
                <ProjectsWidget onHubTab={onHubTab} onOpenProject={onOpenProject} />
                <NotesWidget onHubTab={onHubTab} />
                <TeamWidget onHubTab={onHubTab} />
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
