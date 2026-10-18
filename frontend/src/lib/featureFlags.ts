// Глобальный стор фич-флагов: эффективные значения per-user приходят с бэка
// (через /api/auth/me и /api/feature-flags) и раздаются компонентам хуком useFeature.
// Паттерн — как у offline.ts: модульное состояние + подписки + useSyncExternalStore.

import { useEffect, useSyncExternalStore } from 'react';
import { api } from './api';

// Тонкий реестр ключей для type-safe вызовов useFeature. Дублирует ключи из
// бэкового FeatureFlagCatalog (одна строка на флаг). Описания/дефолты/стадии
// приходят с сервера — здесь только ключи.
export const FLAGS = {
  workspaceDestructive: 'workspace-destructive',
  changeDossiersRecall: 'change-dossiers-recall',
  // Сферы: группы проектов с общей командой персон и хартией; зона персоны «Сфера».
  spheres: 'spheres',
  // Умный подбор фото персонажа под сюжет (ADR-027): метки ракурса/эмоции, подбор, «Заменить».
  characterSmartPhotos: 'character-smart-photos',
  // «Подставить персонажа» на лицо (этап 2): тап по лицу на картинке версии → выбор персонажа.
  characterFaceSwap: 'character-face-swap',
  // Статистика локальных моделей: живые метрики в чипе Ctx + панель админа по медиа-стенду ComfyUI.
  localModelStats: 'local-model-stats',
  // Режим «Исполнение фичи» в «Плане хода»: переключатель + карточка фичи (этап 2).
  turnPlanFeatureMode: 'turn-plan-feature-mode',
  // «Обзор фич»: фасет «Фичи» в списке сессий + плашка фичи в чате (волна 1, dark launch).
  featuresOverview: 'features-overview',
} as const;

export type FlagKey = (typeof FLAGS)[keyof typeof FLAGS];

let _flags: Record<string, boolean> = {};
const _listeners = new Set<() => void>();

function emit() {
  _listeners.forEach(fn => fn());
}

// Заменить весь набор флагов (на старте/после логина из ответа me)
export function setAllFlags(flags: Record<string, boolean>) {
  _flags = { ...flags };
  emit();
}

// Влить пришедшие значения поверх известных: пустой или частичный ответ не затирает
// ключи, которых в нём нет (в отличие от setAllFlags, которая заменяет набор целиком)
export function mergeFlags(flags: Record<string, boolean> | null | undefined) {
  if (!flags) return;
  _flags = { ..._flags, ...flags };
  emit();
}

// Оптимистичное локальное обновление одного флага (для мгновенной реакции тумблера)
export function setFlagLocal(key: string, value: boolean) {
  _flags = { ..._flags, [key]: value };
  emit();
}

export function getFlag(key: string): boolean {
  return _flags[key] ?? false;
}

export function getAllFlags(): Record<string, boolean> {
  return _flags;
}

export function subscribeFlags(fn: () => void): () => void {
  _listeners.add(fn);
  return () => _listeners.delete(fn);
}

// Подписка компонента на конкретный флаг. Возвращает true, если фича включена.
export function useFeature(key: FlagKey): boolean {
  return useSyncExternalStore(
    subscribeFlags,
    () => getFlag(key),
    () => getFlag(key),
  );
}

// Освежение стора: флаги читаются при старте SPA, а тумблер мог сменить другой клиент
// или прямой PUT. Дросселирование — чтобы alt-tab не дёргал сервер на каждый возврат.
export const FLAGS_REFRESH_MIN_INTERVAL_MS = 45_000;

export function createFlagsRefresher(
  fetchFlags: () => Promise<Record<string, boolean> | null | undefined>,
  now: () => number = Date.now,
  minIntervalMs: number = FLAGS_REFRESH_MIN_INTERVAL_MS,
) {
  let last = Number.NEGATIVE_INFINITY;
  let generation = 0;
  async function refresh(force = false): Promise<boolean> {
    const t = now();
    if (!force && t - last < minIntervalMs) return false;
    last = t;
    const mine = generation;
    try {
      const flags = await fetchFlags();
      // ответ после cancel (выход, смена пользователя) в стор не попадает
      if (mine === generation) mergeFlags(flags);
    } catch {
      // сбой освежения не критичен: остаются прежние значения
    }
    return true;
  }
  return Object.assign(refresh, {
    // Флаги только что получены другим путём (стартовый /auth/me): запрос не нужен
    markFresh() { last = now(); },
    // Отбросить ответы всех запросов, ещё летящих
    cancel() { generation++; },
  });
}

// Один раз на всё приложение (после авторизации): при монтировании и при возврате
// окну видимости/фокуса.
export function useFeatureFlagsRefresh(enabled: boolean) {
  useEffect(() => {
    if (!enabled) return;
    const refresh = createFlagsRefresher(() => api.auth.me().then(me => me?.featureFlags));
    // Стартовый /auth/me в App.tsx сам кладёт флаги в стор — здесь только отметка времени
    refresh.markFresh();
    const onVisible = () => { if (document.visibilityState === 'visible') void refresh(); };
    const onFocus = () => { void refresh(); };
    document.addEventListener('visibilitychange', onVisible);
    window.addEventListener('focus', onFocus);
    return () => {
      refresh.cancel();
      document.removeEventListener('visibilitychange', onVisible);
      window.removeEventListener('focus', onFocus);
    };
  }, [enabled]);
}
