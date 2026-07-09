// Локальный тост (без сервера): показать краткое уведомление в том же стеке, что и
// SignalR-уведомления. NotificationToasts слушает событие 'cc-local-toast'.

export type ToastKind = 'reminder' | 'claude' | 'info';

export interface LocalToast {
  title: string;
  body: string;
  kind?: ToastKind;
}

export function showToast(title: string, body: string, kind: ToastKind = 'info') {
  window.dispatchEvent(new CustomEvent<LocalToast>('cc-local-toast', { detail: { title, body, kind } }));
}
