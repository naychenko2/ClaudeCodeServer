// Тосты пользовательских уведомлений (напоминания о задачах, события Claude-исполнителя,
// сообщения персон по автоматизации). Слушает NotificationMessage (группа user_*) через
// SignalR и показывает стек в правом верхнем углу. Уведомление от персоны несёт её лицо
// (аватар) и строку «Роль (Имя) · Проект»; системное — плитку вида. Клик открывает
// диплинк через onNavigate (SPA-переход), без обработчика — фолбэк на hash-URL.

import { useEffect, useState } from 'react';
import { X } from 'lucide-react';
import { C, FONT, FS, R, SP, SHADOW, Z } from '../lib/design';
import { ICON_STROKE } from './ui/icons';
import { joinUser, onMessage, onReconnected } from '../lib/signalr';
import type { LocalToast } from '../lib/toast';
import { KIND_LABELS } from '../features/notifications/kindMeta';
import { NotificationAvatar, hasPersona, notifPersonaLabel } from '../features/notifications/NotificationAvatar';

interface ToastItem {
  id: number;
  title: string;
  body: string;
  url?: string;
  kind: string;
  personaId?: string;
  personaName?: string;
  personaRole?: string;
  personaColor?: string;
  projectName?: string;
}

const AUTO_DISMISS_MS = 8000;
const MAX_TOASTS = 4;   // на экране одновременно; переполнение вытесняет самый старый

// Надзаголовок «кто · где»: персона → «Роль (Имя) · Проект», система → «Вид · Проект»
function eyebrowText(t: ToastItem): string {
  const who = hasPersona(t) ? notifPersonaLabel(t) : (KIND_LABELS[t.kind] ?? '');
  return t.projectName ? (who ? `${who} · ${t.projectName}` : t.projectName) : who;
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
      setToasts(prev => [...prev, item].slice(-MAX_TOASTS));
      setTimeout(() => setToasts(prev => prev.filter(x => x.id !== item.id)), AUTO_DISMISS_MS);
    };
    const off = onMessage(msg => {
      if (msg.type !== 'notification') return;
      pushToast({
        title: msg.title, body: msg.body, url: msg.url, kind: msg.kind,
        personaId: msg.personaId, personaName: msg.personaName, personaRole: msg.personaRole,
        personaColor: msg.personaColor, projectName: msg.projectName,
      });
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
      {toasts.map(t => {
        const eyebrow = eyebrowText(t);
        return (
          <div
            key={t.id}
            onClick={() => openToast(t)}
            style={{
              display: 'flex', gap: SP.md, alignItems: 'flex-start',
              padding: '13px 15px', cursor: t.url ? 'pointer' : 'default',
              background: C.bgCard, border: `1px solid ${C.border}`,
              borderRadius: R.xl, boxShadow: SHADOW.dropdown,
            }}
          >
            <div style={{ marginTop: 1, flexShrink: 0 }}>
              <NotificationAvatar
                personaId={t.personaId}
                personaName={t.personaName}
                personaColor={t.personaColor}
                kind={t.kind}
                size={36}
              />
            </div>
            <div style={{ minWidth: 0, flex: 1 }}>
              {eyebrow && (
                <div style={{
                  fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted,
                  marginBottom: SP.xxs,
                  whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
                }}>
                  {eyebrow}
                </div>
              )}
              <div style={{
                fontFamily: FONT.sans, fontSize: FS.base, fontWeight: 700, color: C.textHeading,
                whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
              }}>
                {t.title}
              </div>
              <div style={{
                fontFamily: FONT.sans, fontSize: FS.sm, color: C.textSecondary, marginTop: SP.xxs,
                overflow: 'hidden', display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical',
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
        );
      })}
    </div>
  );
}
