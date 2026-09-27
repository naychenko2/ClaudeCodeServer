import type { RateLimitInfo, UsageSnapshot } from '../types';
import { C } from './design';

// Окно лимита подписки с вычисленными процентом и уровнем тревоги
export interface RateWindow extends RateLimitInfo {
  pct: number;                          // 0..100 (0 если процент неизвестен)
  hasUtil: boolean;                     // пришёл ли реальный utilization (при низком расходе его нет)
  level: 'normal' | 'warn' | 'danger';
  stale?: boolean;                      // окно сброшено, а свежего снимка нет — данные устарели
}

const WINDOW_LABELS: Record<string, string> = {
  five_hour: '5 часов',
  rolling_5h: '5 часов',
  seven_day: 'Неделя',
  weekly: 'Неделя',
  seven_day_opus: 'Неделя · Opus',
  seven_day_sonnet: 'Неделя · Sonnet',
  seven_day_fable: 'Неделя · Fable',
  extra_usage: 'Перерасход · месяц',
};

export function windowLabel(type: string): string {
  if (WINDOW_LABELS[type]) return WINDOW_LABELS[type];
  // Незнакомое per-model окно (seven_day_<модель>) — читаемая подпись из ключа:
  // новые недельные окна Anthropic подхватываются без правок словаря
  const m = /^seven_day_(.+)$/i.exec(type);
  if (m) return `Неделя · ${m[1].charAt(0).toUpperCase()}${m[1].slice(1).replace(/_/g, ' ')}`;
  if (/5|five|hour/i.test(type)) return '5 часов';
  if (/week|seven|day/i.test(type)) return 'Неделя';
  return type || 'Лимит';
}

// Русские подписи статуса перерасхода — сырой статус API в UI не показываем
const OVERAGE_LABELS: Record<string, string> = {
  rejected: 'перерасход недоступен',
  allowed_warning: 'перерасход почти исчерпан',
};

export function overageLabel(status?: string): string {
  return OVERAGE_LABELS[status ?? ''] ?? 'перерасход ограничен';
}

// Цвета по уровню (из палитры design.ts): норма — нейтральный, внимание — янтарь, лимит — красный
export const RATE_COLORS: Record<RateWindow['level'], { fill: string; text: string; bg: string; border: string }> = {
  normal: { fill: C.textMuted, text: C.textSecondary, bg: C.bgWhite, border: C.border },
  warn:   { fill: C.warning, text: C.warningText, bg: C.warningBg, border: C.warning },
  danger: { fill: C.danger, text: C.dangerText, bg: C.dangerBg, border: C.dangerBorder },
};

function rateLevel(w: RateLimitInfo): RateWindow['level'] {
  const u = w.utilization ?? 0;
  if (w.status === 'rejected' || w.isUsingOverage || u >= 1) return 'danger';
  if (w.status === 'allowed_warning' || u >= 0.6) return 'warn';
  return 'normal';
}

// Служебные события Anthropic, а не отдельные лимиты: seven_day_overage_included дублирует
// недельное окно (сброс тот же), а общее правило seven_day_<модель> сделало бы из него
// окно «Overage included». Режем здесь — единственная точка, через которую идут и пилюля,
// и поповер (latestPerWindow и withAccountFallback сводятся к toRateWindows)
const NON_WINDOW_TYPES = new Set(['seven_day_overage_included']);

