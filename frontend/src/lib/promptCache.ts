import type { ChatItem } from '../types';

// Фронтовый контракт prompt cache (фича «Индикатор prompt cache и перенос в новый чат»,
// шаг F-1a). Форма полей повторяет бэкенд PromptCacheState из Protocol/ServerMessage.cs:
// на проводе camelCase. Вёрстки здесь нет: чистая функция над лентой, UI шапки (F-1b)
// строится поверх неё.

export type PromptCacheMissReason =
  | 'none'
  | 'systemChanged'
  | 'messagesChanged'
  | 'unavailable'
  | 'previousMessageNotFound'
  | 'modelChanged'
  | 'toolsChanged';

// Состояние кэша в result-сообщении хода (бэкенд PromptCacheState). null у поля — ход
// без сигнала кэша (сторонний провайдер, локальный движок, история до появления поля).
// anchorAtMs — серверное UTC-время (мс) прихода message_start последнего вызова
// основного агента, касавшегося кэша; ttl — срок последней записи («1h» | «5m»),
// null — записи за жизнь процесса ещё не видели.
export interface PromptCacheWire {
  readTokens: number;
  writtenTokens: number;
  anchorAtMs?: number;
  ttl?: string;
  missedTokens?: number;
  missReason: PromptCacheMissReason;
}

// Состояние циферблата: тёплый / остывает / холодный. «Идёт ход» — не статус ленты,
// его даёт вызывающий (isWaiting), хук кэш-данных за время хода не трогает.
export type PromptCacheStatus = 'warm' | 'cooling' | 'cold';

export interface PromptCacheMissInfo {
  reason: PromptCacheMissReason;
  tokens?: number;
  label: string; // человеческая подпись причины
}

// null — индикатора нет: в ленте ещё не было result'а с данными кэша (см. promptCacheState).
export interface PromptCacheState {
  status: PromptCacheStatus;
  ttl: '1h' | '5m';
  anchorAtMs: number;
  expiresAt: number; // anchorAtMs + срок TTL; после — кэш остыл
  remainingMs: number; // сколько осталось; 0 — остыл
  readTokens: number; // чтение кэша последнего хода
  writtenTokens: number; // запись кэша последнего хода
  lastMiss?: PromptCacheMissInfo; // последний промах кэша в чате
}

const TTL_MS: Record<'1h' | '5m', number> = { '1h': 60 * 60_000, '5m': 5 * 60_000 };

// Личный порог «остывает» (B-3): минуты остатка до остывания, отдельно для часового и
// 5-минутного срока. Форма — та же, что поле cacheWarnThresholds в ответе /me.
export interface CacheWarnThresholds {
  warn1hMin?: number;
  warn5mMin?: number;
}

// Дефолт порога «остывает», пока личный не подключён: 5 минут у часового кэша, пятая
// доля срока у короткого — иначе 5m был бы «остывающим» всю жизнь.
export const EXPIRING_THRESHOLD_MS: Record<'1h' | '5m', number> = {
  '1h': 5 * 60_000,
  '5m': 60_000,
};

// Запас на время до первого токена: якорь — приход message_start, а кэш живёт от начала
// запроса: минута у часового, полминуты у 5-минутного (десятая доля срока). Личный порог
// зажимается видимым сроком минус этот запас — те же границы держит бэкенд (B-3).
function marginMs(ttlMs: number): number {
  return Math.min(60_000, Math.floor(ttlMs / 10));
}

// Порог «остывает»: дефолт — константа; личный (минуты) зажимается в [30 с, срок − запас]
// — меньше полминуты «остывание» мелькнуло бы, а порог не ниже срока означал бы
// «остывающий» с момента записи. Значения вне диапазона с провода — зажать, не выбросить.
export function expiringThresholdMs(ttl: '1h' | '5m', warn?: CacheWarnThresholds): number {
  const prefMin = ttl === '1h' ? warn?.warn1hMin : warn?.warn5mMin;
  if (prefMin === undefined || !Number.isFinite(prefMin)) return EXPIRING_THRESHOLD_MS[ttl];
  const hi = TTL_MS[ttl] - marginMs(TTL_MS[ttl]);
  return Math.min(hi, Math.max(30_000, prefMin * 60_000));
}

