import type { RateLimitInfo, UsageSnapshot } from '../types';

// Окно лимита подписки с вычисленными процентом и уровнем тревоги
export interface RateWindow extends RateLimitInfo {
  pct: number;                          // 0..100 (0 если процент неизвестен)
  hasUtil: boolean;                     // пришёл ли реальный utilization (при низком расходе его нет)
  level: 'normal' | 'warn' | 'danger';
}

const WINDOW_LABELS: Record<string, string> = {
  five_hour: '5 часов',
  rolling_5h: '5 часов',
  seven_day: 'Неделя',
  weekly: 'Неделя',
};

export function windowLabel(type: string): string {
  if (WINDOW_LABELS[type]) return WINDOW_LABELS[type];
  if (/5|five|hour/i.test(type)) return '5 часов';
  if (/week|seven|day/i.test(type)) return 'Неделя';
  return type || 'Лимит';
}

// Цвета по уровню (из палитры design.ts): норма — нейтральный, внимание — янтарь, лимит — красный
export const RATE_COLORS: Record<RateWindow['level'], { fill: string; text: string; bg: string; border: string }> = {
  normal: { fill: '#9A8F7E', text: '#756B5E', bg: '#FFFFFF', border: '#E0D7C8' },
  warn:   { fill: '#C9923E', text: '#8A6A28', bg: '#FBEFE0', border: '#EAD2A0' },
  danger: { fill: '#B4452F', text: '#C0392B', bg: '#FBF1EC', border: '#F5C6BF' },
};

function rateLevel(w: RateLimitInfo): RateWindow['level'] {
  const u = w.utilization ?? 0;
  if (w.status === 'rejected' || w.isUsingOverage || u >= 1) return 'danger';
  if (w.status === 'allowed_warning' || u >= 0.6) return 'warn';
  return 'normal';
}

// Преобразует карту окон в отсортированный (по использованию, убыв.) массив
export function toRateWindows(rateLimits: Record<string, RateLimitInfo>): RateWindow[] {
  return Object.values(rateLimits)
    .filter(w => typeof w.utilization === 'number' || !!w.status)
    .map(w => ({
      ...w,
      pct: Math.round(Math.min(1, Math.max(0, w.utilization ?? 0)) * 100),
      hasUtil: typeof w.utilization === 'number',
      level: rateLevel(w),
    }))
    .sort((a, b) => (b.utilization ?? 0) - (a.utilization ?? 0));
}

// «Худшее» окно: сначала по уровню тревоги, затем по использованию
export function worstWindow(windows: RateWindow[]): RateWindow | undefined {
  const rank = { danger: 2, warn: 1, normal: 0 };
  return [...windows].sort((a, b) => (rank[b.level] - rank[a.level]) || ((b.utilization ?? 0) - (a.utilization ?? 0)))[0];
}

// Последний снимок по каждому окну (для колец на экране usage), с временем снимка
export function latestPerWindow(snapshots: UsageSnapshot[]): Array<RateWindow & { timestamp?: string }> {
  const latest = new Map<string, UsageSnapshot>();
  for (const s of snapshots) {
    const prev = latest.get(s.limitType);
    if (!prev || new Date(s.timestamp).getTime() > new Date(prev.timestamp).getTime()) latest.set(s.limitType, s);
  }
  const map: Record<string, RateLimitInfo> = {};
  latest.forEach((s, k) => {
    map[k] = { limitType: s.limitType, utilization: s.utilization, status: s.status, isUsingOverage: s.isUsingOverage, resetsAt: s.resetsAt, overageStatus: s.overageStatus, overageResetsAt: s.overageResetsAt };
  });
  return toRateWindows(map).map(w => ({ ...w, timestamp: latest.get(w.limitType)?.timestamp }));
}

// Точки {время(мс), доля} по каждому окну, отсортированные — для спарклайна тренда
export function seriesByWindow(snapshots: UsageSnapshot[]): Record<string, { t: number; u: number }[]> {
  const out: Record<string, { t: number; u: number }[]> = {};
  for (const s of snapshots) {
    if (typeof s.utilization !== 'number') continue;
    (out[s.limitType] ??= []).push({ t: new Date(s.timestamp).getTime(), u: s.utilization });
  }
  for (const k of Object.keys(out)) out[k].sort((a, b) => a.t - b.t);
  return out;
}

// Время сброса окна: относительное (<6ч) либо абсолютное
export function fmtReset(resetsAt?: string): string {
  if (!resetsAt) return '';
  const t = new Date(resetsAt).getTime();
  if (isNaN(t)) return '';
  const diff = t - Date.now();
  if (diff <= 0) return 'скоро';
  if (diff < 6 * 3600_000) {
    const h = Math.floor(diff / 3600_000);
    const m = Math.floor((diff % 3600_000) / 60_000);
    return h > 0 ? `через ${h}ч ${m}м` : `через ${m}м`;
  }
  const d = new Date(t);
  const hhmm = d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
  return d.toDateString() === new Date().toDateString()
    ? `в ${hhmm}`
    : `${d.toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' })}, ${hhmm}`;
}
