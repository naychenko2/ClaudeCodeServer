import { useEffect, useState, useCallback } from 'react';
import { Bell, CheckCheck, Search, Trash2, Columns, SlidersHorizontal, Folder } from 'lucide-react';
import { C, FONT, FS, R, SP, SHADOW } from '../../lib/design';
import { NotificationAvatar, hasPersona, notifAccentColor } from './NotificationAvatar';
import { HubHeader } from '../../components/HubHeader';
import { ConfirmDialog } from '../../components/ui';
import { ToolbarOverflowMenu, type OverflowItem } from '../../components/ToolbarOverflowMenu';
import { useIsMobile } from '../../lib/breakpoints';
import type { HubTabValue } from '../../components/HubTabs';
import type { AuthState } from '../../types';
import type { NotificationItem } from '../../types';
import {
  loadNotifications,
  getNotifications,
  getUnreadCount,
  subscribeToNotifications,
  ensureNotificationsSubscribed,
  markRead,
  markAllRead,
  deleteNotification,
  deleteReadAll,
} from '../../lib/notifications';
import { AgentKanban } from '../agent-kanban/AgentKanban';
import { KIND_META, KIND_LABELS, eventContext, formatTime, openNotificationUrl } from './kindMeta';

function dateGroupKey(iso: string) {
  const d = new Date(iso);
  const now = new Date();
  const diff = now.getTime() - d.getTime();
  const day = 86400000;
  if (diff < day) return 'Сегодня';
  if (diff < 2 * day) return 'Вчера';
  if (diff < 7 * day) return 'На этой неделе';
  return 'Ранее';
}

