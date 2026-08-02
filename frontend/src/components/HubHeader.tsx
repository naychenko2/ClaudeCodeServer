import { useState, useEffect } from 'react';
import { Bell, ChevronRight, ExternalLink, House, Settings, Share2, Users } from 'lucide-react';
import type { AuthState, Project } from '../types';
import { C, FONT, R, TB, SHADOW } from '../lib/design';
import { useIsMobile, useWindowWidth, MOBILE_MAX, TABLET_MAX } from '../lib/breakpoints';
import { IconButton } from './ui/IconButton';
import { ICON_SIZE } from './ui/icons';
import { ProjectIcon } from '../features/projects/ProjectIcon';
import { HubTabs, type HubTab, type HubTabValue, isModuleTab } from './HubTabs';
import { ToolbarOverflowMenu, type OverflowItem } from './ToolbarOverflowMenu';
import { AvatarMenu } from '../features/projects/AvatarMenu';
import { UserManagementModal } from './UserManagementModal';
import { ChangePasswordDialog } from './ChangePasswordDialog';
import { FeatureFlagsModal } from './FeatureFlagsModal';
import { UsageScreen } from './UsageScreen';
import { ModelProvidersModal } from './ModelProvidersModal';
import { api } from '../lib/api';
import { getUnreadCount, subscribeToNotifications, ensureNotificationsSubscribed, ensureUnreadCountLoaded } from '../lib/notifications';

interface Props {
  value: HubTabValue;
  onTab: (t: HubTabValue) => void;
  auth: AuthState;
  onLogout: () => void;
  // Открыта страница «Что нового» (она не вкладка хаба, а overlay) — подсвечиваем
  // её кнопку в шапке, как подсвечен колокольчик в разделе уведомлений
  historyActive?: boolean;
  // «Открыть в новом окне» — иконка перед колокольчиком (раздел «Телеметрия»).
  // Задаётся только когда есть что открывать (SigNoz доступен); undefined — иконки нет.
  onOpenExternal?: () => void;
  // Активный проект воркспейса: задан — после логотипа рисуем хлебную крошку
  // «Домой › иконка + имя проекта» с кнопкой настроек. В разделах хаба (Чаты,
  // Заметки…) проекта нет — крошка не рисуется. Только десктоп.
  project?: Project;
  onOpenProjectSettings?: () => void;
}

// Событие открытия продуктовой истории — слушает App (overlay на верхнем уровне)
export const PRODUCT_HISTORY_EVENT = 'open-product-history';
// Метка «просмотрено» для бейджа — ISO-время последнего открытия истории.
// Ключ привязан к пользователю (userId), чтобы на одном устройстве у разных аккаунтов
// была своя отметка. Без userId (не залогинен) — общий базовый ключ.
const PRODUCT_HISTORY_SEEN_BASE = 'cc_product_history_seen';
export const productHistorySeenKey = (userId?: string | null) =>
  userId ? `${PRODUCT_HISTORY_SEEN_BASE}_${userId}` : PRODUCT_HISTORY_SEEN_BASE;

