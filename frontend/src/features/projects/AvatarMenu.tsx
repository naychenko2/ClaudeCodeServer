import { useEffect, useRef, useState } from 'react';
import { C, R, SHADOW, Z } from '../../lib/design';
import { ConnectionStatus } from '../../components/ConnectionStatus';

const dropdownItem: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 9,
  width: '100%', textAlign: 'left', padding: '8px 14px',
  background: 'none', border: 'none', cursor: 'pointer',
  fontSize: 13.5, fontWeight: 500, fontFamily: 'inherit',
  color: C.textPrimary,
};

interface Props {
  username: string;
  isAdmin: boolean;
  serverUrl: string;
  onLogout: () => void;
  onShowChangePassword: () => void;
  onShowFeatureFlags: () => void;
}

export function AvatarMenu({ username, isAdmin, serverUrl, onLogout, onShowChangePassword, onShowFeatureFlags }: Props) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [open]);

  return (
    <div ref={ref} style={{ position: 'relative', flexShrink: 0 }}>
      <div
        onClick={() => setOpen(o => !o)}
        style={{
          display: 'flex', alignItems: 'center', gap: 7, background: C.bgPanel,
          borderRadius: 20, padding: '5px 11px 5px 7px', cursor: 'pointer',
          minWidth: 0, maxWidth: 220, overflow: 'hidden',
        }}
      >
        <div style={{
          width: 22, height: 22, borderRadius: '50%', background: C.accent,
          color: C.onAccent, fontSize: 11, fontWeight: 700,
          display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
        }}>
          {username ? username.slice(0, 2).toUpperCase() : 'ME'}
        </div>
        <ConnectionStatus variant="badge" label={serverUrl || 'localhost'} />
      </div>

      {open && (
        <div style={{
          position: 'absolute', top: 'calc(100% + 6px)', right: 0,
          background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
          boxShadow: SHADOW.dropdown, zIndex: Z.dropdown,
          minWidth: 190, overflow: 'hidden', padding: '4px 0',
        }}>
          <div style={{
            padding: '8px 14px 6px', fontSize: 12, color: C.textMuted,
            borderBottom: `1px solid ${C.borderLight}`, marginBottom: 4,
          }}>
            <span style={{ fontWeight: 600, color: C.textHeading }}>{username}</span>
            {isAdmin && (
              <span style={{ marginLeft: 6, fontSize: 11, color: C.accent }}>admin</span>
            )}
          </div>
          <button
            onClick={() => { setOpen(false); onShowChangePassword(); }}
            style={dropdownItem}
          >
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8">
              <rect x="3" y="11" width="18" height="11" rx="2" ry="2"/>
              <path d="M7 11V7a5 5 0 0 1 10 0v4"/>
            </svg>
            Сменить пароль
          </button>
          <button
            onClick={() => { setOpen(false); onShowFeatureFlags(); }}
            style={dropdownItem}
          >
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8">
              <path d="M9 3h6M10 3v6.5L5.5 17a2 2 0 0 0 1.7 3h9.6a2 2 0 0 0 1.7-3L14 9.5V3"/>
              <path d="M7.5 14h9"/>
            </svg>
            Экспериментальные функции
          </button>
          <button
            onClick={() => { setOpen(false); onLogout(); }}
            style={{ ...dropdownItem, color: C.danger }}
          >
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8">
              <path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"/>
              <polyline points="16 17 21 12 16 7"/>
              <line x1="21" y1="12" x2="9" y2="12"/>
            </svg>
            Выйти
          </button>
        </div>
      )}
    </div>
  );
}
