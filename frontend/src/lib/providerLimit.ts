// Логика карточки «Продолжить на …» (provider_limit): разложение опций на секции
// (аккаунты пула подписок первыми — продолжить на своей подписке дешевле и роднее,
// затем сторонние провайдеры), подписи кнопок и доступность провайдеров. Чистые
// функции — под vitest, рендер в chat/ChatItemView.tsx (ProviderLimitCard).
import type { ProviderBalanceInfo, ProviderFallbackOption, ProviderQuotaWindow } from '../types';

export interface FallbackSections {
  // Аккаунты того же пула подписок Claude (kind='subscription')
  subscriptions: ProviderFallbackOption[];
  // Сторонние провайдеры (kind='provider' или поле не задано — старый контракт)
  providers: ProviderFallbackOption[];
}

export function splitFallbackOptions(options: ProviderFallbackOption[]): FallbackSections {
  const subscriptions: ProviderFallbackOption[] = [];
  const providers: ProviderFallbackOption[] = [];
  for (const o of options) {
    (o.kind === 'subscription' ? subscriptions : providers).push(o);
  }
  return { subscriptions, providers };
}

// Подпись кнопки аккаунта пула: тариф и утилизация — «Max 5× · 41%».
// Поля опциональны: только тариф, только процент, либо пусто.
export function formatSubscriptionMeta(option: ProviderFallbackOption): string {
  const parts: string[] = [];
  if (option.tierLabel) parts.push(option.tierLabel);
  if (option.utilization != null) parts.push(`${Math.round(option.utilization * 100)}%`);
  return parts.join(' · ');
}

// === Доступность провайдеров для карточки ===
// Источник — уже имеющиеся в приложении данные о квотах: баланс CLI-провайдера
// (/api/providers/{key}/balance — тот же, что кормит экран «Использование»).
// Аккаунты пула подписок (kind='subscription') проверке не подлежат: backend
// кладёт в карточку только здоровые.

export interface ProviderAvailabilityVerdict {
  available: boolean;
  // Для недоступного — ожидаемый момент возврата (ISO); null — неизвестен
  resetsAt: string | null;
}

// Окно квоты исчерпано: 'percent' — остаток 0, 'count' («N/M») — ноль остатка
// ИЛИ выбор лимита целиком (семантика строки источником не зафиксирована —
// граничные значения ловим обе, середина не ложноположительна ни в одной)
function quotaWindowExhausted(w: ProviderQuotaWindow): boolean {
  if (w.unit === 'percent') {
    const v = parseFloat(w.value);
    return !isNaN(v) && v <= 0;
  }
  const m = /^(\d+(?:\.\d+)?)\s*\/\s*(\d+(?:\.\d+)?)/.exec(w.value);
  if (!m) return false;
  const num = parseFloat(m[1]);
  const den = parseFloat(m[2]);
  return num <= 0 || (den > 0 && num >= den);
}

function futureReset(iso: string | null | undefined, now: number): string | null {
  if (!iso) return null;
  const t = new Date(iso).getTime();
  return !isNaN(t) && t > now ? iso : null;
}

