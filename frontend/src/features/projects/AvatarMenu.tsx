import { useEffect, useRef, useState } from 'react';
import { C, R, SHADOW, Z } from '../../lib/design';
import { ConnectionStatus } from '../../components/ConnectionStatus';
import { SegmentedControl } from '../../components/ui';
import { useThemeMode, setThemeMode, type ThemeMode } from '../../lib/themeMode';
import { History, Book, Gauge, Users, Lock, FlaskConical, LogOut } from 'lucide-react';
import { ICON_SIZE } from '../../components/ui/icons';

const THEME_OPTIONS: { value: ThemeMode; label: string }[] = [
  { value: 'light', label: 'Светлая' },
  { value: 'dark', label: 'Тёмная' },
  { value: 'system', label: 'Системная' },
];

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
  onShowUserManagement: () => void;
  // На мобилке URL-бейдж распирает шапку — прячем, оставляя только аватар
  hideStatus?: boolean;
  // «Что нового» в меню (на мобилке, где кнопка убрана из шапки). undefined — пункт не показывать
  onShowHistory?: () => void;
  historyBadge?: number;       // число новых изменений с последнего захода
  historyNeverSeen?: boolean;  // ещё ни разу не открывал историю — точка без числа
  // «Знания» (настройка баз знаний Dify) — раздел убран из хаб-таббара, вызов в меню аватара.
  // undefined — пункт не показывать
  onOpenKnowledge?: () => void;
  // «Использование» (лимиты подписок Claude, баланс провайдеров) — вызов из меню аватара
  onShowUsage?: () => void;
}

export function AvatarMenu({ username, isAdmin, serverUrl, onLogout, onShowChangePassword, onShowFeatureFlags, onShowUserManagement, hideStatus, onShowHistory, historyBadge = 0, historyNeverSeen = false, onOpenKnowledge, onShowUsage }: Props) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const themeMode = useThemeMode();

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
        {!hideStatus && <ConnectionStatus variant="badge" label={serverUrl || 'localhost'} />}
      </div>

      {/* Индикатор новизны «Что нового» на аватаре (мобилка: кнопка уехала в меню) */}
      {onShowHistory && (historyBadge > 0 || historyNeverSeen) && (
        <span style={{
          position: 'absolute', top: -2, right: -2, pointerEvents: 'none',
          ...(historyBadge > 0
            ? {
                minWidth: 15, height: 15, padding: '0 4px', borderRadius: 8,
                background: C.accent, color: C.onAccent, fontSize: 9.5, fontWeight: 700,
                lineHeight: '15px', textAlign: 'center', boxSizing: 'border-box',
              }
            : { width: 8, height: 8, borderRadius: '50%', background: C.accent }),
        }}>
          {historyBadge > 0 ? (historyBadge > 99 ? '99+' : historyBadge) : ''}
        </span>
      )}

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
          {onShowHistory && (
            <button
              onClick={() => { setOpen(false); onShowHistory(); }}
              style={dropdownItem}
            >
              <History size={ICON_SIZE.xs} strokeWidth={2} />
              Что нового
              {(historyBadge > 0 || historyNeverSeen) && (
                <span style={{
                  marginLeft: 'auto',
                  ...(historyBadge > 0
                    ? {
                        minWidth: 18, height: 18, padding: '0 5px', borderRadius: 9,
                        background: C.accent, color: C.onAccent, fontSize: 11, fontWeight: 700,
                        lineHeight: '18px', textAlign: 'center', boxSizing: 'border-box',
                      }
                    : { width: 8, height: 8, borderRadius: '50%', background: C.accent }),
                }}>
                  {historyBadge > 0 ? (historyBadge > 99 ? '99+' : historyBadge) : ''}
                </span>
              )}
            </button>
          )}
          {onOpenKnowledge && (
            <button
              onClick={() => { setOpen(false); onOpenKnowledge(); }}
              style={dropdownItem}
            >
              <Book size={ICON_SIZE.xs} strokeWidth={2} />
              Настройка знаний
            </button>
          )}
          {onShowUsage && (
            <button
              onClick={() => { setOpen(false); onShowUsage(); }}
              style={dropdownItem}
            >
              <Gauge size={ICON_SIZE.xs} strokeWidth={2} />
              Использование
            </button>
          )}
          {isAdmin && (
            <button
              onClick={() => { setOpen(false); onShowUserManagement(); }}
              style={dropdownItem}
            >
              <Users size={ICON_SIZE.xs} strokeWidth={2} />
              Управление пользователями
            </button>
          )}
          <button
            onClick={() => { setOpen(false); onShowChangePassword(); }}
            style={dropdownItem}
          >
            <Lock size={ICON_SIZE.xs} strokeWidth={2} />
            Сменить пароль
          </button>
          <button
            onClick={() => { setOpen(false); onShowFeatureFlags(); }}
            style={dropdownItem}
          >
            <FlaskConical size={ICON_SIZE.xs} strokeWidth={2} />
            Экспериментальные функции
          </button>
          {/* Оформление: светлая / тёмная / системная тема */}
          <div style={{
            padding: '10px 14px 12px', margin: '4px 0',
            borderTop: `1px solid ${C.borderLight}`,
            borderBottom: `1px solid ${C.borderLight}`,
          }}>
            <div style={{
              fontSize: 12, fontWeight: 600, color: C.textMuted, marginBottom: 8,
            }}>
              Оформление
            </div>
            <SegmentedControl<ThemeMode>
              value={themeMode}
              options={THEME_OPTIONS}
              onChange={setThemeMode}
            />
          </div>
          <button
            onClick={() => { setOpen(false); onLogout(); }}
            style={{ ...dropdownItem, color: C.danger }}
          >
            <LogOut size={ICON_SIZE.xs} strokeWidth={2} />
            Выйти
          </button>
        </div>
      )}
    </div>
  );
}
