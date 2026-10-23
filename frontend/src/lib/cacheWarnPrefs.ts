// Личные per-user пороги «кэш промпта скоро остынет» (фича «Индикатор prompt cache и
// перенос в новый чат», шаг F-3). Значения сидирует App.tsx из ответа GET /api/auth/me
// (поле cacheWarnThresholds, минуты; null — не настроено, применяются дефолты
// EXPIRING_THRESHOLD_MS) — тем же потоком, что и contextThresholds, поэтому смена
// пользователя перезаписывает набор, а не оставляет старый. Собственного GET /me у
// стора нет: модульный `_loaded` переживал бы logout и тянул пороги первого владельца.
// Сохранение — PUT /api/auth/cache-thresholds (пустое тело — сброс). Паттерн —
// contextPrefs.ts: модульное состояние + подписки + useSyncExternalStore.

import { useSyncExternalStore } from 'react';
import { request } from './offline';
import type { CacheWarnThresholds } from './promptCache';

// Шкала бэка (B-3, CacheWarnThresholds.InRange): часовой кэш — 1..59 мин,
// 5-минутный — 1..4 мин. За её пределами бэк отвечает 400: диалог проверяет сам,
// а текст 400 показывается как ошибка ввода, не как сбой.
export const WARN1H_MIN = 1;
export const WARN1H_MAX = 59;
export const WARN5M_MIN = 1;
export const WARN5M_MAX = 4;

let _thresholds: CacheWarnThresholds | null = null; // null — не настроено: дефолты индикатора
const _listeners = new Set<() => void>();

function emit() {
  _listeners.forEach(fn => fn());
}

// Применить из ответа /auth/me (null/undefined — не настроено)
export function setCacheWarnThresholdsFromServer(t?: CacheWarnThresholds | null): void {
  _thresholds = t ?? null;
  emit();
}

// Сохранить на сервере и применить локально. null — сброс (пустое тело `{}`).
// 400 бэка приходит Error'ом со status и текстом — вызывающий показывает его как
// ошибку ввода; состояние при этом не меняется.
export async function saveCacheWarnThresholds(t: CacheWarnThresholds | null): Promise<void> {
  const res = await request<{ cacheWarnThresholds: CacheWarnThresholds | null }>(
    '/auth/cache-thresholds', { method: 'PUT', body: JSON.stringify(t ?? {}) },
  );
  _thresholds = res?.cacheWarnThresholds ?? null;
  emit();
}

export function getCacheWarnThresholds(): CacheWarnThresholds | null {
  return _thresholds;
}

function subscribe(fn: () => void): () => void {
  _listeners.add(fn);
  return () => _listeners.delete(fn);
}

// Подписка компонента на личный порог (undefined — не настроен, дефолты индикатора).
// Наружу отдаём undefined, а не null: та же форма параметра у usePromptCache/CacheDial.
export function useCacheWarnThresholds(): CacheWarnThresholds | undefined {
  const read = () => getCacheWarnThresholds() ?? undefined;
  return useSyncExternalStore(subscribe, read, read);
}
