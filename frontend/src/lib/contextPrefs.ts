// Глобальный стор per-user порогов индикатора заполнения контекста.
// Значения приходят с бэка через /api/auth/me (override юзера или null → дефолты),
// сохраняются через PUT /api/auth/context-thresholds.
// Паттерн — как у featureFlags.ts: модульное состояние + подписки + useSyncExternalStore.

import { useSyncExternalStore } from 'react';
import { api } from './api';
import { DEFAULT_CTX_WARN, DEFAULT_CTX_DANGER, type CtxThresholds } from './context';
import type { CacheWarnThresholds } from '../types';

const DEFAULTS: CtxThresholds = { warnPct: DEFAULT_CTX_WARN, dangerPct: DEFAULT_CTX_DANGER };

let _thresholds: CtxThresholds = DEFAULTS;
const _listeners = new Set<() => void>();

function emit() {
  _listeners.forEach(fn => fn());
}

// Установить пороги из ответа /auth/me (null/undefined → дефолты)
export function setCtxThresholdsFromServer(t?: { warnPct: number; dangerPct: number } | null) {
  _thresholds = t ? { warnPct: t.warnPct, dangerPct: t.dangerPct } : DEFAULTS;
  emit();
}

// Сохранить пороги на сервере и применить локально
export async function saveCtxThresholds(warnPct: number, dangerPct: number): Promise<void> {
  await api.auth.setContextThresholds({ warnPct, dangerPct });
  _thresholds = { warnPct, dangerPct };
  emit();
}

// Сбросить к дефолтам (на сервере стирается override)
export async function resetCtxThresholds(): Promise<void> {
  await api.auth.setContextThresholds({});
  _thresholds = DEFAULTS;
  emit();
}

export function getCtxThresholds(): CtxThresholds {
  return _thresholds;
}

export function isCtxDefaults(): boolean {
  return _thresholds.warnPct === DEFAULTS.warnPct && _thresholds.dangerPct === DEFAULTS.dangerPct;
}

function subscribe(fn: () => void): () => void {
  _listeners.add(fn);
  return () => _listeners.delete(fn);
}

// Подписка компонента на пороги
export function useCtxThresholds(): CtxThresholds {
  return useSyncExternalStore(subscribe, getCtxThresholds, getCtxThresholds);
}

// === Порог «кэш промпта скоро остынет» ===
// Тот же путь: /api/auth/me → стор, PUT /api/auth/cache-thresholds. null — дефолты
// (expiringThresholdMs без префа), поэтому значения дефолтов здесь не дублируются.

let _cacheWarn: CacheWarnThresholds | null = null;

export function setCacheThresholdsFromServer(t?: CacheWarnThresholds | null) {
  _cacheWarn = t ? { warn1hSec: t.warn1hSec, warn5mSec: t.warn5mSec } : null;
  emit();
}

export async function saveCacheThresholds(warn1hSec: number, warn5mSec: number): Promise<void> {
  await api.auth.setCacheThresholds({ warn1hSec, warn5mSec });
  _cacheWarn = { warn1hSec, warn5mSec };
  emit();
}

export async function resetCacheThresholds(): Promise<void> {
  await api.auth.setCacheThresholds({});
  _cacheWarn = null;
  emit();
}

export function getCacheThresholds(): CacheWarnThresholds | null {
  return _cacheWarn;
}

export function useCacheThresholds(): CacheWarnThresholds | null {
  return useSyncExternalStore(subscribe, getCacheThresholds, getCacheThresholds);
}
