import { useEffect, useState } from 'react';
import { Bell, CheckCheck } from 'lucide-react';
import { C, FONT } from '../../lib/design';
import type { HubTab } from '../../components/HubTabs';
import type { NotificationItem } from '../../types';
import {
  ensureNotificationsLoaded,
  ensureNotificationsSubscribed,
  subscribeToNotifications,
  getNotifications,
  getUnreadCount,
  markRead,
  markAllRead,
} from '../../lib/notifications';
import { KIND_META, formatTime, openNotificationUrl } from '../notifications/kindMeta';
import { WidgetCard, WidgetAction, WidgetEmpty } from './WidgetCard';

// Сколько непрочитанных показываем: виджет — сигнал «загляни», а не лента.
const SHOWN = 3;

function NotificationRow({ item, onOpen }: { item: NotificationItem; onOpen: () => void }) {
  const meta = KIND_META[item.kind] ?? KIND_META.info;
  return (
    <button
      onClick={onOpen}
      title={item.body || item.title}
      style={{
        display: 'flex', alignItems: 'center', gap: 9, width: '100%', textAlign: 'left',
        background: 'none', border: 'none', borderRadius: 8, padding: '7px 8px',
        margin: '0 -8px', cursor: 'pointer', minWidth: 0,
      }}
      onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
      onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}
    >
      <span style={{
        width: 22, height: 22, borderRadius: 7, flexShrink: 0,
        background: meta.bg, color: meta.color,
        display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: 11,
      }}>
        {meta.icon}
      </span>
      <span style={{
        fontFamily: FONT.sans, fontSize: 13, color: C.textPrimary, flex: 1, minWidth: 0,
        whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
      }}>
        {item.title}
      </span>
      <span style={{
        fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted, flexShrink: 0,
      }}>
        {formatTime(item.createdAt)}
      </span>
    </button>
  );
}

// «Уведомления»: топ-3 непрочитанных. Клик по строке помечает прочитанным и уводит
// по диплинку (если он есть). Данные — общий стор notifications.ts, он же дает realtime.
export function NotificationsWidget({ onHubTab }: { onHubTab: (t: HubTab) => void }) {
  const [, rerender] = useState(0);

  useEffect(() => {
    ensureNotificationsSubscribed();
    void ensureNotificationsLoaded().then(() => rerender(n => n + 1));
    return subscribeToNotifications(() => rerender(n => n + 1));
  }, []);

  const unread = getNotifications().filter(n => !n.isRead);
  const total = getUnreadCount();
  const shown = unread.slice(0, SHOWN);

  const open = (item: NotificationItem) => {
    if (!item.isRead) void markRead(item.id);
    if (item.url) openNotificationUrl(item.url);
    else onHubTab('notifications');
  };

  return (
    <WidgetCard
      icon={<Bell size={16} strokeWidth={2} />}
      title="Уведомления"
      action={
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 10 }}>
          {total > 0 && (
            <button
              onClick={() => { void markAllRead(); }}
              title="Прочитать всё"
              style={{
                display: 'inline-flex', alignItems: 'center', gap: 4,
                background: 'none', border: 'none', padding: 0, cursor: 'pointer',
                fontFamily: FONT.sans, fontSize: 12.5, color: C.textMuted, whiteSpace: 'nowrap',
              }}
              onMouseEnter={e => { e.currentTarget.style.color = C.accent; }}
              onMouseLeave={e => { e.currentTarget.style.color = C.textMuted; }}
            >
              <CheckCheck size={13} /> Прочитать всё
            </button>
          )}
          <WidgetAction label="Все →" onClick={() => onHubTab('notifications')} />
        </span>
      }
    >
      {shown.length === 0
        ? <WidgetEmpty text="Всё прочитано — новых уведомлений нет." />
        : (
          <div style={{ display: 'flex', flexDirection: 'column' }}>
            {shown.map(n => <NotificationRow key={n.id} item={n} onOpen={() => open(n)} />)}
            {total > SHOWN && (
              <div style={{
                fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted,
                padding: '6px 0 0',
              }}>
                и ещё {total - SHOWN} непрочитанных
              </div>
            )}
          </div>
        )}
    </WidgetCard>
  );
}
