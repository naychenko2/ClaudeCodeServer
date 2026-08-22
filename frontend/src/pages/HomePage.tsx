import { useEffect } from 'react';
import { displayNameOf, type AuthState, type Project } from '../types';
import { C, FONT, HOME_MAX_W } from '../lib/design';
import { useIsMobile } from '../lib/breakpoints';
import { ensurePersonasLoaded } from '../lib/personas';
import { HubHeader } from '../components/HubHeader';
import { PageCanvas } from '../components/ui/PageCanvas';
import type { HubTabValue } from '../components/HubTabs';
import { useHomeSummary } from '../features/home/useHomeSummary';
import { ActivityWidget } from '../features/home/ActivityWidget';
import { TasksWidget } from '../features/home/TasksWidget';
import { UsageWidget } from '../features/home/UsageWidget';
import { SpendWidget } from '../features/home/SpendWidget';
import { RecentSessionsWidget } from '../features/home/RecentSessionsWidget';
import { QuickActions } from '../features/home/QuickActions';
import { ProjectsWidget } from '../features/home/ProjectsWidget';
import { NotesWidget } from '../features/home/NotesWidget';
import { TeamWidget } from '../features/home/TeamWidget';
import { WhatsNewWidget } from '../features/home/WhatsNewWidget';
import { NotificationsWidget } from '../features/home/NotificationsWidget';
import { BackupWidget } from '../features/home/BackupWidget';
import { WallWidget } from '../features/home/WallWidget';
import { moveToVisible, slotCount } from '../features/wall/wallStore';

interface Props {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTabValue) => void;
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
  const isAdmin = auth.role === 'admin';
  // Персоны — для подписей «Роль (Имя)» в строках сессий
  useEffect(() => { void ensurePersonasLoaded(); }, []);

  const today = new Date().toLocaleDateString('ru-RU', { weekday: 'long', day: 'numeric', month: 'long' });

  // Вход на стену из виджета. moveToVisible, а не focusChat: стена показывает первые
  // slotCount(w) колонок и фокусирует только видимую, поэтому клик по невлезшей строке
  // открыл бы чужой чат. Цена — такой клик меняет порядок набора и сохраняет его на
  // сервере; это осознанно, иначе виджет открывал бы не то, по чему кликнули.
  const openWall = (focusId?: string) => {
    if (focusId) moveToVisible(focusId, slotCount(window.innerWidth));
    onHubTab('wall');
  };

  return (
    <PageCanvas>
      <HubHeader value="home" onTab={onHubTab} auth={auth} onLogout={onLogout} />
      {/* Ширина дашборда — своя (HOME_MAX_W): почему не колонка чтения и не сетка
          раздела, объяснено при константе. Боковые отступы — на скролл-контейнере,
          а полотно несёт только вертикальные: при border-box padding на самом
          полотне сузил бы видимую ширину и увёл её от заявленной (см. design.ts) */}
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: isMobile ? '0 14px' : '0 26px' }}>
        <div style={{ maxWidth: HOME_MAX_W, margin: '0 auto', padding: isMobile ? '18px 0 28px' : '26px 0 40px' }}>
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
              колонку. Мобильная лента порядок НЕ повторяет: действия и уведомления
              сверху, следом «мои пространства», а справочные сводки (что нового,
              сейчас работают, использование) уходят в хвост — до них долистывают редко. */}
          {isMobile ? (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {/* Уведомления — сразу под действиями: единственная сводка, которая
                  требует реакции, остальные листают реже */}
              <QuickActions onHubTab={onHubTab} onOpenProject={onOpenProject} />
              <NotificationsWidget onHubTab={onHubTab} />
              {/* «Мои пространства» — то, ради чего заходят с телефона */}
              <TasksWidget onHubTab={onHubTab} />
              <RecentSessionsWidget recent={data?.recent ?? []} onHubTab={onHubTab} />
              <ProjectsWidget onOpenProject={onOpenProject} />
              <NotesWidget onHubTab={onHubTab} />
              <TeamWidget onHubTab={onHubTab} />
              {/* Справочные сводки — хвостом */}
              <WhatsNewWidget userId={auth.id} />
              <ActivityWidget active={data?.active ?? []} />
              <SpendWidget />
              <UsageWidget />
              {isAdmin && <BackupWidget />}
            </div>
          ) : (
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: 12, alignItems: 'start' }}>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 12, minWidth: 0 }}>
                <QuickActions onHubTab={onHubTab} onOpenProject={onOpenProject} />
                <NotificationsWidget onHubTab={onHubTab} />
                <WhatsNewWidget userId={auth.id} />
                <ActivityWidget active={data?.active ?? []} />
                <SpendWidget />
                <UsageWidget />
                {/* Бэкап — только админу: настройка инстансная, общая для всех */}
                {isAdmin && <BackupWidget />}
              </div>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 12, minWidth: 0 }}>
                <TasksWidget onHubTab={onHubTab} />
                <RecentSessionsWidget recent={data?.recent ?? []} onHubTab={onHubTab} />
                <ProjectsWidget onOpenProject={onOpenProject} />
                {/* Стена — сразу под проектами: она и есть «несколько проектов разом»,
                    и читается как продолжение их списка.
                    Только десктоп: стена гасит себя при ширине ≤ MOBILE_MAX (тот же
                    порог, что у useIsMobile), и на телефоне вход вёл бы в заглушку */}
                <WallWidget ownerId={auth.id} onOpenWall={openWall} />
                <NotesWidget onHubTab={onHubTab} />
                <TeamWidget onHubTab={onHubTab} />
              </div>
            </div>
          )}
        </div>
      </div>
    </PageCanvas>
  );
}