// Вердикт доступности провайдера по его балансу. Нет баланса (источник не
// настроен, запрос не удался) — провайдер НЕ скрывается: отсутствие данных
// о квоте не доказательство паузы.
export function providerAvailabilityFromBalance(
  balance: ProviderBalanceInfo | null | undefined,
  now: number = Date.now(),
): ProviderAvailabilityVerdict {
  if (!balance) return { available: true, resetsAt: null };
  // Провайдер сам сообщает, что аккаунт недоступен
  if (!balance.available) return { available: false, resetsAt: futureReset(balance.resetsAt, now) };
  // Квотные провайдеры (GLM/Kimi/MiniMax): любое исчерпанное окно ставит на паузу.
  // Возврат — ближайший сброс среди исчерпанных; все сбросы в прошлом = данные
  // протухли (окно уже обновилось) и провайдер снова доступен
  const exhausted = (balance.windows ?? []).filter(quotaWindowExhausted);
  if (exhausted.length > 0) {
    const future = exhausted
      .map(w => futureReset(w.resetsAt, now))
      .filter((r): r is string => r !== null)
      .sort((a, b) => new Date(a).getTime() - new Date(b).getTime());
    if (future.length > 0) return { available: false, resetsAt: future[0] };
    const unknown = exhausted.some(w => futureReset(w.resetsAt, now) === null && !isPastReset(w.resetsAt, now));
    return unknown ? { available: false, resetsAt: null } : { available: true, resetsAt: null };
  }
  // Денежные провайдеры без окон квоты: нулевой баланс не даст выполнить ход
  const money = parseFloat(balance.totalBalance);
  if (!isNaN(money) && money <= 0) return { available: false, resetsAt: null };
  return { available: true, resetsAt: null };
}

function isPastReset(iso: string | null | undefined, now: number): boolean {
  if (!iso) return false;
  const t = new Date(iso).getTime();
  return !isNaN(t) && t <= now;
}

export interface HiddenProvider {
  option: ProviderFallbackOption;
  resetsAt: string | null;
}

// Провайдер, чья попытка миграции только что вернула ответ об исчерпании лимита —
// именно он вызвал(-порождает) карточку `provider_limit`, поэтому его кэшированный
// вердикт (баланс живёт 5 минут) forcibly перекрывается: splitByAvailability больше
// не поверит устаревшему available=true и не предложит ту же кнопку повторно
export function invalidateExhaustedVerdict<T extends Record<string, ProviderAvailabilityVerdict | undefined>>(
  verdicts: T,
  key: string,
  resetsAt: string | null = null,
): T {
  return { ...verdicts, [key]: { available: false, resetsAt } };
}

// Разводит сторонние провайдеры на доступные (кнопки) и скрытые (сноска).
// Опция без вердикта считается доступной — решение о скрытии принимается
// только по данным, а не по их отсутствию
export function splitByAvailability(
  providers: ProviderFallbackOption[],
  verdicts: Record<string, ProviderAvailabilityVerdict | undefined>,
): { available: ProviderFallbackOption[]; hidden: HiddenProvider[] } {
  const available: ProviderFallbackOption[] = [];
  const hidden: HiddenProvider[] = [];
  for (const p of providers) {
    const v = verdicts[p.key];
    if (v && !v.available) hidden.push({ option: p, resetsAt: v.resetsAt });
    else available.push(p);
  }
  return { available, hidden };
}

// Ближайший по времени возврата из скрытых провайдеров; без известного
// времени — null (сноска/пустое состояние идут вариантом без времени)
export function nearestReturn(hidden: HiddenProvider[]): HiddenProvider | null {
  let best: HiddenProvider | null = null;
  let bestT = Infinity;
  for (const h of hidden) {
    if (!h.resetsAt) continue;
    const t = new Date(h.resetsAt).getTime();
    if (isNaN(t) || t >= bestT) continue;
    bestT = t;
    best = h;
  }
  return best;
}

// «провайдер(а/ов)» для числа скрытых
export function providersPlural(n: number): string {
  const d10 = n % 10;
  const d100 = n % 100;
  if (d10 === 1 && d100 !== 11) return 'провайдер';
  if (d10 >= 2 && d10 <= 4 && (d100 < 12 || d100 > 14)) return 'провайдера';
  return 'провайдеров';
}

// Время возврата для фраз «вернётся …» / «освободится …»: тот же день —
// «в 14:30», другой день — «12 авг. в 14:30»
export function fmtReturnTime(resetsAt: string, now: number = Date.now()): string {
  const dt = new Date(resetsAt);
  if (isNaN(dt.getTime())) return '';
  const hhmm = dt.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
  if (dt.toDateString() === new Date(now).toDateString()) return `в ${hhmm}`;
  return `${dt.toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' })} в ${hhmm}`;
}
