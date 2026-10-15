import type { ChatItem, PromptCacheInfo } from '../types';

// Модель индикатора TTL prompt cache (шаг 2 плана «индикатор TTL prompt cache»).
// Источник — result.promptCache: бэкенд кладёт его только для родного Claude (TTL из
// ephemeral_*, якорь — приход message_start последнего вызова основного агента с кэшем,
// причина промаха — diagnostics.cache_miss_reason первого вызова хода). Вёрстки здесь нет:
// чистая функция над лентой, UI (чип в шапке, попап, подвал хода) строится поверх неё.

export type PromptCacheStatus = 'warm' | 'expiring' | 'cold' | 'unknown';

export interface PromptCacheMiss {
  reason: string;
  tokens?: number;
  label: string;         // человеческий текст причины
}

export interface PromptCacheState {
  status: PromptCacheStatus;
  ttl?: '1h' | '5m';
  anchorAtMs?: number;
  expiresAt?: number;    // момент, после которого кэш считаем остывшим (с запасом, см. marginMs)
  remainingMs?: number;  // сколько осталось до expiresAt; 0 — остыл
  prefixTokens?: number; // сколько перепишет следующий ход, если кэш остыл
  lastMiss?: PromptCacheMiss; // последний промах кэша в чате
}

// Кэш одного хода для подвала поста: чтение/запись из usage result и причина промаха
// первого вызова хода (только у родного Claude)
export interface TurnCacheInfo {
  read: number;
  creation: number;
  miss?: { label: string; tokens?: number };
}

const TTL_MS: Record<'1h' | '5m', number> = { '1h': 60 * 60_000, '5m': 5 * 60_000 };

// Запас на время до первого токена: якорь — приход message_start, а кэш живёт от начала
// запроса. Минута для часового кэша; у 5-минутного минута съела бы пятую часть срока,
// поэтому запас не больше десятой доли TTL.
export function marginMs(ttlMs: number): number {
  return Math.min(60_000, ttlMs / 10);
}

// Порог «остывает»: 5 минут у часового кэша, пятая доля TTL у короткого (иначе 5m был бы
// «остывающим» всю жизнь).
export function expiringThresholdMs(ttlMs: number): number {
  return Math.min(5 * 60_000, ttlMs / 5);
}

// Причины промаха из diagnostics.cache_miss_reason.type. Незнакомая причина показывается как есть.
const MISS_LABELS: Record<string, string> = {
  previous_message_not_found: 'истёк срок кэша',
  system_changed: 'сменился системный промпт',
  model_changed: 'смена модели',
  tools_changed: 'сменились инструменты',
  messages_changed: 'изменилась история сообщений',
  unavailable: 'причина неизвестна',
};

export function missReasonLabel(reason: string): string {
  return MISS_LABELS[reason] ?? reason;
}

// Остаток кэша для чипа: минуты с округлением вверх — «1 мин» до самого остывания
export function cacheLeftText(remainingMs: number): string {
  return `${Math.max(1, Math.ceil(remainingMs / 60_000))} мин`;
}

// Множители тарифа Anthropic к базовой цене входа: выход ×5, чтение кэша ×0,1, запись
// кэша ×1,25 на 5 минут и ×2 на час. Абсолютных цен фронт не знает — базу выводим из
// фактической стоимости последнего хода этого же чата.
const OUTPUT_MULT = 5;
const READ_MULT = 0.1;
const WRITE_MULT: Record<'1h' | '5m', number> = { '1h': 2, '5m': 1.25 };

// Примерная цена перезаписи остывшего кэша следующим ходом, $. undefined — оценить не по
// чему (нет хода родного Claude со стоимостью и расходом). Расход хода суммирует и
// сабагентов на других моделях, поэтому это ориентир, а не счёт.
export function rewriteCostUsd(items: ChatItem[], ttl: '1h' | '5m', prefixTokens: number): number | undefined {
  if (!(prefixTokens > 0)) return undefined;
  for (let i = items.length - 1; i >= 0; i--) {
    const it = items[i];
    if (it.kind !== 'result' || !it.promptCache || !it.usage || !(it.totalCostUsd && it.totalCostUsd > 0)) continue;
    const u = it.usage;
    const write = WRITE_MULT[normTtl(it.promptCache.ttl) ?? ttl];
    const units = u.inputTokens + OUTPUT_MULT * u.outputTokens + READ_MULT * u.cacheReadTokens + write * u.cacheCreationTokens;
    if (!(units > 0)) continue;
    return (it.totalCostUsd / units) * WRITE_MULT[ttl] * prefixTokens;
  }
  return undefined;
}

function normTtl(ttl: PromptCacheInfo['ttl']): '1h' | '5m' | undefined {
  return ttl === '1h' || ttl === '5m' ? ttl : undefined;
}

export function promptCacheState(items: ChatItem[], now: number): PromptCacheState {
  let anchor: PromptCacheInfo | undefined;
  let ttl: '1h' | '5m' | undefined;
  let lastMiss: PromptCacheMiss | undefined;

  // Обратный проход по result'ам. Result без promptCache:
  // - с contextTokens — настоящий ход, но не родного Claude (сторонний провайдер, локальный
  //   движок) либо история до этого поля: про кэш Anthropic сказать нечего → стоп, unknown;
  // - без contextTokens (компакт-ход, пустой ход) — пропускаем: якорь он не сбрасывает.
  for (let i = items.length - 1; i >= 0; i--) {
    const it = items[i];
    if (it.kind !== 'result') continue;
    const pc = it.promptCache;
    if (!pc) {
      if (!anchor && it.contextTokens && it.contextTokens > 0) break;
      continue;
    }
    if (!lastMiss && pc.missReason)
      lastMiss = { reason: pc.missReason, tokens: pc.missedTokens ?? undefined, label: missReasonLabel(pc.missReason) };
    if (!anchor && typeof pc.anchorAtMs === 'number') anchor = pc;
    // Вызов, который только читал кэш, TTL не несёт — наследуем от последней записи
    if (anchor && !ttl) ttl = normTtl(pc.ttl);
    if (anchor && ttl && lastMiss) break;
  }

  if (!anchor || !ttl || typeof anchor.anchorAtMs !== 'number')
    return { status: 'unknown', lastMiss };

  const ttlMs = TTL_MS[ttl];
  const expiresAt = anchor.anchorAtMs + ttlMs - marginMs(ttlMs);
  const remainingMs = Math.max(0, expiresAt - now);
  const status: PromptCacheStatus =
    remainingMs <= 0 ? 'cold'
    : remainingMs < expiringThresholdMs(ttlMs) ? 'expiring'
    : 'warm';

  return {
    status,
    ttl,
    anchorAtMs: anchor.anchorAtMs,
    expiresAt,
    remainingMs,
    prefixTokens: anchor.prefixTokens > 0 ? anchor.prefixTokens : undefined,
    lastMiss,
  };
}