// Верхняя шапка-хаб главной страницы: логотип слева, переключатель «Чаты | Проекты» по центру,
// аватар/меню справа. На мобилке логотип и URL-бейдж скрыты (не помещаются).
export function HubHeader({ value, onTab, auth, onLogout, historyActive, onOpenExternal, project, onOpenProjectSettings }: Props) {
  const isMobile = useIsMobile();
  // Планшет (601–1199): вкладки хаба занимают всю ширину центра, и текст логотипа
  // «Home AI» рядом с крошкой проекта уже не помещается — левая секция обрезается.
  // Сворачиваем логотип до одной favicon-кнопки «Домой», отдавая место главному
  // ориентиру «где я» — имени проекта. На полном десктопе места хватает обоим.
  const w = useWindowWidth();
  const isTablet = w > MOBILE_MAX && w <= TABLET_MAX;
  const [showUserMgmt, setShowUserMgmt] = useState(false);
  const [showChangePassword, setShowChangePassword] = useState(false);
  const [showFeatureFlags, setShowFeatureFlags] = useState(false);
  const [showUsage, setShowUsage] = useState(false);
  const [showBackgroundTasks, setShowBackgroundTasks] = useState(false);

  const isAdmin = auth.role === 'admin';
  const serverUrl = localStorage.getItem('cc_server_url') ?? '';

  // «Что нового» — продуктовая история по всем проектам (основной функционал).
  // Бейдж: число новых изменений с последнего захода. Особый случай — пользователь
  // ещё ни разу не открывал историю (нет метки): показываем точку без числа
  // («тут есть что-то новенькое, загляни»).
  const [historyBadge, setHistoryBadge] = useState(0);
  const [neverSeen, setNeverSeen] = useState(false);
  const [notifBadge, setNotifBadge] = useState(0);
  const [showNotifTip, setShowNotifTip] = useState(false);       // кастомный tooltip колокольчика

  // Подписка на счётчик уведомлений. Счётчик подтягиваем и здесь: шапка живёт во всех
  // разделах, поэтому бейдж должен быть правдивым сразу, не дожидаясь захода в раздел
  // уведомлений (список грузит уже он сам).
  useEffect(() => {
    ensureNotificationsSubscribed();
    void ensureUnreadCountLoaded();
    setNotifBadge(getUnreadCount());
    return subscribeToNotifications(() => setNotifBadge(getUnreadCount()));
  }, []);

  // Один источник текста для тултипа и aria-label колокольчика
  const notifTip = notifBadge > 0
    ? `Уведомления (${notifBadge > 99 ? '99+' : notifBadge})`
    : 'Уведомления';
  useEffect(() => {
    let seen: string | null = null;
    try { seen = localStorage.getItem(productHistorySeenKey(auth.id)); } catch { /* ignore */ }
    if (!seen) {
      setNeverSeen(true); // первый заход — точка-индикатор без числа
    } else {
      setNeverSeen(false);
      api.history.newCount(seen).then(({ count }) => setHistoryBadge(count)).catch(() => {});
    }
    // Открыли историю → гасим и точку, и число (App диспатчит это же событие)
    const reset = () => { setHistoryBadge(0); setNeverSeen(false); };
    window.addEventListener(PRODUCT_HISTORY_EVENT, reset);
    return () => window.removeEventListener(PRODUCT_HISTORY_EVENT, reset);
  }, [auth.id]);

  const openHistory = () => window.dispatchEvent(new Event(PRODUCT_HISTORY_EVENT));

  // Мобильный хаб: в таббаре три primary-раздела, «Домой», «Заметки» и «Персоны» —
  // в «⋯ Разделы», иначе вкладки лезут под обрез экрана. «Знания» и «Что нового»
  // живут в меню аватара, поэтому здесь их нет.
  // «Стена» (фича wall) вкладки в таббаре НЕ имеет вовсе: вход — из воркспейса
  // проекта (док стены под доком проектов, DnD чата туда / пункт меню чата).
  // 'wall' сидит в TABLESS (HubTabs), поэтому и активная стена вкладку не
  // дописывает — PillSwitch умеет «нет выбранного». Диплинк #/wall работает.

  const PRIMARY_MOBILE: HubTab[] = ['chats', 'projects', 'calendar'];
  const HIDDEN_MOBILE: HubTab[] = ['notes', 'personas'];
  // Активен спрятанный раздел — показываем его 4-й вкладкой, чтобы подсветка была верной
  // (модульные табы — не из HubTab, их добавляет сам HubTabs из реестра)
  const mobileTabs = !isModuleTab(value) && HIDDEN_MOBILE.includes(value)
    ? [...PRIMARY_MOBILE, value] : PRIMARY_MOBILE;
  // active — подсветка текущего раздела: эти пункты живут в «⋯», и без неё
  // не видно, где находишься
  const sectionItems: OverflowItem[] = [
    // «Домой» есть и на логотипе, но дублируем пунктом: у дашборда нет своей вкладки,
    // и без строки в меню не видно, что ты на нём
    { key: 'home', icon: <House size={18} strokeWidth={2} />, label: 'Домой', onClick: () => onTab('home'), active: !historyActive && value === 'home' },
    { key: 'notes', icon: <Share2 size={18} strokeWidth={2} />, label: 'Заметки', onClick: () => onTab('notes'), active: !historyActive && value === 'notes' },
    { key: 'personas', icon: <Users size={18} strokeWidth={2} />, label: 'Персоны', onClick: () => onTab('personas'), active: !historyActive && value === 'personas' },
  ];

  // «Утренний бриф» и «Единый поиск» убраны из шапки — доступны через AI-палитру (⌘/Ctrl+K).

  // Логотип — кнопка «Домой»: клик открывает дашборд (стартовый раздел). Только десктоп.
  const [logoHover, setLogoHover] = useState(false);
  const logo = (
    <div
      role="button"
      aria-label="Домой"
      onClick={() => onTab('home')}
      onMouseEnter={() => setLogoHover(true)}
      onMouseLeave={() => setLogoHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: isTablet ? 0 : 8, minWidth: 0, cursor: 'pointer',
        // Плашка в тон кнопке аватара, но со скруглением hover-подложки (не пилюля).
        // Левый край выровнен с карточками списка чатов под ней (padX 16 + 9).
        // На планшете текста нет — плашка сжимается до квадратной favicon-кнопки.
        padding: isTablet ? 6 : '4px 12px 4px 7px', marginLeft: 9, borderRadius: 8,
        background: logoHover ? C.bgSelected : C.bgPanel, transition: 'background 0.15s',
      }}
    >
      <img src="/favicon.svg" alt="" width={26} height={26} style={{ display: 'block', flexShrink: 0 }} />
      {!isTablet && (
        <span style={{ fontFamily: FONT.serif, fontSize: 18, fontWeight: 500, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          Home AI
        </span>
      )}
    </div>
  );

  // Хлебная крошка активного проекта: «Домой › иконка + имя [⚙]». Отвечает на вопрос
  // «где я»: в вертикальном доке проект узнаётся только по подсветке иконки, а имя
  // всплывает лишь по наведению. Крошка держит его на виду всегда. Настройки —
  // отдельной кнопкой в плашке (тот же диалог, что и из подписи иконки в доке).
  const projectCrumb = project && (
    <>
      <ChevronRight size={ICON_SIZE.xs} strokeWidth={2} color={C.textMuted} style={{ flexShrink: 0 }} />
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, minWidth: 0 }}>
        <ProjectIcon project={project} size={22} radius={R.sm} />
        <span
          title={project.name}
          style={{ fontFamily: FONT.sans, fontSize: 15, fontWeight: 500, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', maxWidth: isTablet ? 150 : 220 }}
        >
          {project.name}
        </span>
        {onOpenProjectSettings && (
          <IconButton size="sm" ariaLabel="Настройки проекта" title="Настройки проекта" onClick={onOpenProjectSettings}>
            <Settings size={ICON_SIZE.sm} strokeWidth={2} />
          </IconButton>
        )}
      </div>
    </>
  );

  return (
    <div style={{
      flexShrink: 0, display: 'flex', alignItems: 'center', gap: 12,
      height: isMobile ? TB.heightMobile : TB.heightDesktop,
      padding: `0 ${isMobile ? TB.padXMobile : TB.padX}px`,
      // Десктоп: шапка сливается с холстом (Islands) — граница не нужна, острова
      // начинаются под ней с зазором. Мобилка: полноэкранные списки без островов,
      // без границы шапка повисла бы в воздухе.
      boxSizing: 'border-box', borderBottom: isMobile ? `1px solid ${C.border}` : 'none',
    }}>
      {/* Левая секция — логотип, он же вход на дашборд. На мобиле скрыт: место нужно
          вкладкам, а на дашборд там ведёт пункт «Домой» в «⋯ Разделы» */}
      {!isMobile && (
        <div style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', gap: 8 }}>
          {logo}
          {projectCrumb}
        </div>
      )}

      {/* Центр — переключатель вкладок. На мобиле компакт-режим (иконки, подпись
          у активного); если пять разделов не влезут — таббар скроллится, не обрезается */}
      {isMobile ? (
        <div style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6 }}>
          <div className="cc-no-scrollbar" style={{ minWidth: 0, display: 'flex', overflowX: 'auto', overflowY: 'hidden' }}>
            <div style={{ flexShrink: 0, display: 'flex' }}>
              <HubTabs mobile value={value} onChange={onTab} tabs={mobileTabs} />
            </div>
          </div>
          {/* «Домой», «Заметки» и «Персоны» — в overflow «⋯», а не под скролл таббара */}
          <ToolbarOverflowMenu isMobile title="Разделы" items={sectionItems} />
        </div>
      ) : (
        <HubTabs value={value} onChange={onTab} />
      )}

      {/* Правая секция — меню аватара (управление пользователями — внутри меню, admin) */}
      <div style={{ flex: isMobile ? 'none' : 1, minWidth: 0, display: 'flex', alignItems: 'center', justifyContent: 'flex-end', gap: 8 }}>
        {/* «Открыть в новом окне» — только в разделе «Телеметрия» и только когда SigNoz
            доступен (иначе открылась бы вкладка с ошибкой прокси). Иконкой, как колокольчик. */}
        {onOpenExternal && (
          <button
            onClick={onOpenExternal}
            aria-label="Открыть в новом окне"
            title="Открыть в новом окне"
            style={{
              width: 32, height: 32, borderRadius: 8, border: 'none',
              background: 'none', color: C.textSecondary, cursor: 'pointer',
              display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
            }}
            onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
            onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}
          >
            <ExternalLink size={17} strokeWidth={2} />
          </button>
        )}

        {/* Колокольчик уведомлений — бейдж с числом непрочитанных */}
        <button
          onClick={() => onTab('notifications')}
          aria-label={notifTip}
          style={{
            position: 'relative', width: 32, height: 32, borderRadius: 8, border: 'none',
            background: 'none', color: value === 'notifications' ? C.accent : C.textSecondary,
            cursor: 'pointer',
            display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
          }}
          onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; setShowNotifTip(true); }}
          onMouseLeave={e => { e.currentTarget.style.background = 'none'; setShowNotifTip(false); }}
        >
          <Bell size={17} strokeWidth={2} />
          {notifBadge > 0 && (
            <span style={{
              position: 'absolute', top: -3, right: -5, minWidth: 15, height: 15,
              padding: '0 4px', borderRadius: 8, background: C.accent, color: C.onAccent,
              fontSize: 9.5, fontWeight: 700, lineHeight: '15px', textAlign: 'center',
              boxSizing: 'border-box', pointerEvents: 'none',
            }}>
              {notifBadge > 99 ? '99+' : notifBadge}
            </span>
          )}
          {/* Кастомный tooltip в стиле приложения — как у соседней кнопки «Что нового» */}
          {showNotifTip && (
            <span style={{
              position: 'absolute', top: 'calc(100% + 7px)', right: 0, zIndex: 200,
              background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: 8,
              boxShadow: SHADOW.dropdown, padding: '5px 10px',
              fontSize: 12, fontWeight: 500, color: C.textHeading, whiteSpace: 'nowrap',
              fontFamily: FONT.sans, pointerEvents: 'none',
            }}>
              {notifTip}
            </span>
          )}
        </button>

        {/* «Единый поиск» и «Утренний бриф» убраны из шапки — теперь только через AI-палитру (⌘/Ctrl+K). */}
        {/* «Что нового» своей кнопки в шапке больше не имеет: на десктопе пункт живёт
            в меню аватара (рядом с «Настройкой знаний»), на мобилке — в «⋯ Разделы».
            Сигнал о новом виден на самом пункте меню — аватар им не обвешиваем. */}
        <AvatarMenu
          username={auth.username}
          displayName={auth.displayName}
          isAdmin={isAdmin}
          serverUrl={serverUrl}
          onLogout={onLogout}
          onShowChangePassword={() => setShowChangePassword(true)}
          onShowFeatureFlags={() => setShowFeatureFlags(true)}
          onShowUsage={() => setShowUsage(true)}
          onShowBackgroundTasks={() => setShowBackgroundTasks(true)}
          onShowUserManagement={() => setShowUserMgmt(true)}
          hideStatus={isMobile}
          // «Знания», «Аналитика токенов» и «Что нового» живут здесь на обеих платформах:
          // в таббар они не входят, а отдельного меню разделов больше нет
          onOpenKnowledge={() => onTab('knowledge')}
          onOpenSpend={() => onTab('spend')}
          // Телеметрия — только админам (проброс SigNoz под [Authorize(Roles=admin)])
          onOpenTelemetry={isAdmin ? () => onTab('telemetry') : undefined}
          onShowHistory={openHistory}
          historyBadge={historyBadge}
          historyNeverSeen={neverSeen}
          historyActive={historyActive}
        />
      </div>

      {showUserMgmt && <UserManagementModal currentUserId={auth.id} onClose={() => setShowUserMgmt(false)} />}
      {showChangePassword && <ChangePasswordDialog onClose={() => setShowChangePassword(false)} />}
      {showFeatureFlags && <FeatureFlagsModal onClose={() => setShowFeatureFlags(false)} />}
      {showUsage && <UsageScreen onClose={() => setShowUsage(false)} />}
      {showBackgroundTasks && <ModelProvidersModal isAdmin={isAdmin} onClose={() => setShowBackgroundTasks(false)} />}
    </div>
  );
}
