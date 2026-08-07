// Браузерные уведомления о событиях сессии (нужно решение / ход завершён).
//
// Два уровня:
//  1) ГЛОБАЛЬНЫЙ рубильник — разрешение браузера + флаг в localStorage. Тумблер стоит
//     в разделе «Уведомления» перед строкой поиска: выключен — молчат все чаты.
//  2) ПЕР-ЧАТНЫЙ мьют — поле сессии notificationsMuted (кнопка в шапке чата и пункт
//     меню его карточки в списке). Заглушает один чат, не трогая остальные.
// Уведомления показываем только когда вкладка не в фокусе (document.hidden), чтобы не
// дублировать видимое в UI.

import { useSyncExternalStore } from 'react';
import type { Session } from '../types';
import { updateChatFields } from './chatUpdate';

const LS_KEY = 'cc_notify_enabled';

// Подписчики глобального тумблера: раздел «Уведомления» и кнопки в шапках чатов живут
// на одном экране, и переключение в одном месте обязано перерисовать другое
const listeners = new Set<() => void>();
function emit() { listeners.forEach(l => l()); }

export function isNotifySupported(): boolean {
  return typeof window !== 'undefined' && 'Notification' in window;
}

export function isNotifyEnabled(): boolean {
  if (!isNotifySupported()) return false;
  return localStorage.getItem(LS_KEY) === '1' && Notification.permission === 'granted';
}

// Реактивное чтение глобального тумблера для UI
export function useNotifyEnabled(): boolean {
  return useSyncExternalStore(
    cb => { listeners.add(cb); return () => { listeners.delete(cb); }; },
    isNotifyEnabled,
    () => false,
  );
}

// Включение требует разрешения пользователя (вызов из обработчика клика).
// Возвращает true, если уведомления включены и разрешены.
export async function setNotifyEnabled(enabled: boolean): Promise<boolean> {
  if (!enabled) {
    localStorage.setItem(LS_KEY, '0');
    emit();
    return false;
  }
  if (!isNotifySupported()) return false;
  let perm = Notification.permission;
  if (perm === 'default') perm = await Notification.requestPermission();
  if (perm !== 'granted') {
    localStorage.setItem(LS_KEY, '0');
    emit();
    return false;
  }
  localStorage.setItem(LS_KEY, '1');
  emit();
  return true;
}

// Придут ли уведомления по этому чату: общий рубильник включён И чат не заглушён
export function isChatNotifyOn(session: Pick<Session, 'notificationsMuted'>): boolean {
  return isNotifyEnabled() && !session.notificationsMuted;
}

// Реактивный вариант для кнопок (глобальный тумблер меняется на том же экране)
export function useChatNotifyOn(session: Pick<Session, 'notificationsMuted'>): boolean {
  return useNotifyEnabled() && !session.notificationsMuted;
}

// Вкл/выкл уведомлений одного чата. Включение при отсутствующем разрешении сначала
// поднимает глобальный рубильник (и запрашивает разрешение браузера) — иначе тумблер
// чата стоял бы «включённым» при молчащих уведомлениях. Выключение глобальный не трогает.
// Возвращает фактическое состояние и обновлённую сессию (её отдал бэкенд).
export async function setChatNotifyEnabled(session: Session, enabled: boolean): Promise<{
  enabled: boolean;
  session?: Session;
}> {
  if (enabled && !await setNotifyEnabled(true)) return { enabled: false };
  const muted = !enabled;
  // Поле уже в нужном состоянии (глушили только глобальным тумблером) — запрос не шлём
  if ((session.notificationsMuted ?? false) === muted) return { enabled, session };
  return { enabled, session: await updateChatFields(session, { notificationsMuted: muted }) };
}

export function notify(title: string, body: string): void {
  if (!isNotifyEnabled()) return;
  if (typeof document !== 'undefined' && !document.hidden) return; // вкладка активна — не отвлекаем
  try {
    const n = new Notification(title, { body, icon: '/favicon.ico', tag: 'claude-session' });
    n.onclick = () => { window.focus(); n.close(); };
  } catch { /* пользователь мог отозвать разрешение */ }
}
