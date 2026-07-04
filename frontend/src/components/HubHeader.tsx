import { useState, useEffect } from 'react';
import type { AuthState } from '../types';
import { C, FONT, TB } from '../lib/design';
import { HubTabs, type HubTab } from './HubTabs';
import { AvatarMenu } from '../features/projects/AvatarMenu';
import { UserManagementModal } from './UserManagementModal';
import { ChangePasswordDialog } from './ChangePasswordDialog';
import { FeatureFlagsModal } from './FeatureFlagsModal';
import { api } from '../lib/api';

interface Props {
  value: HubTab;
  onTab: (t: HubTab) => void;
  auth: AuthState;
  onLogout: () => void;
}

// Событие открытия продуктовой истории — слушает App (overlay на верхнем уровне)
export const PRODUCT_HISTORY_EVENT = 'open-product-history';
// Метка «просмотрено» для бейджа — ISO-время последнего открытия истории
export const PRODUCT_HISTORY_SEEN_KEY = 'cc_product_history_seen';

// Мобильный брейкпоинт (совпадает с остальными раскладками)
function useIsMobile() {
  const [mobile, setMobile] = useState(() =>
    typeof window !== 'undefined' ? window.matchMedia('(max-width: 767px)').matches : false
  );
  useEffect(() => {
    const mq = window.matchMedia('(max-width: 767px)');
    const handler = (e: MediaQueryListEvent) => setMobile(e.matches);
    setMobile(mq.matches);
    mq.addEventListener('change', handler);
    return () => mq.removeEventListener('change', handler);
  }, []);
  return mobile;
}

// Верхняя шапка-хаб главной страницы: логотип слева, переключатель «Чаты | Проекты» по центру,
// аватар/меню справа. На мобилке логотип и URL-бейдж скрыты (не помещаются).
export function HubHeader({ value, onTab, auth, onLogout }: Props) {
  const isMobile = useIsMobile();
  const [showUserMgmt, setShowUserMgmt] = useState(false);
  const [showChangePassword, setShowChangePassword] = useState(false);
  const [showFeatureFlags, setShowFeatureFlags] = useState(false);

  const isAdmin = auth.role === 'admin';
  const serverUrl = localStorage.getItem('cc_server_url') ?? '';

  // «Что нового» — продуктовая история по всем проектам (основной функционал).
  // Бейдж: сколько изменений в проектах появилось с последнего захода.
  const [historyBadge, setHistoryBadge] = useState(0);
  useEffect(() => {
    let seen: string | null = null;
    try { seen = localStorage.getItem(PRODUCT_HISTORY_SEEN_KEY); } catch { /* ignore */ }
    if (!seen) return; // первый заход — бейдж не показываем
    api.history.newCount(seen).then(({ count }) => setHistoryBadge(count)).catch(() => {});
    // Сбрасываем бейдж, когда историю открыли (App диспатчит это же событие)
    const reset = () => setHistoryBadge(0);
    window.addEventListener(PRODUCT_HISTORY_EVENT, reset);
    return () => window.removeEventListener(PRODUCT_HISTORY_EVENT, reset);
  }, []);

  const openHistory = () => window.dispatchEvent(new Event(PRODUCT_HISTORY_EVENT));

  const logo = (
    <div style={{ display: 'flex', alignItems: 'center', gap: 10, minWidth: 0 }}>
      <img src="/favicon.svg" alt="" width={30} height={30} style={{ display: 'block', flexShrink: 0 }} />
      <span style={{ fontFamily: FONT.serif, fontSize: 18, fontWeight: 500, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
        Claude Home
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
      {/* Левая секция — логотип (скрыт на мобилке) */}
      <div style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'center' }}>
        {!isMobile && logo}
      </div>

      {/* Центр — переключатель вкладок */}
      <HubTabs value={value} onChange={onTab} />

      {/* Правая секция — меню аватара (управление пользователями — внутри меню, admin) */}
      <div style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', justifyContent: 'flex-end', gap: 8 }}>
        {/* «Что нового» — продуктовая история по всем проектам (во всех разделах) */}
        {(
          <button
            onClick={openHistory}
            title="Что нового"
            style={{
              position: 'relative', width: 32, height: 32, borderRadius: 8, border: 'none',
              background: 'none', color: C.textSecondary, cursor: 'pointer',
              display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
            }}
            onMouseEnter={e => { e.currentTarget.style.background = 'rgba(0,0,0,0.05)'; }}
            onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}
          >
            <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8" />
              <path d="M3 3v5h5" />
              <polyline points="12 7 12 12 15 14" />
            </svg>
            {(historyBadge ?? 0) > 0 && (
              <span style={{
                position: 'absolute', top: -3, right: -5, minWidth: 15, height: 15,
                padding: '0 4px', borderRadius: 8, background: C.accent, color: '#fff',
                fontSize: 9.5, fontWeight: 700, lineHeight: '15px', textAlign: 'center',
                boxSizing: 'border-box', pointerEvents: 'none',
              }}>
                {historyBadge! > 99 ? '99+' : historyBadge}
              </span>
            )}
          </button>
        )}
        <AvatarMenu
          username={auth.username}
          isAdmin={isAdmin}
          serverUrl={serverUrl}
          onLogout={onLogout}
          onShowChangePassword={() => setShowChangePassword(true)}
          onShowFeatureFlags={() => setShowFeatureFlags(true)}
          onShowUserManagement={() => setShowUserMgmt(true)}
          hideStatus={isMobile}
        />
      </div>

      {showUserMgmt && <UserManagementModal currentUserId={auth.id} onClose={() => setShowUserMgmt(false)} />}
      {showChangePassword && <ChangePasswordDialog onClose={() => setShowChangePassword(false)} />}
      {showFeatureFlags && <FeatureFlagsModal onClose={() => setShowFeatureFlags(false)} />}
    </div>
  );
}
