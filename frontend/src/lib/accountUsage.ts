import { useSyncExternalStore } from 'react';
import type { UsageResponse, UsageSnapshot } from '../types';
import { api } from './api';

// Снимки лимитов аккаунтов (/api/usage) для шапок чатов. Один общий запрос на всех
// подписчиков: полный чат и открытые рядом панели читают одни данные. Колонки стены
// не подписываются — плашек лимитов в их узкой шапке нет.
const POLL_MS = 60_000;

let current: UsageResponse | null = null;
// время последней успешной загрузки (0 — ещё не грузили): повторная подписка в пределах
// POLL_MS не дёргает сервер, а таймер ведёт следующий запрос по расписанию, а не сразу
let lastLoadAt = 0;
// в-полёте: один запрос на всех, пока он в пути (одновременные load() — один GET)
let inflight: Promise<UsageResponse | null> | null = null;
let timer: ReturnType<typeof setInterval> | null = null;
const listeners = new Set<() => void>();

function load() {
  // Фоновая вкладка цифр не видит — не дёргаем сервер зря, догонит следующий тик
  if (typeof document !== 'undefined' && document.hidden && current) return;
  // в-полёте: запрос уже в пути — переиспользуем его, не дёргая сервер повторно
  if (inflight) return inflight;
  inflight = (async () => {
    try {
      const u = await api.usage.get();
      current = u;
      lastLoadAt = Date.now();
      listeners.forEach(l => l());
      return u;
    } catch {
      // лимиты — необязательная информация: остаются прежние цифры
      return null;
    } finally {
      inflight = null;
    }
  })();
  return inflight;
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  if (listeners.size === 1) {
    // повторная подписка: данные свежие (загружали < POLL_MS назад) — сервер не дёргаем
    if (!(current && Date.now() - lastLoadAt < POLL_MS)) load();
    // таймер: следующий запрос по расписанию (через POLL_MS), а не сразу
    if (timer) clearInterval(timer);
    timer = setInterval(load, POLL_MS);
  }
  return () => {
    listeners.delete(listener);
    if (listeners.size === 0 && timer) { clearInterval(timer); timer = null; }
  };
}

const noopSubscribe = () => () => {};
const getCurrent = () => current;
const getNull = () => null;

// enabled=false — хук не подписывается и не ходит в сеть (чат стороннего провайдера).
// useSyncExternalStore отдаёт последнее известное сразу, в том числе при повторной подписке
export function useAccountUsage(enabled: boolean): UsageResponse | null {
  return useSyncExternalStore(enabled ? subscribe : noopSubscribe, enabled ? getCurrent : getNull);
}

// Снимки аккаунта, который обслуживает чат. С пулом подписок — строго свой аккаунт:
// чужой процент хуже, чем никакого. Без пула — общие снимки минус сторонние провайдеры.
export function accountSnapshotsFor(usage: UsageResponse | null, subscriptionKey: string): UsageSnapshot[] {
  if (!usage) return [];
  if (usage.subscriptions) return usage.subscriptions[subscriptionKey]?.snapshots ?? [];
  const provKeys = new Set(Object.keys(usage.providers ?? {}));
  return usage.snapshots.filter(s => !s.subscriptionKey || !provKeys.has(s.subscriptionKey));
}

// Тестовые швы: подписка/отписка и сброс состояния между тестами.
export const __test = {
  subscribe,
  reset() {
    current = null;
    lastLoadAt = 0;
    if (timer) { clearInterval(timer); timer = null; }
    inflight = null;
    listeners.clear();
  },
};