// ====== NotificationCard ======
function NotificationCard({ item, onRead, onDelete }: {
  item: NotificationItem;
  onRead: (id: string) => void;
  onDelete: (id: string) => void;
}) {
  const meta = KIND_META[item.kind] ?? KIND_META.info;
  const isPersona = hasPersona(item);
  const senderColor = notifAccentColor(item, item.kind);   // цвет персоны (hex) или вида
  // Лёгкая тонировка шапки: цвет персоны с alpha (только валидный hex), иначе фон вида
  const headBg = isPersona
    ? (senderColor.startsWith('#') ? senderColor + '14' : C.bgInset)
    : meta.bg;
  const context = eventContext(item);

  const linkStyle = (color: string): React.CSSProperties => ({
    fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600,
    border: 'none', background: 'transparent', cursor: 'pointer',
    padding: '3px 7px', borderRadius: R.sm, color,
  });

  return (
    <div
      style={{
        background: C.bgWhite,
        borderRadius: R.lg,
        border: `1px solid ${item.isRead ? C.border : C.accent}`,
        boxShadow: SHADOW.card,
        overflow: 'hidden',
        transition: 'box-shadow 0.16s ease, transform 0.16s ease',
      }}
      onMouseEnter={e => { e.currentTarget.style.boxShadow = SHADOW.dropdown; e.currentTarget.style.transform = 'translateY(-1px)'; }}
      onMouseLeave={e => { e.currentTarget.style.boxShadow = SHADOW.card; e.currentTarget.style.transform = 'none'; }}
    >
      {/* Шапка-идентичность: аватар + «отправитель» и заголовок события в две строки */}
      <div style={{ display: 'flex', gap: SP.md, padding: '9px 13px', background: headBg }}>
        <NotificationAvatar
          personaId={item.personaId}
          personaName={item.personaName}
          personaColor={item.personaColor}
          kind={item.kind}
          size={34}
        />
        <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', justifyContent: 'center', gap: 1 }}>
          <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm }}>
            {isPersona ? (
              <>
                <span style={{ fontSize: FS.sm, fontWeight: 700, color: senderColor, whiteSpace: 'nowrap' }}>
                  {item.personaRole || item.personaName}
                </span>
                {item.personaRole && item.personaName && (
                  <span style={{ fontSize: FS.xs, color: C.textSecondary, whiteSpace: 'nowrap' }}>
                    {item.personaName}
                  </span>
                )}
              </>
            ) : (
              <span style={{ fontSize: FS.sm, fontWeight: 700, color: meta.color, whiteSpace: 'nowrap' }}>
                {KIND_LABELS[item.kind] ?? item.kind}
              </span>
            )}
            <span style={{
              marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 5,
              fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap',
            }}>
              {!item.isRead && <span style={{ width: 7, height: 7, borderRadius: '50%', background: C.accent }} />}
              {formatTime(item.createdAt)}
            </span>
          </div>
          <div style={{
            fontSize: FS.base, fontWeight: 600, color: C.textHeading,
            whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
          }}>
            {item.title}
          </div>
        </div>
      </div>

      {/* Тело: превью + контекст + действия, с отступом под шапкой и выравниванием под текстом */}
      <div style={{ padding: '10px 14px 11px 59px' }}>
        <div style={{
          fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5,
          display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden',
        }}>
          {item.body}
        </div>

        <div style={{ display: 'flex', alignItems: 'center', gap: SP.md, marginTop: SP.sm, flexWrap: 'wrap' }}>
          {item.projectName && (
            <span style={{
              display: 'inline-flex', alignItems: 'center', gap: SP.xs,
              padding: '1px 7px', borderRadius: R.sm,
              fontSize: FS.xs, fontWeight: 500,
              background: C.bgInset, color: C.textMuted, maxWidth: 170, overflow: 'hidden',
            }}>
              <Folder size={11} strokeWidth={2} style={{ flexShrink: 0 }} />
              <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {item.projectName}
              </span>
            </span>
          )}
          {context && <span style={{ fontSize: FS.xs, color: C.textMuted }}>{context}</span>}

          <span style={{ marginLeft: 'auto', display: 'flex', gap: SP.xs, alignItems: 'center' }}>
            {item.url && (
              <button
                style={linkStyle(C.accent)}
                onClick={() => { if (!item.isRead) onRead(item.id); openNotificationUrl(item.url!); }}
                onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
                onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
              >
                Открыть
              </button>
            )}
            {!item.isRead && (
              <button
                style={linkStyle(C.textMuted)}
                onClick={() => onRead(item.id)}
                onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; e.currentTarget.style.color = C.textPrimary; }}
                onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; e.currentTarget.style.color = C.textMuted; }}
              >
                Прочитано
              </button>
            )}
            <button
              style={linkStyle(C.textMuted)}
              onClick={() => onDelete(item.id)}
              onMouseEnter={e => { e.currentTarget.style.color = C.danger; e.currentTarget.style.background = C.dangerBg; }}
              onMouseLeave={e => { e.currentTarget.style.color = C.textMuted; e.currentTarget.style.background = 'transparent'; }}
            >
              Удалить
            </button>
          </span>
        </div>
      </div>
    </div>
  );
}

// ====== Filters ======
const FILTERS = [
  { key: 'all', label: 'Все' },
  { key: 'unread', label: 'Непрочитанные' },
  { key: 'reminder', label: '⏰ Напоминания' },
  { key: 'claude', label: '● Claude' },
  { key: 'info', label: 'ℹ Системные' },
  { key: 'success', label: '✓ Выполнено' },
];

