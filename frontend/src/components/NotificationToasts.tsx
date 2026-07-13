// Тосты пользовательских уведомлений (напоминания о задачах, события Claude-исполнителя).
// Слушает NotificationMessage (группа user_*) через SignalR и показывает стек в правом
// верхнем углу. Клик по тосту открывает диплинк через onNavigate (SPA-переход в App
// без перезагрузки); без обработчика — фолбэк на полную загрузку по hash-URL.

import { useEffect, useState } from 'react';
import { Bell, X } from 'lucide-react';
import { C, FONT, R, SHADOW, Z } from '../lib/design';
import { ICON_STROKE } from './ui/icons';
import { joinUser, onMessage, onReconnected } from '../lib/signalr';
import type { LocalToast } from '../lib/toast';

interface ToastItem {
  id: number;
  title: string;
  body: string;
  url?: string;
  kind: string;
}

const AUTO_DISMISS_MS = 8000;

const KIND_COLOR: Record<ToastItem['kind'], string> = {
  reminder: C.warning,  // warning — колокольчик
  claude:   C.accent,  // accent — события Claude
  info:     C.info,  // info
};

function KindIcon({ kind }: { kind: ToastItem['kind'] }) {
  const color = KIND_COLOR[kind];
  if (kind === 'reminder')
    return <Bell size={15} strokeWidth={2} color={color} style={{ flexShrink: 0 }} />;
  return <span style={{ width: 9, height: 9, borderRadius: '50%', background: color, display: 'inline-block' }} />;
}

let nextId = 1;

function joinUserGroup() {
  const uid = localStorage.getItem('cc_user_id') || sessionStorage.getItem('cc_user_id');
  if (uid) joinUser(uid).catch(() => {});
}

export function NotificationToasts({ onNavigate }: { onNavigate?: (url: string) => void }) {
  const [toasts, setToasts] = useState<ToastItem[]>([]);

  const openToast = (t: ToastItem) => {
    if (!t.url) return;
    setToasts(prev => prev.filter(x => x.id !== t.id));
    if (onNavigate) onNavigate(t.url);
    else window.location.assign(t.url);
  };

  useEffect(() => {
    joinUserGroup();
    const offReconnect = onReconnected(joinUserGroup);
    const pushToast = (t: Omit<ToastItem, 'id'>) => {
      const item: ToastItem = { id: nextId++, ...t };
      setToasts(prev => [...prev, item]);
      setTimeout(() => setToasts(prev => prev.filter(x => x.id !== item.id)), AUTO_DISMISS_MS);
    };
    const off = onMessage(msg => {
      if (msg.type !== 'notification') return;
      pushToast({ title: msg.title, body: msg.body, url: msg.url, kind: msg.kind });
    });
    // Локальные тосты (клиентские события без сервера)
    const onLocal = (e: Event) => {
      const d = (e as CustomEvent<LocalToast>).detail;
      pushToast({ title: d.title, body: d.body, kind: d.kind ?? 'info' });
    };
    window.addEventListener('cc-local-toast', onLocal);
    return () => { off(); offReconnect(); window.removeEventListener('cc-local-toast', onLocal); };
  }, []);

  if (toasts.length === 0) return null;

  return (
    <div style={{
      position: 'fixed', top: 14, right: 14, zIndex: Z.modal + 10,
      display: 'flex', flexDirection: 'column', gap: 10,
      maxWidth: 'min(360px, calc(100vw - 28px))',
    }}>
      {toasts.map(t => (
        <div
          key={t.id}
          onClick={() => openToast(t)}
          style={{
            display: 'flex', gap: 11, alignItems: 'flex-start',
            padding: '13px 15px', cursor: t.url ? 'pointer' : 'default',
            background: C.bgCard, border: `1px solid ${C.border}`,
            borderRadius: R.xl, boxShadow: SHADOW.dropdown,
          }}
        >
          <span style={{ flexShrink: 0, marginTop: 1 }}><KindIcon kind={t.kind} /></span>
          <div style={{ minWidth: 0, flex: 1 }}>
            <div style={{ fontFamily: FONT.sans, fontSize: 13.5, fontWeight: 700, color: C.textHeading }}>
              {t.title}
            </div>
            <div style={{
              fontFamily: FONT.sans, fontSize: 12.5, color: C.textSecondary, marginTop: 3,
              overflow: 'hidden', display: '-webkit-box', WebkitLineClamp: 3, WebkitBoxOrient: 'vertical',
            }}>
              {t.body}
            </div>
          </div>
          <button
            onClick={e => { e.stopPropagation(); setToasts(prev => prev.filter(x => x.id !== t.id)); }}
            title="Закрыть"
            style={{
              border: 'none', background: 'none', cursor: 'pointer', padding: 2,
              color: C.textMuted, fontSize: 13, lineHeight: 1, fontFamily: FONT.sans, flexShrink: 0,
              display: 'flex', alignItems: 'center',
            }}
          >
            <X size={13} strokeWidth={ICON_STROKE} />
          </button>
        </div>
      ))}
    </div>
  );
}
