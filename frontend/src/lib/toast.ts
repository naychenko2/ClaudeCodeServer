// Локальный тост (без сервера): показать краткое уведомление в том же стеке, что и
// SignalR-уведомления. NotificationToasts слушает событие 'cc-local-toast'.

export type ToastKind = 'reminder' | 'claude' | 'info';

// Действие внутри тоста: одна кнопка справа от текста («Отменить», «Открыть»…).
// Задан — NotificationToasts рендерит её отдельной строкой под body; не задан —
// тост как раньше, только текст. Колбэк зовётся на клик; повторный показ тоста
// с тем же id нужен самому вызывающему — NotificationToasts id не присваивает.
export interface ToastAction {
  label: string;
  onClick: () => void;
}

export interface LocalToast {
  title: string;
  body: string;
  kind?: ToastKind;
  action?: ToastAction;
}

export function showToast(title: string, body: string, kind: ToastKind = 'info', action?: ToastAction) {
  window.dispatchEvent(new CustomEvent<LocalToast>('cc-local-toast', {
    detail: { title, body, kind, ...(action ? { action } : {}) },
  }));
}
