// Фича «Локальные модели»: тонкая обёртка над живым REST. Контракт ответа —
// DTO бэкенда (LocalModelStatsController.cs), тип LocalModelStats живёт в
// lib/api.ts (общий слой, как у соседних features: JWT и ошибки — там же).
//
// USE_MOCK = false — живые вызовы. Моки (mockLocalModelStats и др.) — только
// фикстуры тестов/витрины (localModel.fixtures.ts), в рантайм не участвуют.

import {
  api,
  type LocalModelMedia,
  type LocalModelStats,
  type MediaDone,
  type MediaGpu,
  type MediaHistoryPage,
  type MediaHistoryQuery,
  type MediaHistoryRecord,
  type MediaQueue,
  type MediaRunning,
  type MediaSteps,
} from '../../lib/api';

// Тип ответа /api/local-model/stats?window=15m|1h&since=<unix ms>.
// Источник истины — DTO бэкенда (LocalModelStatsController.cs): время — unix-мс,
// kv — доли 0..1 (не проценты и не ГиБ), chats — projectId (не имя проекта).
export type { LocalModelStats };

// Тип ответа /api/local-model/stats/media?since=<unix ms> (вкладка «Медиа», ComfyUI).
// Источник истины — DTO бэкенда (LocalModelStatsController.cs, LocalModelMediaResponse):
// время — unix-мс, chужие прогоны анонимны, gpus может быть null.
export type { LocalModelMedia, MediaGpu, MediaSteps, MediaRunning, MediaQueue, MediaDone };

// false — живые вызовы через общий lib/api.ts (JWT, ошибки, офлайн-кэш GET).
// true — панель и значок на моках (localModel.fixtures.ts) — режим витрины.
export const USE_MOCK = false;

// Тонкая обёртка вызова: панель и значок знают только эту функцию,
// а не транспорт. Дефолт 5m — как DEFAULT_WINDOW в ThroughputChart (дублируем
// литералом, чтобы тонкая обёртка не тянула модуль графика).
export function fetchLocalModelStats(window: '1m' | '5m' | '15m' | '1h' = '5m', since?: number): Promise<LocalModelStats> {
  return api.localModel.stats(window, since);
}

// Вкладка «Медиа»: снимок ComfyUI. since — unix-мс, фильтрует lastHour.
export function fetchLocalModelMedia(since?: number): Promise<LocalModelMedia> {
  return api.localModel.media(since);
}

// История медиа-генераций (ADR-030): страница 7-дневного журнала. Пустой q —
// вся история за 7 дней. Ошибки — как у request: status 403/404/410 на поле
// ошибки, body 410 — { reason }.
export function fetchMediaHistory(q?: MediaHistoryQuery): Promise<MediaHistoryPage> {
  return api.localModel.history(q);
}

export type { MediaHistoryRecord, MediaHistoryPage, MediaHistoryQuery };
