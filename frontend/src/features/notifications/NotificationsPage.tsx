import { useEffect, useState, useCallback } from 'react';
import { Bell, CheckCheck, Search, Trash2, Columns, SlidersHorizontal, Folder } from 'lucide-react';
import { C, FONT, FS, R, SP, SHADOW } from '../../lib/design';
import { NotificationAvatar, hasPersona, notifPersonaLabel, notifAccentColor } from './NotificationAvatar';
import { HubHeader } from '../../components/HubHeader';
import { ConfirmDialog } from '../../components/ui';
import { ToolbarOverflowMenu, type OverflowItem } from '../../components/ToolbarOverflowMenu';
import { useIsMobile } from '../../lib/breakpoints';
import type { HubTab } from '../../components/HubTabs';
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
import { KIND_META, KIND_LABELS, formatTime, openNotificationUrl } from './kindMeta';

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
  const withPersona = hasPersona(item);
  const stripColor = notifAccentColor(item, item.kind);

  return (
    <div
      style={{
        display: 'flex',
        gap: SP.lg,
        padding: SP.xl,
        background: C.bgCard,
        borderRadius: R.lg,
        border: `1px solid ${item.isRead ? C.border : C.accent}`,
        boxShadow: SHADOW.card,
        cursor: 'default',
        position: 'relative',
        transition: 'all 0.2s ease',
        overflow: 'hidden',
      }}
      onMouseEnter={e => {
        const el = e.currentTarget;
        el.style.borderColor = C.accentMuted;
        el.style.boxShadow = SHADOW.dropdown;
        el.style.transform = 'translateY(-1px)';
      }}
      onMouseLeave={e => {
        const el = e.currentTarget;
        el.style.borderColor = item.isRead ? C.border : C.accent;
        el.style.boxShadow = SHADOW.card;
        el.style.transform = 'none';
      }}
    >
      {/* Color strip — цвет персоны или вида */}
      <div style={{
        position: 'absolute',
        left: -1, top: -1, bottom: -1, width: 4,
        background: stripColor,
        borderRadius: '12px 0 0 12px',
      }} />

      {/* Аватар персоны (круг) или плитка вида (квадрат) */}
      <div style={{ marginTop: 2, flexShrink: 0 }}>
        <NotificationAvatar
          personaId={item.personaId}
          personaName={item.personaName}
          personaColor={item.personaColor}
          kind={item.kind}
          size={44}
        />
      </div>

      {/* Body */}
      <div style={{ flex: 1, minWidth: 0 }}>
        {withPersona && (
          <div style={{
            fontSize: FS.xs, fontWeight: 600, color: C.textSecondary,
            marginBottom: SP.xxs, maxWidth: 320,
            whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
          }}>
            {notifPersonaLabel(item)}
          </div>
        )}
        <div style={{ display: 'flex', justifyContent: 'space-between', gap: SP.md }}>
          <div style={{
            fontWeight: 600,
            fontSize: FS.base,
            color: C.textHeading,
            display: 'flex', alignItems: 'center', gap: SP.md,
            flex: 1,
          }}>
            {item.title}
            {!item.isRead && (
              <div style={{
                width: 8, height: 8, borderRadius: '50%',
                background: C.accent, flexShrink: 0,
              }} />
            )}
          </div>
          <span style={{
            fontSize: FS.xs, color: C.textMuted,
            whiteSpace: 'nowrap', flexShrink: 0,
            marginTop: 2,
          }}>
            {formatTime(item.createdAt)}
          </span>
        </div>

        <div style={{
          fontSize: FS.sm,
          color: C.textSecondary,
          marginTop: 3,
          lineHeight: 1.55,
          display: '-webkit-box',
          WebkitLineClamp: 2,
          WebkitBoxOrient: 'vertical',
          overflow: 'hidden',
          maxWidth: 520,
        }}>
          {item.body}
        </div>

        {/* Meta row */}
        <div style={{
          display: 'flex', alignItems: 'center', gap: SP.md,
          marginTop: SP.md, flexWrap: 'wrap',
        }}>
          <span style={{
            display: 'inline-flex', alignItems: 'center', gap: 4,
            padding: '2px 8px', borderRadius: 4,
            fontSize: 10, fontWeight: 600, textTransform: 'uppercase',
            letterSpacing: '0.3px', lineHeight: 1.5,
            background: meta.bg, color: meta.color,
          }}>
            {KIND_LABELS[item.kind] ?? item.kind}
          </span>
          {item.projectName ? (
            <span style={{
              display: 'inline-flex', alignItems: 'center', gap: SP.xs,
              padding: '2px 8px', borderRadius: R.sm,
              fontSize: FS.xs, fontWeight: 500,
              background: C.bgInset, color: C.textMuted,
              maxWidth: 180, overflow: 'hidden',
            }}>
              <Folder size={11} strokeWidth={2} style={{ flexShrink: 0 }} />
              <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {item.projectName}
              </span>
            </span>
          ) : item.source && (
            <span style={{ fontSize: FS.xs, color: C.textMuted }}>
              · {item.source}
            </span>
          )}
          {item.tag && (
            <span style={{ fontSize: FS.xs, color: C.textMuted }}>
              · {item.tag}
            </span>
          )}
        </div>

        {/* Actions */}
        <div style={{
          display: 'flex', gap: SP.sm, marginTop: SP.lg,
          opacity: 0.95,
        }}>
          {item.url && (
            <button
              style={{
                padding: '6px 12px', borderRadius: R.sm, border: 'none',
                fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600,
                cursor: 'pointer',
                background: C.accent, color: C.onAccent,
              }}
              onClick={() => {
                if (!item.isRead) onRead(item.id);
                openNotificationUrl(item.url!);
              }}
              onMouseEnter={e => { e.currentTarget.style.opacity = '0.88'; }}
              onMouseLeave={e => { e.currentTarget.style.opacity = '1'; }}
            >
              Открыть
            </button>
          )}
          {item.isRead ? null : (
            <button
              style={{
                padding: '6px 12px', borderRadius: R.sm, border: 'none',
                fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 500,
                cursor: 'pointer', background: 'transparent', color: C.textSecondary,
              }}
              onClick={() => onRead(item.id)}
              onMouseEnter={e => { e.currentTarget.style.background = C.bgPanel; e.currentTarget.style.color = C.textPrimary; }}
              onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; e.currentTarget.style.color = C.textSecondary; }}
            >
              ✓ Прочитано
            </button>
          )}
          <button
            style={{
              padding: '6px 12px', borderRadius: R.sm, border: 'none',
              fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 500,
              cursor: 'pointer', background: 'transparent', color: C.textMuted,
            }}
            onClick={() => onDelete(item.id)}
            onMouseEnter={e => { e.currentTarget.style.color = C.danger; e.currentTarget.style.background = C.dangerBg; }}
            onMouseLeave={e => { e.currentTarget.style.color = C.textMuted; e.currentTarget.style.background = 'transparent'; }}
          >
            ✕ Удалить
          </button>
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
  onHubTab: (t: HubTab) => void;
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
