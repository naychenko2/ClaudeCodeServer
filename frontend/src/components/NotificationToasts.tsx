// Тосты пользовательских уведомлений (напоминания о задачах, события Claude-исполнителя,
// сообщения персон по автоматизации). Слушает NotificationMessage (группа user_*) через
// SignalR и показывает стек в правом верхнем углу. Уведомление от персоны несёт её лицо
// (аватар) и строку «Роль (Имя) · Проект»; системное — плитку вида. Клик открывает
// диплинк только через обязательный onNavigate: url пишет и модель, а проверку своего
// origin делает обработчик (notificationTargetUrl), своей навигации здесь нет (S7).
// Закреплённое уведомление (sticky) не гаснет по таймеру и всплывает при каждом входе,
// пока не прочитано: «Открыть» или ✕ помечают его прочитанным.
// Тост-слой НЕ перехватывает клики интерфейса: слой и карточка — pointer-events:none,
// нажатие проходит сквозь тост к тому, что под ним. Кликабельны только контролы карточки
// (✕, «Открыть», кнопка действия) — они pointer-events:auto. Закреплённое висит
// неограниченно, и кликабельная карточка лежала бы поверх кнопок шапки раздела всё это
// время: нажатие до них не доходило (упавшие браузерные сценарии).

import { useEffect, useState } from 'react';
import { X } from 'lucide-react';
import { C, FONT, FS, R, SP, SHADOW, Z } from '../lib/design';
import { ICON_STROKE } from './ui/icons';
import { useGenSheetRaised } from '../lib/genSheet';
import { readStoredUserId } from '../lib/offline';
import { joinUser, onMessage, onReconnected } from '../lib/signalr';
import { loadUnreadCount, loadUnreadSticky, markRead } from '../lib/notifications';
import type { LocalToast, ToastAction } from '../lib/toast';
import { KIND_LABELS, TOAST_META } from '../features/notifications/kindMeta';
import { NotificationAvatar, hasPersona, notifPersonaLabel } from '../features/notifications/NotificationAvatar';

export interface ToastItem {
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
  // Серверное уведомление: id — чтобы пометить прочитанным и не задвоить тост
  notificationId?: string;
  sticky?: boolean;
  // Необязательное действие («Отменить», «Открыть»…) — кнопка справа под body.
  // Не задан — тост как раньше, только текст. Колбэк зовётся из NotificationToasts
  // при клике; id не нужен — действие само знает, что делать
  action?: ToastAction;
}

const AUTO_DISMISS_MS = 8000;
const MAX_TOASTS = 4;   // на экране одновременно; переполнение вытесняет самый старый

// Надзаголовок «кто · где»: персона → «Роль (Имя) · Проект», система → «Вид · Проект».
// Локальный тост 'error' использует TOAST_META.label и не показывает аватар персоны.
function eyebrowText(t: ToastItem): string {
  const who = hasPersona(t)
    ? notifPersonaLabel(t)
    : (TOAST_META[t.kind]?.label ?? KIND_LABELS[t.kind] ?? '');
  return t.projectName ? (who ? `${who} · ${t.projectName}` : t.projectName) : who;
}

// Подпись вида для NotificationAvatar: персональный аватар (чаты) либо плитка-индикатор
// системного вида. 'error' — локальный клиентский отказ, рисуется отдельной danger-плиткой
// в NotificationAvatar, чтобы визуально отличаться от обычных info-уведомлений.
function avatarKind(t: ToastItem): string {
  if (hasPersona(t)) return t.kind;
  return t.kind;
}

let nextId = 1;

function joinUserGroup() {
  const uid = readStoredUserId();
  if (uid) joinUser(uid).catch(() => {});
}

export function NotificationToasts({ onNavigate }: { onNavigate: (url: string) => void }) {
  const [toasts, setToasts] = useState<ToastItem[]>([]);

  // Закреплённое снимается только явно — и тогда помечается прочитанным, иначе всплыло
  // бы снова при следующем входе
  const dismiss = (t: ToastItem) => {
    setToasts(prev => prev.filter(x => x.id !== t.id));
    if (t.sticky && t.notificationId)
      markRead(t.notificationId).then(() => loadUnreadCount()).catch(() => {});
  };

  const openToast = (t: ToastItem) => {
    if (!t.url) return;
    dismiss(t);
    onNavigate(t.url);
  };

  useEffect(() => {
    joinUserGroup();
    const offReconnect = onReconnected(joinUserGroup);
    const pushToast = (t: Omit<ToastItem, 'id'>) => {
      const item: ToastItem = { id: nextId++, ...t };
      // Одно и то же серверное уведомление (живое + загруженное при входе) — один тост
      setToasts(prev => item.notificationId && prev.some(x => x.notificationId === item.notificationId)
        ? prev : [...prev, item].slice(-MAX_TOASTS));
      if (!item.sticky)
        setTimeout(() => setToasts(prev => prev.filter(x => x.id !== item.id)), AUTO_DISMISS_MS);
    };
    const off = onMessage(msg => {
      if (msg.type !== 'notification') return;
      pushToast({
        title: msg.title, body: msg.body, url: msg.url, kind: msg.kind,
        personaId: msg.personaId, personaName: msg.personaName, personaRole: msg.personaRole,
        personaColor: msg.personaColor, projectName: msg.projectName,
        notificationId: msg.notificationId, sticky: msg.sticky,
      });
    });
    // Закреплённые, пришедшие, пока вкладка была закрыта, — показываем при входе
    loadUnreadSticky()
      .then(list => list.forEach(n => pushToast({
        title: n.title, body: n.body, url: n.url, kind: n.kind,
        personaId: n.personaId, personaName: n.personaName, personaRole: n.personaRole,
        personaColor: n.personaColor, projectName: n.projectName,
        notificationId: n.id, sticky: true,
      })))
      .catch(() => {});
    // Локальные тосты (клиентские события без сервера)
    const onLocal = (e: Event) => {
      const d = (e as CustomEvent<LocalToast>).detail;
      pushToast({ title: d.title, body: d.body, kind: d.kind ?? 'info', action: d.action });
    };
    window.addEventListener('cc-local-toast', onLocal);
    return () => { off(); offReconnect(); window.removeEventListener('cc-local-toast', onLocal); };
  }, []);

  // Поднятая шторка панели генерации держит шапку в зоне тостов: тосты уходят под её слой
  const sheetRaised = useGenSheetRaised();

  if (toasts.length === 0) return null;
  return (
    <ToastStack
      items={toasts}
      sheetRaised={sheetRaised}
      onOpen={openToast}
      onDismiss={dismiss}
      onAction={t => { t.action?.onClick(); setToasts(prev => prev.filter(x => x.id !== t.id)); }}
    />
  );
}