// Статус по остатку: ноль — холодный, не выше порога — остывает, выше — тёплый.
export function statusForRemaining(remainingMs: number, ttl: '1h' | '5m', warn?: CacheWarnThresholds): PromptCacheStatus {
  return remainingMs <= 0 ? 'cold' : remainingMs <= expiringThresholdMs(ttl, warn) ? 'cooling' : 'warm';
}

// Живое состояние на момент now: меняются только остаток и статус, остальное как было.
// Считает циферблат (и поповер при открытии), не вся пилюля.
export function liveState(state: PromptCacheState, now: number, warn?: CacheWarnThresholds): PromptCacheState {
  const remainingMs = Math.max(0, state.expiresAt - now);
  return { ...state, remainingMs, status: statusForRemaining(remainingMs, state.ttl, warn) };
}

// Подписи причин промаха (там имена snake_case, на проводе camelCase — маппинг явный).
// Все — именными фразами одного вида («смена модели», а не «сменилась модель»): в UI
// читаются как «промах: …». «none» подписи не имеет: промаха не было. Незнакомая
// причина показывается как есть.
const MISS_LABELS: Record<string, string> = {
  systemChanged: 'смена системного промпта',
  messagesChanged: 'изменение истории сообщений',
  unavailable: 'неизвестная причина',
  previousMessageNotFound: 'истечение срока кэша',
  modelChanged: 'смена модели',
  toolsChanged: 'смена инструментов',
};

export function missReasonLabel(reason: PromptCacheMissReason): string {
  return MISS_LABELS[reason] ?? reason;
}

// TTL с провода — только «1h»/«5m», всё остальное (в том числе null) — не срок
export function normTtl(ttl?: string): '1h' | '5m' | undefined {
  return ttl === '1h' || ttl === '5m' ? ttl : undefined;
}

// Состояние по последнему result'у ленты с данными кэша. Обратный проход: result'ы
// без promptCache (сторонний провайдер, локальная модель, старая история) и без
// якоря/срока пропускаются — индикатор берётся по последнему, который их несёт. Ход
// на провайфере без сигнала кэша не гасит живой индикатор: кэш привязан к разговору,
// не к провайдеру. Ни одного result с данными — пустое состояние.
// warnThresholds — личный порог «остывает» (B-3, User.cacheWarnThresholds), применяется
// по ttl; не задан — дефолты EXPIRING_THRESHOLD_MS.
export function promptCacheState(items: ChatItem[], now: number, warnThresholds?: CacheWarnThresholds): PromptCacheState | null {
  let lastMiss: PromptCacheMissInfo | undefined;
  let state: PromptCacheState | null = null;
  for (let i = items.length - 1; i >= 0; i--) {
    // Оба факта собраны — более старые result'ы уже не интересны.
    if (state && lastMiss) break;
    const it = items[i];
    if (it.kind !== 'result') continue;
    const pc = it.promptCache;
    if (!pc) continue;
    // Промах — из самого свежего result'а с причиной, даже без якоря/срока и даже
    // старше решающего: он в ленте есть независимо от того, есть что показать.
    if (!lastMiss && pc.missReason !== 'none') {
      lastMiss = {
        reason: pc.missReason,
        tokens: pc.missedTokens ?? undefined,
        label: missReasonLabel(pc.missReason),
      };
    }
    if (state) continue;
    const ttl = normTtl(pc.ttl);
    if (typeof pc.anchorAtMs !== 'number' || !ttl) continue;

    const remainingMs = Math.max(0, pc.anchorAtMs + TTL_MS[ttl] - now);
    const status = statusForRemaining(remainingMs, ttl, warnThresholds);

    state = {
      status,
      ttl,
      anchorAtMs: pc.anchorAtMs,
      expiresAt: pc.anchorAtMs + TTL_MS[ttl],
      remainingMs,
      readTokens: pc.readTokens,
      writtenTokens: pc.writtenTokens,
    };
  }
  return state ? { ...state, lastMiss } : null;
}
