import { useEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from 'react';
import { C, FS, R, SHADOW, Z } from '../../lib/design';
import { ConnectionStatus } from '../../components/ConnectionStatus';
import { SegmentedControl } from '../../components/ui';
import { useThemeMode, setThemeMode, type ThemeMode } from '../../lib/themeMode';
import { Bell, History, Book, BriefcaseBusiness, Gauge, Users, Lock, FlaskConical, LogOut, Mic, Coins, MonitorSmartphone, Palette, Plug, Rocket, SquareDashedMousePointer } from 'lucide-react';
import { ICON_SIZE } from '../../components/ui/icons';
import { isMicKeyboardFallback, clearMicKeyboardFallback } from '../../lib/voiceInput';
import { showToast } from '../../lib/toast';
import { toggleUiInspector, useUiInspector } from '../../lib/uiInspector';
import { buildStamp } from '../../lib/buildInfo';
import { useConnectionDisplayState } from '../../hooks/useConnectionDisplayState';

const THEME_OPTIONS: { value: ThemeMode; label: string }[] = [
  { value: 'light', label: 'Светлая' },
  { value: 'dark', label: 'Тёмная' },
  { value: 'system', label: 'Системная' },
];

// Видимый фокус триггера — кольцо по токену SHADOW.focus (как IconButton), пилюля
// остаётся <div> с role="button", для псевдокласса :focus-visible нужен CSS-класс.
const FOCUS_CLASS = 'cc-avatarmenu';
if (typeof document !== 'undefined' && !document.getElementById('cc-avatarmenu-style')) {
  const el = document.createElement('style');
  el.id = 'cc-avatarmenu-style';
  el.textContent = `.${FOCUS_CLASS}:focus-visible{outline:none;box-shadow:${SHADOW.focus};}`;
  document.head.appendChild(el);
}

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
  // «Специальности» (справочник ролей персон) — раздел-таб без вкладки, вызов из
  // меню аватара. undefined — пункт не показывать
  onOpenSpecialties?: () => void;
  // «Модели и расход» — единый раздел вместо прежних «Использование» и «Поставщики моделей»
  // (редизайн models-spend, этап 1). undefined — пункт не показывать
  onShowModelsSpend?: () => void;
  // «Аналитика токенов» (расход по ходам/моделям/проектам) — раздел-таб без вкладки,
  // вызов из меню аватара. undefined — пункт не показывать
  onOpenSpend?: () => void;
  // «MCP-серверы» (личный реестр внешних инструментов) — соседний пункт; за фич-флагом
  // mcp-registry, поэтому HubHeader передаёт колбэк только при включённом флаге
  onShowMcpServers?: () => void;
  // «Уведомления» — на планшете колокольчик уходит из шапки в меню аватара,
  // счётчик непрочитанных — на самом аватаре числом. undefined — пункт не показывать,
  // бейдж на аватаре не рисуется (десктоп и мобилка: колокольчик остаётся в шапке,
  // дубля не возникает).
  onOpenNotifications?: () => void;
  notifBadge?: number;
  notifActive?: boolean;
  // «Устройства» — компьютеры, которым можно отдать руки в десктопном чате (ADR-008).
  // За фич-флагом desktop-agent, поэтому HubHeader передаёт колбэк не всегда
  onShowDevices?: () => void;
  // «Выкатить на бой» — публикация продукта трей-раннером. Пункт только для админов И только
  // когда фича включена в конфиге сервера, поэтому HubHeader передаёт колбэк не всегда
  onShowDeploy?: () => void;
}

