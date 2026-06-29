// Браузерные уведомления о событиях сессии (нужно решение / ход завершён).
// Глобальный вкл/выкл хранится в localStorage; уведомления показываем только
// когда вкладка не в фокусе (document.hidden), чтобы не дублировать видимое в UI.

const LS_KEY = 'cc_notify_enabled';

export function isNotifySupported(): boolean {
  return typeof window !== 'undefined' && 'Notification' in window;
}

export function isNotifyEnabled(): boolean {
  if (!isNotifySupported()) return false;
  return localStorage.getItem(LS_KEY) === '1' && Notification.permission === 'granted';
}

// Включение требует разрешения пользователя (вызов из обработчика клика).
// Возвращает true, если уведомления включены и разрешены.
export async function setNotifyEnabled(enabled: boolean): Promise<boolean> {
  if (!enabled) {
    localStorage.setItem(LS_KEY, '0');
    return false;
  }
  if (!isNotifySupported()) return false;
  let perm = Notification.permission;
  if (perm === 'default') perm = await Notification.requestPermission();
  if (perm !== 'granted') {
    localStorage.setItem(LS_KEY, '0');
    return false;
  }
  localStorage.setItem(LS_KEY, '1');
  return true;
}

export function notify(title: string, body: string): void {
  if (!isNotifyEnabled()) return;
  if (typeof document !== 'undefined' && !document.hidden) return; // вкладка активна — не отвлекаем
  try {
    const n = new Notification(title, { body, icon: '/favicon.ico', tag: 'claude-session' });
    n.onclick = () => { window.focus(); n.close(); };
  } catch { /* пользователь мог отозвать разрешение */ }
}