// Слой тостов — только вид, без состояния и подписок: правило «тост не перехватывает
// клики интерфейса» должно проверяться рендером. Слой и карточка — pointer-events:none:
// тост висит над интерфейсом, но нажатию не мешает (тот же приём, что у «призрака»
// разделителя в PanelZone). Кликабельны контролы карточки: ✕, «Открыть», действие.
export function ToastStack({ items, sheetRaised, onOpen, onDismiss, onAction }: {
  items: ToastItem[];
  sheetRaised: boolean;
  onOpen: (t: ToastItem) => void;
  onDismiss: (t: ToastItem) => void;
  onAction: (t: ToastItem) => void;
}) {
  if (items.length === 0) return null;
  return (
    <div data-testid="toast-layer" style={{
      position: 'fixed', top: 14, right: 14, zIndex: sheetRaised ? Z.modal - 1 : Z.modal + 10,
      display: 'flex', flexDirection: 'column', gap: 10,
      maxWidth: 'min(360px, calc(100vw - 28px))',
      pointerEvents: 'none',
    }}>
      {items.map(t => {
        const eyebrow = eyebrowText(t);
        // 'error' — клиентский отказ (текст сервера в body). Рамка danger, чтобы
        // оператор не перепутал с обычным info
        const isError = t.kind === 'error';
        return (
          <div
            key={t.id}
            data-testid="toast"
            style={{
              display: 'flex', gap: SP.md, alignItems: 'flex-start',
              padding: '13px 15px',
              background: C.bgCard,
              border: `1px solid ${isError ? C.dangerBorder : C.border}`,
              borderRadius: R.xl, boxShadow: SHADOW.dropdown,
              pointerEvents: 'none',
            }}
          >
            <div style={{ marginTop: 1, flexShrink: 0 }}>
              <NotificationAvatar
                personaId={t.personaId}
                personaName={t.personaName}
                personaColor={t.personaColor}
                kind={avatarKind(t)}
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
                fontFamily: FONT.sans, fontSize: FS.base, fontWeight: 700,
                color: isError ? C.danger : C.textHeading,
                whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
              }}>
                {t.title}
              </div>
              <div style={{
                fontFamily: FONT.sans, fontSize: FS.sm,
                color: isError ? C.dangerText : C.textSecondary,
                marginTop: SP.xxs,
                overflow: 'hidden', display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical',
              }}>
                {t.body}
              </div>
              {/* Адрес уведомления — переход остаётся явной кнопкой: сама карточка клики
                  не принимает. Форма та же, что у кнопки-действия ниже: accent-цветом,
                  без заливки */}
              {t.url && (
                <div style={{ marginTop: SP.sm, display: 'flex', justifyContent: 'flex-end' }}>
                  <button
                    type="button"
                    data-testid="toast-open"
                    onClick={() => onOpen(t)}
                    style={{
                      border: 'none', background: 'none', cursor: 'pointer', padding: 0,
                      color: C.accent, fontFamily: FONT.sans, fontWeight: 600,
                      fontSize: FS.sm, lineHeight: 1.2, pointerEvents: 'auto',
                    }}
                  >
                    Открыть
                  </button>
                </div>
              )}
              {t.action && (
                // Кнопка-действие — справа под body, на своей строке («Отменить» в тосте
                // архивации): зовёт свой колбэк и снимает тост. Курсор pointer — только
                // на ней
                <div style={{ marginTop: SP.sm, display: 'flex', justifyContent: 'flex-end' }}>
                  <button
                    type="button"
                    onClick={() => onAction(t)}
                    style={{
                      border: 'none', background: 'none', cursor: 'pointer', padding: 0,
                      color: C.accent, fontFamily: FONT.sans, fontWeight: 600,
                      fontSize: FS.sm, lineHeight: 1.2, pointerEvents: 'auto',
                    }}
                  >
                    {t.action.label}
                  </button>
                </div>
              )}
            </div>
            <button
              onClick={() => onDismiss(t)}
              title="Закрыть"
              style={{
                border: 'none', background: 'none', cursor: 'pointer', padding: 2,
                color: C.textMuted, fontSize: 13, lineHeight: 1, fontFamily: FONT.sans, flexShrink: 0,
                display: 'flex', alignItems: 'center', pointerEvents: 'auto',
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