// ====== NotificationsPage ======
export function NotificationsPage({ auth, onLogout, onHubTab }: {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTabValue) => void;
}) {
  // Режим: 'notifications' (по умолчанию) или 'dispatcher'
  const [mode, setMode] = useState<'notifications' | 'dispatcher'>('notifications');
  const [filter, setFilter] = useState('all');
  const [search, setSearch] = useState('');
  const [, forceUpdate] = useState(0);
  // Подтверждение очистки прочитанных — через ConfirmDialog вместо window.confirm
  const [confirmClear, setConfirmClear] = useState(false);
  const isMobile = useIsMobile();

  const rerender = useCallback(() => forceUpdate(n => n + 1), []);

  useEffect(() => {
    ensureNotificationsSubscribed();
    loadNotifications().then(rerender);
    return subscribeToNotifications(rerender);
  }, []);

  const notifs = getNotifications();
  const totalUnread = getUnreadCount();

  const filtered = notifs.filter(n => {
    if (filter === 'unread' && n.isRead) return false;
    if (filter !== 'all' && filter !== 'unread' && n.kind !== filter) return false;
    if (search) {
      const q = search.toLowerCase();
      if (!n.title.toLowerCase().includes(q) && !n.body.toLowerCase().includes(q)) return false;
    }
    return true;
  });

  // Group by date
  const groups: Record<string, NotificationItem[]> = {};
  filtered.forEach(n => {
    const gk = dateGroupKey(n.createdAt);
    if (!groups[gk]) groups[gk] = [];
    groups[gk].push(n);
  });

  // Мобильная разгрузка toolbar (наши концепции): фильтр-чипы → «Фильтр» (overflow),
  // действия «Прочитать всё/Очистить» → «⋯», поиск — primary во всю ширину.
  const filterItems: OverflowItem[] = FILTERS.map(f => ({
    key: f.key, label: f.label, dot: filter === f.key, onClick: () => setFilter(f.key),
  }));
  const actionItems: OverflowItem[] = [];
  if (totalUnread > 0) actionItems.push({
    key: 'readall', icon: <CheckCheck size={16} />, label: 'Прочитать всё',
    onClick: () => { void markAllRead().then(rerender); },
  });
  if (notifs.some(n => n.isRead)) actionItems.push({
    key: 'clear', icon: <Trash2 size={16} />, label: 'Очистить прочитанные', danger: true,
    onClick: () => setConfirmClear(true),
  });

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      <HubHeader value="notifications" onTab={onHubTab} auth={auth} onLogout={onLogout} />

      <div style={{
        flex: 1, overflow: 'auto', padding: '24px 16px',
        display: 'flex', justifyContent: 'center',
        // Резерв под скроллбар: без него появление/исчезновение полосы сдвигает
        // весь центрированный контент вбок при переключении режимов
        scrollbarGutter: 'stable',
      }}>
        {/* Ширина каркаса НЕ зависит от режима — иначе заголовок и переключатель
            ездят по горизонтали при клике. Широкая раскладка нужна только
            «Диспетчеру»; шапка и лента уведомлений живут в колонке 680. */}
        <div style={{ width: '100%', maxWidth: 1180 }}>

          {/* Page header. Ширина шапки следует за контентом режима (680 у ленты,
              1180 у канбана) — тогда «Период» диспетчера идёт вровень со своей
              шапкой. При этом заголовок с переключателем ЦЕНТРИРОВАНЫ: оба блока
              центрированы по одной оси экрана, поэтому переключатель остаётся на
              месте при смене режима. Тулбар — отдельной строкой ниже, чтобы не
              сбивать эту центровку. */}
          <div style={{ maxWidth: mode === 'notifications' ? 680 : 1180, margin: '0 auto 24px' }}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 14, minWidth: 0 }}>
              {!isMobile && (
                <div style={{
                  fontFamily: FONT.serif, fontSize: FS.h2, fontWeight: 700,
                  color: C.textHeading, letterSpacing: '-0.3px',
                  // Ширина под более длинное слово («Уведомления»), чтобы смена
                  // заголовка не сдвигала переключатель рядом
                  minWidth: 150, whiteSpace: 'nowrap',
                }}>
                  {mode === 'notifications' ? 'Уведомления' : 'Диспетчер'}
                </div>
              )}
              {/* PillSwitch: Уведомления / Диспетчер */}
              <div style={{
                display: 'inline-flex', border: `1px solid ${C.border}`,
                borderRadius: R.md, overflow: 'hidden',
              }}>
                <button
                  onClick={() => setMode('notifications')}
                  style={{
                    display: 'inline-flex', alignItems: 'center', gap: 5,
                    padding: '5px 12px', cursor: 'pointer',
                    border: 'none',
                    background: mode === 'notifications' ? C.accent : 'transparent',
                    color: mode === 'notifications' ? C.onAccent : C.textSecondary,
                    fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600,
                  }}
                >
                  <Bell size={14} />
                  Уведомления
                </button>
                <button
                  onClick={() => setMode('dispatcher')}
                  style={{
                    display: 'inline-flex', alignItems: 'center', gap: 5,
                    padding: '5px 12px', cursor: 'pointer',
                    border: 'none',
                    background: mode === 'dispatcher' ? C.accent : 'transparent',
                    color: mode === 'dispatcher' ? C.onAccent : C.textSecondary,
                    fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600,
                  }}
                >
                  <Columns size={14} />
                  Диспетчер
                </button>
              </div>
            </div>
            {/* Тулбар — своей строкой под заголовком (только у ленты уведомлений).
                Он идёт ПОСЛЕ центрированного заголовка, поэтому его появление
                и исчезновение не сдвигает переключатель */}
            {!isMobile && mode === 'notifications' && (
              <div style={{ display: 'flex', gap: SP.md, alignItems: 'center', justifyContent: 'flex-end', minHeight: 34, marginTop: SP.lg }}>
                {(<>
                {/* Search */}
                <div style={{ position: 'relative', width: 200 }}>
                  <Search size={14} style={{
                    position: 'absolute', left: 10, top: '50%',
                    transform: 'translateY(-50%)', color: C.textMuted, pointerEvents: 'none',
                  }} />
                  <input
                    placeholder="Поиск..."
                    value={search}
                    onChange={e => setSearch(e.target.value)}
                    style={{
                      width: '100%', height: 34,
                      padding: '0 12px 0 32px',
                      border: `1px solid ${C.border}`,
                      borderRadius: R.md,
                      background: C.bgCard,
                      fontFamily: FONT.sans,
                      fontSize: FS.sm,
                      color: C.textPrimary,
                      outline: 'none',
                    }}
                  />
                </div>
                {totalUnread > 0 && (
                  <button
                    style={{
                      padding: '7px 16px', borderRadius: R.md,
                      border: `1px solid ${C.border}`,
                      background: C.bgCard, color: C.textSecondary,
                      fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 500,
                      cursor: 'pointer', whiteSpace: 'nowrap',
                      display: 'flex', alignItems: 'center', gap: 6,
                    }}
                    onClick={() => markAllRead().then(rerender)}
                    onMouseEnter={e => { e.currentTarget.style.borderColor = C.accent; e.currentTarget.style.color = C.accent; }}
                    onMouseLeave={e => { e.currentTarget.style.borderColor = C.border; e.currentTarget.style.color = C.textSecondary; }}
                  >
                    <CheckCheck size={14} /> Прочитать всё
                  </button>
                )}
                {notifs.some(n => n.isRead) && (
                  <button
                    style={{
                      padding: '7px 16px', borderRadius: R.md,
                      border: `1px solid ${C.border}`,
                      background: C.bgCard, color: C.textMuted,
                      fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 500,
                      cursor: 'pointer', whiteSpace: 'nowrap',
                      display: 'flex', alignItems: 'center', gap: 6,
                    }}
                    onClick={() => setConfirmClear(true)}
                    onMouseEnter={e => { e.currentTarget.style.borderColor = C.danger; e.currentTarget.style.color = C.danger; }}
                    onMouseLeave={e => { e.currentTarget.style.borderColor = C.border; e.currentTarget.style.color = C.textMuted; }}
                  >
                    <Trash2 size={14} /> Очистить прочитанные
                  </button>
                )}
                </>)}
              </div>
            )}
          </div>

          {/* Мобильная строка toolbar: поиск (primary) + «Фильтр» (overflow) + «⋯» действия */}
          {mode === 'notifications' && isMobile && (
            <div style={{ display: 'flex', gap: 8, alignItems: 'center', maxWidth: 680, margin: `0 auto ${SP.lg}px` }}>
              <div style={{ position: 'relative', flex: 1, minWidth: 0 }}>
                <Search size={14} style={{ position: 'absolute', left: 10, top: '50%', transform: 'translateY(-50%)', color: C.textMuted, pointerEvents: 'none' }} />
                <input
                  placeholder="Поиск..." value={search} onChange={e => setSearch(e.target.value)}
                  style={{ width: '100%', height: 38, padding: '0 12px 0 32px', border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgCard, fontFamily: FONT.sans, fontSize: 16, color: C.textPrimary, outline: 'none', boxSizing: 'border-box' }}
                />
              </div>
              <ToolbarOverflowMenu isMobile title="Фильтр" triggerIcon={<SlidersHorizontal size={15} strokeWidth={2.2} />} triggerLabel="Фильтр" indicator={filter !== 'all' ? true : undefined} items={filterItems} />
              {actionItems.length > 0 && <ToolbarOverflowMenu isMobile title="Действия" items={actionItems} />}
            </div>
          )}

          {mode === 'dispatcher' ? (
            <AgentKanban />
          ) : (
            // Читаемая колонка ленты — 680 по центру, вровень с шапкой
            <div style={{ maxWidth: 680, margin: '0 auto' }}>
              {/* Filter chips — десктоп; на мобиле фильтр в «⋯ Фильтр» */}
              {!isMobile && (
                <div style={{ display: 'flex', gap: SP.sm, marginBottom: SP.xl, flexWrap: 'wrap' }}>
                  {FILTERS.map(f => (
                    <button
                      key={f.key}
                      style={{
                        padding: '5px 14px', borderRadius: 999,
                        fontSize: FS.sm, fontWeight: 500,
                        color: filter === f.key ? C.onAccent : C.textSecondary,
                        background: filter === f.key ? C.accent : 'transparent',
                        border: `1px solid ${filter === f.key ? C.accent : C.border}`,
                        cursor: 'pointer', fontFamily: FONT.sans,
                        whiteSpace: 'nowrap',
                      }}
                      onClick={() => setFilter(f.key)}
                    >
                      {f.label}
                    </button>
                  ))}
                </div>
              )}

              {/* Notification list */}
              {Object.keys(groups).length === 0 ? (
                <div style={{
                  display: 'flex', flexDirection: 'column',
                  alignItems: 'center', justifyContent: 'center',
                  padding: '64px 16px', textAlign: 'center',
                }}>
                  <div style={{
                    width: 96, height: 96, borderRadius: '50%',
                    background: C.bgPanel,
                    display: 'flex', alignItems: 'center', justifyContent: 'center',
                    fontSize: 40, color: C.textMuted, marginBottom: 24,
                  }}>
                    <Bell size={40} />
                  </div>
                  <div style={{
                    fontSize: FS.lg, fontWeight: 600,
                    color: C.textHeading, marginBottom: SP.sm,
                  }}>
                    Всё чисто
                  </div>
                  <div style={{
                    fontSize: FS.sm, color: C.textMuted,
                    maxWidth: 320, lineHeight: 1.6,
                  }}>
                    {search
                      ? 'Нет уведомлений по вашему запросу'
                      : 'Нет уведомлений по выбранному фильтру'}
                  </div>
                </div>
              ) : (
                Object.entries(groups).map(([gk, gnotifs]) => (
                  <div key={gk} style={{ marginBottom: SP.xl }}>
                    <div style={{
                      display: 'flex', alignItems: 'center', gap: SP.lg,
                      marginBottom: SP.lg,
                      fontSize: FS.sm, fontWeight: 600,
                      color: C.textMuted, textTransform: 'uppercase',
                      letterSpacing: '0.6px',
                    }}>
                      {gk}
                      <div style={{
                        flex: 1, height: 1, background: C.borderLight,
                      }} />
                    </div>
                    {gnotifs.map(n => (
                      <div key={n.id} style={{ marginBottom: SP.lg }}>
                        <NotificationCard
                          item={n}
                          onRead={async (id) => { await markRead(id); rerender(); }}
                          onDelete={async (id) => { await deleteNotification(id); rerender(); }}
                        />
                      </div>
                    ))}
                  </div>
                ))
              )}
            </div>
          )}

        </div>
      </div>

      {confirmClear && (
        <ConfirmDialog
          title="Очистить прочитанные?"
          subtitle="Все прочитанные уведомления будут удалены."
          confirmLabel="Очистить"
          confirmVariant="danger"
          onConfirm={() => { setConfirmClear(false); void deleteReadAll().then(rerender); }}
          onCancel={() => setConfirmClear(false)}
        />
      )}
    </div>
  );
}
