import { useState, useEffect } from 'react';
import { Bell, House, Share2, Users } from 'lucide-react';
import type { AuthState } from '../types';
import { C, FONT, TB, SHADOW } from '../lib/design';
import { useIsMobile } from '../lib/breakpoints';
import { HubTabs, type HubTab } from './HubTabs';
import { ToolbarOverflowMenu, type OverflowItem } from './ToolbarOverflowMenu';
import { AvatarMenu } from '../features/projects/AvatarMenu';
import { UserManagementModal } from './UserManagementModal';
import { ChangePasswordDialog } from './ChangePasswordDialog';
import { FeatureFlagsModal } from './FeatureFlagsModal';
import { UsageScreen } from './UsageScreen';
import { api } from '../lib/api';
import { getUnreadCount, subscribeToNotifications, ensureNotificationsSubscribed, ensureUnreadCountLoaded } from '../lib/notifications';

interface Props {
  value: HubTab;
  onTab: (t: HubTab) => void;
  auth: AuthState;
  onLogout: () => void;
  // Открыта страница «Что нового» (она не вкладка хаба, а overlay) — подсвечиваем
  // её кнопку в шапке, как подсвечен колокольчик в разделе уведомлений
  historyActive?: boolean;
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
export function HubHeader({ value, onTab, auth, onLogout, historyActive }: Props) {
  const isMobile = useIsMobile();
  const [showUserMgmt, setShowUserMgmt] = useState(false);
  const [showChangePassword, setShowChangePassword] = useState(false);
  const [showFeatureFlags, setShowFeatureFlags] = useState(false);
  const [showUsage, setShowUsage] = useState(false);

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
  const PRIMARY_MOBILE: HubTab[] = ['chats', 'projects', 'calendar'];
  const HIDDEN_MOBILE: HubTab[] = ['notes', 'personas'];
  // Активен спрятанный раздел — показываем его 4-й вкладкой, чтобы подсветка была верной
  const mobileTabs = HIDDEN_MOBILE.includes(value) ? [...PRIMARY_MOBILE, value] : PRIMARY_MOBILE;
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
        display: 'flex', alignItems: 'center', gap: 10, minWidth: 0, cursor: 'pointer',
        padding: '4px 8px', margin: '-4px -8px', borderRadius: 8,
        background: logoHover ? C.bgSelected : 'transparent', transition: 'background 0.15s',
      }}
    >
      <img src="/favicon.svg" alt="" width={30} height={30} style={{ display: 'block', flexShrink: 0 }} />
      <span style={{ fontFamily: FONT.serif, fontSize: 18, fontWeight: 500, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
        AI Home
      </span>
    </div>
  );

  return (
    <div style={{
      flexShrink: 0, display: 'flex', alignItems: 'center', gap: 12,
      height: isMobile ? TB.heightMobile : TB.heightDesktop,
      padding: `0 ${isMobile ? TB.padXMobile : TB.padX}px`,
      boxSizing: 'border-box', borderBottom: `1px solid ${C.border}`,
    }}>
      {/* Левая секция — логотип, он же вход на дашборд. На мобиле скрыт: место нужно
          вкладкам, а на дашборд там ведёт пункт «Домой» в «⋯ Разделы» */}
      {!isMobile && (
        <div style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'center' }}>
          {logo}
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
          onShowUserManagement={() => setShowUserMgmt(true)}
          hideStatus={isMobile}
          // «Знания» и «Что нового» живут здесь на обеих платформах: в таббар они не
          // входят, а отдельного меню разделов больше нет
          onOpenKnowledge={() => onTab('knowledge')}
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
    </div>
  );
}
