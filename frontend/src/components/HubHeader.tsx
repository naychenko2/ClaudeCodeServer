import { useState, useEffect } from 'react';
import type { AuthState } from '../types';
import { C, FONT, TB, SHADOW } from '../lib/design';
import { HubTabs, type HubTab } from './HubTabs';
import { AvatarMenu } from '../features/projects/AvatarMenu';
import { UserManagementModal } from './UserManagementModal';
import { ChangePasswordDialog } from './ChangePasswordDialog';
import { FeatureFlagsModal } from './FeatureFlagsModal';
import { api } from '../lib/api';
import { useFeature, FLAGS } from '../lib/featureFlags';
import { openNoteById } from '../features/notes/saveToNote';
import { GlobalSearch } from './GlobalSearch';

// Локальная дата устройства в формате YYYY-MM-DD (для дневника брифа)
function localDateStr(): string {
  const d = new Date();
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

interface Props {
  value: HubTab;
  onTab: (t: HubTab) => void;
  auth: AuthState;
  onLogout: () => void;
}

// Событие открытия продуктовой истории — слушает App (overlay на верхнем уровне)
export const PRODUCT_HISTORY_EVENT = 'open-product-history';
// Метка «просмотрено» для бейджа — ISO-время последнего открытия истории.
// Ключ привязан к пользователю (userId), чтобы на одном устройстве у разных аккаунтов
// была своя отметка. Без userId (не залогинен) — общий базовый ключ.
const PRODUCT_HISTORY_SEEN_BASE = 'cc_product_history_seen';
export const productHistorySeenKey = (userId?: string | null) =>
  userId ? `${PRODUCT_HISTORY_SEEN_BASE}_${userId}` : PRODUCT_HISTORY_SEEN_BASE;

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
  // Бейдж: число новых изменений с последнего захода. Особый случай — пользователь
  // ещё ни разу не открывал историю (нет метки): показываем точку без числа
  // («тут есть что-то новенькое, загляни»).
  const [historyBadge, setHistoryBadge] = useState(0);
  const [neverSeen, setNeverSeen] = useState(false);
  const [showHistoryTip, setShowHistoryTip] = useState(false);   // кастомный tooltip кнопки «Что нового»
  const historyTip = (historyBadge ?? 0) > 0
    ? `Что нового (${historyBadge! > 99 ? '99+' : historyBadge})`
    : neverSeen ? 'Что нового — есть свежее' : 'Что нового';
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

  // «Утренний бриф» (флаг daily-briefing): собрать план дня в дневник и открыть его
  const briefingEnabled = useFeature(FLAGS.aiAssist);
  const [briefingBusy, setBriefingBusy] = useState(false);
  // Единый поиск (флаг unified-search)
  const searchEnabled = useFeature(FLAGS.aiAssist);
  const [showSearch, setShowSearch] = useState(false);
  const runBriefing = async () => {
    if (briefingBusy) return;
    setBriefingBusy(true);
    try {
      const note = await api.briefing.today(localDateStr());
      openNoteById(note.id);
    } catch {
      alert('Не удалось собрать бриф. Попробуйте ещё раз.');
    } finally {
      setBriefingBusy(false);
    }
  };

  const logo = (
    <div style={{ display: 'flex', alignItems: 'center', gap: 10, minWidth: 0 }}>
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
      {/* Левая секция — логотип (скрыт на мобилке; там распорка не нужна — иначе
          4 вкладки не помещаются: правая секция не сжимается меньше аватара) */}
      {!isMobile && (
        <div style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'center' }}>
          {logo}
        </div>
      )}

      {/* Центр — переключатель вкладок. На мобиле — компакт-режим (иконки, подпись
          у активного): 4 раздела помещаются даже на 320px без скролла и обрезания */}
      {isMobile ? (
        <div style={{ flex: 1, minWidth: 0, display: 'flex', justifyContent: 'center', overflow: 'hidden' }}>
          <HubTabs mobile value={value} onChange={onTab} />
        </div>
      ) : (
        <HubTabs value={value} onChange={onTab} />
      )}

      {/* Правая секция — меню аватара (управление пользователями — внутри меню, admin) */}
      <div style={{ flex: isMobile ? 'none' : 1, minWidth: 0, display: 'flex', alignItems: 'center', justifyContent: 'flex-end', gap: 8 }}>
        {/* Единый поиск (флаг unified-search) — заметки + задачи. Только десктоп. */}
        {!isMobile && searchEnabled && (
          <button
            onClick={() => setShowSearch(true)}
            aria-label="Поиск по пространству"
            title="Поиск по заметкам и задачам"
            style={{
              width: 32, height: 32, borderRadius: 8, border: 'none', background: 'none',
              color: C.textSecondary, cursor: 'pointer', display: 'flex', alignItems: 'center',
              justifyContent: 'center', flexShrink: 0,
            }}
            onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
            onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}
          >
            <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <circle cx="11" cy="11" r="7" />
              <path d="m21 21-4.3-4.3" />
            </svg>
          </button>
        )}
        {/* «Утренний бриф» — план дня в дневник (флаг daily-briefing). Как и «Что нового»,
            только на десктопе (на мобилке в шапке нет места). */}
        {!isMobile && briefingEnabled && (
          <button
            onClick={runBriefing}
            disabled={briefingBusy}
            aria-label="Собрать утренний бриф"
            title="Утренний бриф — план дня в дневник"
            style={{
              position: 'relative', width: 32, height: 32, borderRadius: 8, border: 'none',
              background: 'none', color: C.textSecondary, cursor: briefingBusy ? 'default' : 'pointer',
              display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
              opacity: briefingBusy ? 0.6 : 1,
            }}
            onMouseEnter={e => { if (!briefingBusy) e.currentTarget.style.background = C.bgSelected; }}
            onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}
          >
            <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"
              style={briefingBusy ? { animation: 'cc-spin 1s linear infinite' } : undefined}>
              <circle cx="12" cy="12" r="4" />
              <path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" />
            </svg>
          </button>
        )}
        {/* «Что нового» — продуктовая история по всем проектам (во всех разделах).
            На мобилке кнопка наезжала бы на контент — там она уезжает в меню аватара. */}
        {!isMobile && (
          <button
            onClick={openHistory}
            aria-label={historyTip}
            style={{
              position: 'relative', width: 32, height: 32, borderRadius: 8, border: 'none',
              background: 'none', color: C.textSecondary, cursor: 'pointer',
              display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
            }}
            onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; setShowHistoryTip(true); }}
            onMouseLeave={e => { e.currentTarget.style.background = 'none'; setShowHistoryTip(false); }}
          >
            <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8" />
              <path d="M3 3v5h5" />
              <polyline points="12 7 12 12 15 14" />
            </svg>
            {(historyBadge ?? 0) > 0 ? (
              <span style={{
                position: 'absolute', top: -3, right: -5, minWidth: 15, height: 15,
                padding: '0 4px', borderRadius: 8, background: C.accent, color: C.onAccent,
                fontSize: 9.5, fontWeight: 700, lineHeight: '15px', textAlign: 'center',
                boxSizing: 'border-box', pointerEvents: 'none',
              }}>
                {historyBadge! > 99 ? '99+' : historyBadge}
              </span>
            ) : neverSeen && (
              // Ещё ни разу не открывал — точка без числа: «тут есть что-то новенькое»
              <span style={{
                position: 'absolute', top: 0, right: -1, width: 8, height: 8,
                borderRadius: '50%', background: C.accent, pointerEvents: 'none',
              }} />
            )}
            {/* Кастомный tooltip в стиле приложения (вместо системного title) */}
            {showHistoryTip && (
              <span style={{
                position: 'absolute', top: 'calc(100% + 7px)', right: 0, zIndex: 200,
                background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: 8,
                boxShadow: SHADOW.dropdown, padding: '5px 10px',
                fontSize: 12, fontWeight: 500, color: C.textHeading, whiteSpace: 'nowrap',
                fontFamily: FONT.sans, pointerEvents: 'none',
              }}>
                {historyTip}
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
          // На мобилке «Что нового» переезжает в меню (в шапке нет места); индикатор
          // новизны переносим на аватар и в пункт меню, чтобы он не потерялся
          onShowHistory={isMobile ? openHistory : undefined}
          historyBadge={historyBadge}
          historyNeverSeen={neverSeen}
        />
      </div>

      {showSearch && <GlobalSearch onClose={() => setShowSearch(false)} />}
      {showUserMgmt && <UserManagementModal currentUserId={auth.id} onClose={() => setShowUserMgmt(false)} />}
      {showChangePassword && <ChangePasswordDialog onClose={() => setShowChangePassword(false)} />}
      {showFeatureFlags && <FeatureFlagsModal onClose={() => setShowFeatureFlags(false)} />}
    </div>
  );
}
