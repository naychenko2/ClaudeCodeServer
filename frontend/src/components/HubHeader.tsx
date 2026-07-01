import { useState, useEffect } from 'react';
import type { AuthState } from '../types';
import { C, FONT, TB } from '../lib/design';
import { HubTabs, type HubTab } from './HubTabs';
import { AvatarMenu } from '../features/projects/AvatarMenu';
import { UserManagementModal } from './UserManagementModal';
import { ChangePasswordDialog } from './ChangePasswordDialog';
import { FeatureFlagsModal } from './FeatureFlagsModal';

interface Props {
  value: HubTab;
  onTab: (t: HubTab) => void;
  auth: AuthState;
  onLogout: () => void;
}

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