export function AvatarMenu({ username, displayName, isAdmin, serverUrl, onLogout, onShowChangePassword, onShowFeatureFlags, onShowUserManagement, hideStatus, onShowHistory, historyBadge = 0, historyNeverSeen = false, historyActive = false, onOpenKnowledge, onOpenSpecialties, onShowModelsSpend, onOpenSpend, onShowMcpServers, onShowDevices, onShowDeploy, onOpenNotifications, notifBadge = 0, notifActive = false }: Props) {
  // Как обращаемся к пользователю; логин остаётся видимым отдельной строкой,
  // чтобы было понятно, под каким аккаунтом сидишь
  const name = displayName?.trim() || username;
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLDivElement>(null);
  const themeMode = useThemeMode();
  // Флаг мог подняться, пока меню закрыто — перечитываем на каждом открытии
  const [micFallback, setMicFallback] = useState(false);
  // Режим UI-инспектора — подсветка пункта, пока режим включён
  const inspectorOn = useUiInspector();
  // Маркер связи: 'online' (сплошное кольцо success), 'unstable' (пунктир warning
  // с пульсом — после 3с устойчивой потери), 'offline' (grayscale + диагональ —
  // после 10с от потери). Возврат — мгновенный, без промежуточных.
  const connection = useConnectionDisplayState();
  const connectionLabel = connection === 'online'
    ? 'В сети'
    : connection === 'unstable'
      ? 'Проблемы со связью'
      : 'Офлайн';

  const toggleOpen = () => {
    setOpen(o => {
      if (!o) setMicFallback(isMicKeyboardFallback());
      return !o;
    });
  };

  // Enter/Space открывают меню; Space без preventDefault скроллит страницу
  const handleKeyDown = (e: ReactKeyboardEvent<HTMLDivElement>) => {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      toggleOpen();
    }
  };

  useEffect(() => {
    if (!open) return;
    const onMouseDown = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        setOpen(false);
        // Возвращаем фокус на триггер — иначе он провалится в body, когда
        // пользователь нажал Esc, находясь на пункте меню
        buttonRef.current?.focus();
      }
    };
    document.addEventListener('mousedown', onMouseDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('mousedown', onMouseDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [open]);

  return (
    <div ref={ref} style={{ position: 'relative', flexShrink: 0 }}>
      <div
        ref={buttonRef}
        className={FOCUS_CLASS}
        onClick={toggleOpen}
        onKeyDown={handleKeyDown}
        role="button"
        tabIndex={0}
        aria-label={
          (notifBadge > 0
            ? `Меню пользователя, ${notifBadge > 99 ? '99+' : notifBadge} непрочитанных уведомлений`
            : 'Меню пользователя')
          + `, ${connectionLabel}`
        }
        style={{
          display: 'flex', alignItems: 'center', gap: 7, background: C.bgPanel,
          borderRadius: 20, padding: hideStatus ? 6 : '6px 11px 6px 7px', cursor: 'pointer',
          minWidth: 0, maxWidth: 220, overflow: 'hidden',
        }}
      >
        {/* Маркер связи: пассивный визуал у аватарки вместо тоста «Связь восстановлена».
            Обёртка 28×28 (запас под 2px-кольцо + 1px обводка кольца); аватар 22×22;
            кольцо и черта — абсолютно позиционированы. Title читается при ховере,
            status для скринридера добавлен в aria-label самого триггера (выше), чтобы
            не дублировать структурой. Тач-цель триггера: 6px padding + 28px маркер
            = 40px (порог из гайда для мобильного тапа). */}
        <div
          title={connectionLabel}
          style={{
            position: 'relative', width: 28, height: 28,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            flexShrink: 0,
          }}
        >
          <div style={{
            width: 22, height: 22, borderRadius: R.full, background: C.accent,
            color: C.onAccent, fontSize: FS.xs, fontWeight: 700,
            display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
            filter: connection === 'offline' ? 'grayscale(100%)' : 'none',
            opacity: connection === 'offline' ? 0.6 : 1,
            transition: 'filter 0.2s ease, opacity 0.2s ease',
          }}>
            {name ? name.slice(0, 2).toUpperCase() : 'ME'}
          </div>
          {connection !== 'offline' && (
            // Кольцо: 2px цветной border + 1px обводка в C.bgPanel снаружи — отделяет
            // кольцо от любого фона под пилюлей (между C.warning/C.success и C.accent
            // аватаром контраст недостаточный на самих участках пересечения).
            <div aria-hidden style={{
              position: 'absolute', inset: 0, borderRadius: R.full,
              border: `2px solid ${connection === 'unstable' ? C.warning : C.success}`,
              borderStyle: connection === 'unstable' ? 'dashed' : 'solid',
              boxShadow: `0 0 0 1px ${C.bgPanel}`,
              animation: connection === 'unstable' ? 'cc-conn-pulse 1.4s ease-in-out infinite' : 'none',
              pointerEvents: 'none',
            }} />
          )}
          {connection === 'offline' && (
            // Черта: C.textHeading вместо C.textMuted — на grayscale+opacity:0.6 аватаре
            // textMuted даёт контраст ~1.3:1, черта не читается. textHeading читается
            // в обеих темах (тёмный на светлом круге, светлый на тёмном).
            <div aria-hidden style={{
              position: 'absolute', top: '50%', left: '-12%', width: '124%', height: 1.5,
              background: C.textHeading, transform: 'translateY(-50%) rotate(-45deg)',
              pointerEvents: 'none', borderRadius: 1,
            }} />
          )}
        </div>
        {!hideStatus && <ConnectionStatus variant="badge" label={serverUrl || 'localhost'} />}
      </div>

      {/* Бейдж непрочитанных на самом аватаре — числом (планшет, где колокольчик
          ушёл из шапки). Геометрия как у колокольчика: position:absolute у ВНЕШНЕЙ
          обёртки (у самой пилюли overflow:hidden под обрезку подписи сервера —
          бейдж внутри срежется). Показ: передан onOpenNotifications И есть
          что показать (>0). На десктопе/мобиле, где колокольчик в шапке, onOpenNotifications
          не передаётся — дубля не возникает. */}
      {onOpenNotifications && notifBadge > 0 && (
        <span aria-hidden style={{
          position: 'absolute', top: -3, right: -5, minWidth: 15, height: 15,
          padding: '0 4px', borderRadius: 8, background: C.accent, color: C.onAccent,
          fontSize: 9.5, fontWeight: 700, lineHeight: '15px', textAlign: 'center',
          boxSizing: 'border-box', pointerEvents: 'none',
        }}>
          {notifBadge > 99 ? '99+' : notifBadge}
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
            <span style={{ fontWeight: 600, color: C.textHeading }}>{name}</span>
            {isAdmin && (
              <span style={{ marginLeft: 6, fontSize: 11, color: C.accent }}>admin</span>
            )}
            {/* Логин показываем, только если он отличается от имени — иначе дубль */}
            {name !== username && username && (
              <div style={{ fontSize: 11, color: C.textMuted, marginTop: 2 }}>{username}</div>
            )}
          </div>
          {onOpenNotifications && (
            <button
              onClick={() => { setOpen(false); onOpenNotifications(); }}
              style={notifActive ? { ...dropdownItem, color: C.accent } : dropdownItem}
            >
              <Bell size={ICON_SIZE.xs} strokeWidth={2} />
              Уведомления
              {notifBadge > 0 && (
                <span style={{
                  marginLeft: 'auto',
                  minWidth: 18, height: 18, padding: '0 5px', borderRadius: 9,
                  background: C.accent, color: C.onAccent, fontSize: 11, fontWeight: 700,
                  lineHeight: '18px', textAlign: 'center', boxSizing: 'border-box',
                }}>
                  {notifBadge > 99 ? '99+' : notifBadge}
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
              Знания
            </button>
          )}
          {onOpenSpecialties && (
            <button
              onClick={() => { setOpen(false); onOpenSpecialties(); }}
              style={dropdownItem}
            >
              <BriefcaseBusiness size={ICON_SIZE.xs} strokeWidth={2} />
              Специальности
            </button>
          )}
          {onShowModelsSpend && (
            <button
              onClick={() => { setOpen(false); onShowModelsSpend(); }}
              style={dropdownItem}
            >
              <Gauge size={ICON_SIZE.xs} strokeWidth={2} />
              Модели и расход
            </button>
          )}
          {onOpenSpend && (
            <button
              onClick={() => { setOpen(false); onOpenSpend(); }}
              style={dropdownItem}
            >
              <Coins size={ICON_SIZE.xs} strokeWidth={2} />
              Аналитика токенов
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
          {onShowMcpServers && (
            <button
              onClick={() => { setOpen(false); onShowMcpServers(); }}
              style={dropdownItem}
            >
              <Plug size={ICON_SIZE.xs} strokeWidth={2} />
              MCP-серверы
            </button>
          )}
          {onShowDevices && (
            <button
              onClick={() => { setOpen(false); onShowDevices(); }}
              style={dropdownItem}
            >
              <MonitorSmartphone size={ICON_SIZE.xs} strokeWidth={2} />
              Устройства
            </button>
          )}
          <button
            onClick={() => { setOpen(false); onShowChangePassword(); }}
            style={dropdownItem}
          >
            <Lock size={ICON_SIZE.xs} strokeWidth={2} />
            Сменить пароль
          </button>
          {onShowDeploy && (
            <button
              onClick={() => { setOpen(false); onShowDeploy(); }}
              style={dropdownItem}
            >
              <Rocket size={ICON_SIZE.xs} strokeWidth={2} />
              Выкатить на бой
            </button>
          )}
          <MenuDivider />
          <button
            onClick={() => { setOpen(false); onShowFeatureFlags(); }}
            style={dropdownItem}
          >
            <FlaskConical size={ICON_SIZE.xs} strokeWidth={2} />
            Эксперименты
          </button>
          {import.meta.env.DEV && (
            <button
              onClick={() => { setOpen(false); window.open(`${window.location.pathname}#/ui-kit`, '_blank'); }}
              style={dropdownItem}
            >
              <Palette size={ICON_SIZE.xs} strokeWidth={2} />
              Витрина-дизайна
            </button>
          )}
          {/* Инспектор UI (admin-only): тумблер зовёт модульный стор напрямую —
              оверлей монтирует App, цепочка пропсов не нужна */}
          {isAdmin && (
            <button
              onClick={() => { setOpen(false); toggleUiInspector(); }}
              title="Ctrl+Alt+I"
              style={inspectorOn ? { ...dropdownItem, color: C.accent } : dropdownItem}
            >
              <SquareDashedMousePointer size={ICON_SIZE.xs} strokeWidth={2} />
              Инспектор UI
            </button>
          )}
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
          {/* Метка сборки: чтобы «старый бандл» не приняли за «фикс не сделан» —
              сравнивается со временем правки исходников (lib/buildInfo.ts) */}
          <div style={{
            padding: '8px 14px 10px',
            fontSize: 11,
            color: C.textMuted,
            textAlign: 'center',
            userSelect: 'text',
          }}>
            {buildStamp()}
          </div>
        </div>
      )}
    </div>
  );
}
