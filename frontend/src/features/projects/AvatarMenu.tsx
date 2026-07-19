import { useEffect, useRef, useState } from 'react';
import { C, R, SHADOW, Z } from '../../lib/design';
import { ConnectionStatus } from '../../components/ConnectionStatus';
import { SegmentedControl } from '../../components/ui';
import { useThemeMode, setThemeMode, type ThemeMode } from '../../lib/themeMode';
import { History, Book, Gauge, Users, Lock, FlaskConical, LogOut, Mic } from 'lucide-react';
import { ICON_SIZE } from '../../components/ui/icons';
import { isMicKeyboardFallback, clearMicKeyboardFallback } from '../../lib/voiceInput';
import { showToast } from '../../lib/toast';

const THEME_OPTIONS: { value: ThemeMode; label: string }[] = [
  { value: 'light', label: 'Светлая' },
  { value: 'dark', label: 'Тёмная' },
  { value: 'system', label: 'Системная' },
];

// Разделитель между смысловыми группами пунктов меню
function MenuDivider() {
  return <div style={{ height: 1, background: C.borderLight, margin: '4px 0' }} />;
}

const dropdownItem: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 9,
  width: '100%', textAlign: 'left', padding: '8px 14px',
  background: 'none', border: 'none', cursor: 'pointer',
  fontSize: 13.5, fontWeight: 500, fontFamily: 'inherit',
  color: C.textPrimary,
};

interface Props {
  username: string;
  // Имя из профиля («Григорий»); пусто — обходимся логином
  displayName?: string;
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
  historyActive?: boolean;     // страница «Что нового» открыта — подсвечиваем пункт
  // «Знания» (настройка баз знаний Dify) — раздел убран из хаб-таббара, вызов в меню аватара.
  // undefined — пункт не показывать
  onOpenKnowledge?: () => void;
  // «Использование» (лимиты подписок Claude, баланс провайдеров) — вызов из меню аватара
  onShowUsage?: () => void;
}

export function AvatarMenu({ username, displayName, isAdmin, serverUrl, onLogout, onShowChangePassword, onShowFeatureFlags, onShowUserManagement, hideStatus, onShowHistory, historyBadge = 0, historyNeverSeen = false, historyActive = false, onOpenKnowledge, onShowUsage }: Props) {
  // Как обращаемся к пользователю; логин остаётся видимым отдельной строкой,
  // чтобы было понятно, под каким аккаунтом сидишь
  const name = displayName?.trim() || username;
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const themeMode = useThemeMode();
  // Флаг мог подняться, пока меню закрыто — перечитываем на каждом открытии
  const [micFallback, setMicFallback] = useState(false);

  const toggleOpen = () => {
    setOpen(o => {
      if (!o) setMicFallback(isMicKeyboardFallback());
      return !o;
    });
  };

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
        onClick={toggleOpen}
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
          {name ? name.slice(0, 2).toUpperCase() : 'ME'}
        </div>
        {!hideStatus && <ConnectionStatus variant="badge" label={serverUrl || 'localhost'} />}
      </div>

      {/* Сам аватар индикатором новизны не обвешиваем: счётчик «Что нового»
          показывается только на одноимённом пункте внутри меню */}

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
            <span style={{ fontWeight: 600, color: C.textHeading }}>{name}</span>
            {isAdmin && (
              <span style={{ marginLeft: 6, fontSize: 11, color: C.accent }}>admin</span>
            )}
            {/* Логин показываем, только если он отличается от имени — иначе дубль */}
            {name !== username && username && (
              <div style={{ fontSize: 11, color: C.textMuted, marginTop: 2 }}>{username}</div>
            )}
          </div>
          {onOpenKnowledge && (
            <button
              onClick={() => { setOpen(false); onOpenKnowledge(); }}
              style={dropdownItem}
            >
              <Book size={ICON_SIZE.xs} strokeWidth={2} />
              Знания
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
          <MenuDivider />
          {isAdmin && (
            <button
              onClick={() => { setOpen(false); onShowUserManagement(); }}
              style={dropdownItem}
            >
              <Users size={ICON_SIZE.xs} strokeWidth={2} />
              Пользователи
            </button>
          )}
          <button
            onClick={() => { setOpen(false); onShowChangePassword(); }}
            style={dropdownItem}
          >
            <Lock size={ICON_SIZE.xs} strokeWidth={2} />
            Сменить пароль
          </button>
          <MenuDivider />
          <button
            onClick={() => { setOpen(false); onShowFeatureFlags(); }}
            style={dropdownItem}
          >
            <FlaskConical size={ICON_SIZE.xs} strokeWidth={2} />
            Эксперименты
          </button>
          {onShowHistory && (
            <button
              onClick={() => { setOpen(false); onShowHistory(); }}
              style={historyActive ? { ...dropdownItem, color: C.accent } : dropdownItem}
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
          {/* Виден только когда голосовой ввод свалился в клавиатурный режим —
              сбрасывает флаг, чтобы кнопка микрофона снова пробовала распознавание */}
          {micFallback && (
            <button
              onClick={() => {
                clearMicKeyboardFallback();
                setMicFallback(false);
                setOpen(false);
                showToast('Голосовой ввод',
                  'Распознавание речи включено обратно. Нажми микрофон в поле ввода и проверь.');
              }}
              style={dropdownItem}
            >
              <Mic size={ICON_SIZE.xs} strokeWidth={2} />
              Вернуть голосовой ввод
            </button>
          )}
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
