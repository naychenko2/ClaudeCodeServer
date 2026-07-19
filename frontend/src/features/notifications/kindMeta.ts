import { C } from '../../lib/design';
import type { NotificationKind } from '../../types';

// Оформление видов уведомлений — общее для раздела и виджета дашборда.
// Токены design.ts (CSS-переменные), а не хардкод-hex: иначе ломается темная тема.
export const KIND_META: Record<NotificationKind, { icon: string; color: string; bg: string }> = {
  reminder: { icon: '⏰', color: C.warning, bg: C.warningBg },
  claude: { icon: '●', color: C.accent, bg: C.accentLight },
  info: { icon: 'ℹ', color: C.info, bg: C.infoBg },
  success: { icon: '✓', color: C.success, bg: C.successBg },
  meeting: { icon: '🏁', color: C.plan, bg: C.planLight },
};

export const KIND_LABELS: Record<string, string> = {
  reminder: 'Напоминание',
  claude: 'Claude',
  info: 'Системное',
  success: 'Выполнено',
  meeting: 'Совещание',
};

// Время уведомления: сегодня — часы:минуты, дальше — «Вчера» / день недели / дата
export function formatTime(iso: string) {
  const d = new Date(iso);
  const now = new Date();
  const diff = now.getTime() - d.getTime();
  const day = 86400000;

  if (diff < day) return d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
  if (diff < 2 * day) return 'Вчера';
  if (diff < 7 * day) return d.toLocaleDateString('ru-RU', { weekday: 'short' });
  return d.toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' });
}

// SPA-переход по диплинку уведомления. Смена location.hash сама по себе экран не
// меняет — hashchange в приложении никто не слушает, переход идет через App.
export function openNotificationUrl(url: string) {
  window.dispatchEvent(new CustomEvent('cc-open-url', { detail: { url } }));
}
