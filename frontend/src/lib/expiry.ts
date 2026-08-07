import type { Session } from '../types';

// Временные чаты: пресеты срока жизни и форматирование остатка времени.
// Дедлайн = точка отсчёта + expiresAfterMinutes, где точка отсчёта — последняя
// активность (updatedAt), но не раньше момента установки срока (expiryAnchor).
// Тот же расчёт на бэкенде — ChatExpiryService.CountFrom.

export const EXPIRY_PRESETS: { minutes: number; label: string }[] = [
  { minutes: 60, label: '1 час' },
  { minutes: 1440, label: '24 часа' },
  { minutes: 10080, label: '7 дней' },
  { minutes: 43200, label: '30 дней' },
];

export const DEFAULT_EXPIRY = 1440;

// Подпись выбранного срока жизни: «Бессрочно» / label пресета / «N мин» для нестандартного
export function expiryOptionLabel(minutes?: number | null): string {
  if (!minutes || minutes <= 0) return 'Бессрочно';
  return EXPIRY_PRESETS.find(p => p.minutes === minutes)?.label ?? `${minutes} мин`;
}

type ExpiryFields = Pick<Session, 'updatedAt' | 'expiresAfterMinutes' | 'expiryAnchor'>;

// Момент авто-удаления; null — чат не временный
export function expiresAt(session: ExpiryFields): Date | null {
  if (!session.expiresAfterMinutes || session.expiresAfterMinutes <= 0) return null;
  const updated = new Date(session.updatedAt).getTime();
  const anchor = session.expiryAnchor ? new Date(session.expiryAnchor).getTime() : 0;
  return new Date(Math.max(updated, anchor) + session.expiresAfterMinutes * 60_000);
}

// Остаток до удаления: «через 40 мин / 3 ч / 6 дн», просрочен — «скоро»
export function formatTimeLeft(session: ExpiryFields): string | null {
  const at = expiresAt(session);
  if (!at) return null;
  const leftMs = at.getTime() - Date.now();
  if (leftMs <= 0) return 'скоро';
  const min = Math.round(leftMs / 60_000);
  if (min < 60) return `через ${Math.max(min, 1)} мин`;
  const hours = Math.round(min / 60);
  if (hours < 48) return `через ${hours} ч`;
  return `через ${Math.round(hours / 24)} дн`;
}

// Дата удаления для подписи в настройках: «11 июля, 18:42»
export function formatExpiryDate(at: Date): string {
  return at.toLocaleString('ru-RU', { day: 'numeric', month: 'long', hour: '2-digit', minute: '2-digit' });
}
