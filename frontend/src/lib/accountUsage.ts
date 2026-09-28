import { useSyncExternalStore } from 'react';
import type { UsageResponse, UsageSnapshot } from '../types';
import { api } from './api';

// Снимки лимитов аккаунтов (/api/usage) для шапок чатов. Один общий запрос на всех
// подписчиков: полный чат и открытые рядом панели читают одни данные. Колонки стены
// не подписываются — плашек лимитов в их узкой шапке нет.
const POLL_MS = 60_000;

let current: UsageResponse | null = null;
let timer: ReturnType<typeof setInterval> | null = null;
const listeners = new Set<() => void>();

function load() {
  // Фоновая вкладка цифр не видит — не дёргаем сервер зря, догонит следующий тик
  if (typeof document !== 'undefined' && document.hidden && current) return;
  api.usage.get()
    .then(u => { current = u; listeners.forEach(l => l()); })
    .catch(() => { /* лимиты — необязательная информация: остаются прежние цифры */ });
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  if (listeners.size === 1) {
    load();
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