// Преобразует карту окон в отсортированный (по использованию, убыв.) массив
export function toRateWindows(rateLimits: Record<string, RateLimitInfo>): RateWindow[] {
  return Object.values(rateLimits)
    .filter(w => !NON_WINDOW_TYPES.has(w.limitType))
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

// Последний снимок по каждому окну (для колец на экране usage), с временем снимка.
// Если самый свежий снимок окна без процента (live-события шлют utilization только у
// лимита), а раньше в ТОМ ЖЕ окне (сброс совпадает с точностью до пары минут) процент
// был — берём его: точная цифра опроса не должна затираться событием «в пределах нормы».
const SAME_WINDOW_MS = 5 * 60_000;

export function latestPerWindow(snapshots: UsageSnapshot[]): Array<RateWindow & { timestamp?: string }> {
  const latest = new Map<string, UsageSnapshot>();
  const latestWithUtil = new Map<string, UsageSnapshot>();
  for (const s of snapshots) {
    const ts = new Date(s.timestamp).getTime();
    const prev = latest.get(s.limitType);
    if (!prev || ts > new Date(prev.timestamp).getTime()) latest.set(s.limitType, s);
    if (typeof s.utilization === 'number') {
      const prevU = latestWithUtil.get(s.limitType);
      if (!prevU || ts > new Date(prevU.timestamp).getTime()) latestWithUtil.set(s.limitType, s);
    }
  }
  const map: Record<string, RateLimitInfo> = {};
  latest.forEach((s, k) => {
    let utilization = s.utilization;
    if (typeof utilization !== 'number') {
      const withUtil = latestWithUtil.get(k);
      const sameWindow = withUtil?.resetsAt && s.resetsAt
        && Math.abs(new Date(withUtil.resetsAt).getTime() - new Date(s.resetsAt).getTime()) < SAME_WINDOW_MS;
      if (sameWindow) utilization = withUtil!.utilization;
    }
    map[k] = { limitType: s.limitType, utilization, status: s.status, isUsingOverage: s.isUsingOverage, resetsAt: s.resetsAt, overageStatus: s.overageStatus, overageResetsAt: s.overageResetsAt };
  });
  return toRateWindows(map).map(w => ({ ...w, timestamp: latest.get(w.limitType)?.timestamp }));
}

// Окна чата поверх снимков аккаунта: живое событие чата — самое свежее, а если оно пришло
// без процента (в нормальном режиме так почти всегда), latestPerWindow подставит процент
// того же окна из снимка. Окна, которых в чате нет вовсе, приходят из снимка — так цифры
// видны с открытия чата, до первого хода.
// Событие чата помечается временем «сейчас», поэтому устаревшее отбрасываем, иначе оно
// перебило бы свежий снимок: окно уже сброшено (resetsAt в прошлом) или событие без процента
// и без сброса, а у аккаунта это окно есть (такому событию нечем уточнить снимок).
// Снимки аккаунта из уже сброшенного окна отбрасываются по тому же условию: старый «90%»
// после сброса — неправда. Но окно при этом не пропадает: если свежих данных о нём нет
// (опрос лимитов лёг), оно остаётся прочерком с пометкой stale — «данные устарели».
export function withAccountFallback(chat: RateLimitInfo[], accountSnapshots: UsageSnapshot[], now: string = new Date().toISOString()): RateWindow[] {
  const nowMs = new Date(now).getTime();
  const expired = (resetsAt?: string) => !!resetsAt && new Date(resetsAt).getTime() <= nowMs;
  const account = accountSnapshots.filter(s => !expired(s.resetsAt));
  const accountTypes = new Set(account.map(s => s.limitType));
  const live = chat.filter(w =>
    !expired(w.resetsAt)
    && (typeof w.utilization === 'number' || !!w.resetsAt || !accountTypes.has(w.limitType)));
  const fresh = latestPerWindow([
    ...account,
    ...live.map(w => ({
      timestamp: now, limitType: w.limitType, utilization: w.utilization, status: w.status,
      isUsingOverage: w.isUsingOverage, resetsAt: w.resetsAt, overageStatus: w.overageStatus, overageResetsAt: w.overageResetsAt,
    })),
  ]);
  const seen = new Set(fresh.map(w => w.limitType));
  const staleByType = new Map<string, UsageSnapshot>();
  for (const s of accountSnapshots) {
    if (seen.has(s.limitType) || NON_WINDOW_TYPES.has(s.limitType) || !expired(s.resetsAt)) continue;
    const prev = staleByType.get(s.limitType);
    if (!prev || new Date(s.timestamp).getTime() > new Date(prev.timestamp).getTime()) staleByType.set(s.limitType, s);
  }
  const stale: RateWindow[] = [...staleByType.values()].map(s => ({
    limitType: s.limitType, resetsAt: s.resetsAt, pct: 0, hasUtil: false, level: 'normal', stale: true,
  }));
  return [...fresh, ...stale];
}

// Короткие подписи окон для пилюли шапки (полные — windowLabel, в поповере)
const SHORT_WINDOW_LABELS: Record<string, string> = {
  five_hour: '5ч',
  rolling_5h: '5ч',
  seven_day: 'Нед',
  weekly: 'Нед',
  extra_usage: 'Доп',
};

export function shortWindowLabel(type: string): string {
  if (SHORT_WINDOW_LABELS[type]) return SHORT_WINDOW_LABELS[type];
  const m = /^seven_day_(.+)$/i.exec(type);
  if (m) return `${m[1].charAt(0).toUpperCase()}${m[1].slice(1).replace(/_/g, ' ')}`;
  if (/5|five|hour/i.test(type)) return '5ч';
  if (/week|seven|day/i.test(type)) return 'Нед';
  // Незнакомый ключ сырым идентификатором на пилюлю не пускаем — полное имя в подсказке и поповере
  return 'Др.';
}

// Порядок окон на пилюле — постоянный (5ч → неделя → по моделям → перерасход), а не по
// проценту: иначе окна прыгали бы местами по ходу разговора
function pillRank(type: string): number {
  if (/^seven_day_/i.test(type)) return 2;
  if (type === 'extra_usage') return 3;
  const s = shortWindowLabel(type);
  return s === '5ч' ? 0 : s === 'Нед' ? 1 : 4;
}

export interface RatePillSegment {
  limitType: string;
  label: string;                        // «5ч», «Нед», «Opus»
  text: string;                         // «41%», «100%+» (перерасход), «—» (процент неизвестен)
  pct: number | null;                   // 0..100 для мини-бара; null — процент неизвестен
  level: RateWindow['level'];
  stale?: boolean;                      // данные окна устарели (прочерк без мини-бара)
}

function toSegment(w: RateWindow): RatePillSegment {
  return {
    limitType: w.limitType,
    label: shortWindowLabel(w.limitType),
    text: w.hasUtil ? `${w.pct}%${w.isUsingOverage ? '+' : ''}` : '—',
    pct: w.hasUtil ? w.pct : null,
    level: w.level,
    stale: w.stale,
  };
}

// Все окна для пилюли шапки: «5ч 41% · Нед 12% · Opus 30%»
export function ratePillSegments(windows: RateWindow[]): RatePillSegment[] {
  return [...windows]
    .sort((a, b) => (pillRank(a.limitType) - pillRank(b.limitType)) || a.limitType.localeCompare(b.limitType))
    .map(toSegment);
}

// Десктоп: не больше max окон в постоянном порядке, остальные — счётчиком «+N»
export const PILL_MAX_WINDOWS = 3;

export function ratePillVisible(windows: RateWindow[], max: number = PILL_MAX_WINDOWS): { segments: RatePillSegment[]; more: number } {
  const all = ratePillSegments(windows);
  return { segments: all.slice(0, max), more: Math.max(0, all.length - max) };
}

// Сжатая форма для мобилы/планшета: худшее окно + сколько окон ещё («5ч 41% +2»)
export function ratePillCompact(windows: RateWindow[]): { head: RatePillSegment; more: number } | null {
  const worst = worstWindow(windows);
  return worst ? { head: toSegment(worst), more: windows.length - 1 } : null;
}

// Хвост «+N» с неразрывным отступом: без него выходило «41%+2», а при перерасходе «100%++2»
export function ratePillMoreText(more: number): string {
  return more > 0 ? ` +${more}` : '';
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

// Ярлыки источника снимка (UsageSnapshot.source) для подписи свежести на вкладке аккаунта
const SNAPSHOT_SOURCE_LABELS: Record<string, string> = {
  turn: 'Живой ход',
  probe: 'Пинг',
  oauth: 'OAuth-опрос',
};

// Последний по времени снимок С ПРОЦЕНТОМ — именно он говорит, насколько свежи цифры
// (rate_limit_event ходов шлёт одни resets, без utilization такой снимок не считается)
export function latestWithUtilization(snapshots: UsageSnapshot[]): UsageSnapshot | null {
  let best: UsageSnapshot | null = null;
  let bestT = 0;
  for (const s of snapshots) {
    if (typeof s.utilization !== 'number') continue;
    const t = new Date(s.timestamp).getTime();
    if (isNaN(t)) continue;
    if (!best || t > bestT) { best = s; bestT = t; }
  }
  return best;
}

// Подпись свежести данных аккаунта: «Пинг · 3 мин назад», «Живой ход · только что»;
// снимок без источника (записи до фичи) — только возраст: «12 мин назад»
export function snapshotFreshnessLabel(source: UsageSnapshot['source'], timestamp: string, now: number = Date.now()): string | null {
  const t = new Date(timestamp).getTime();
  if (isNaN(t)) return null;
  const mins = Math.max(0, Math.floor((now - t) / 60000));
  const age = mins < 1 ? 'только что' : mins < 60 ? `${mins} мин назад` : `${Math.floor(mins / 60)} ч назад`;
  const src = source ? SNAPSHOT_SOURCE_LABELS[source] : undefined;
  return src ? `${src} · ${age}` : age;
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
