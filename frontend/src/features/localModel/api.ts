// Фича «Локальные модели»: тонкая обёртка над живым REST. Контракт ответа —
// DTO бэкенда (LocalModelStatsController.cs), тип LocalModelStats живёт в
// lib/api.ts (общий слой, как у соседних features: JWT и ошибки — там же).
//
// USE_MOCK = false — живые вызовы. Моки (mockLocalModelStats и др.) — только
// фикстуры тестов/витрины (localModel.fixtures.ts), в рантайм не участвуют.

import {
  api,
  type LocalModelEngine,
  type LocalModelEngineName,
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

// Тип ответа /api/local-model/stats?window=15m|1h&since=<unix ms>&engine=main|bonsai.
// Источник истины — DTO бэкенда (LocalModelStatsController.cs): время — unix-мс,
// kv — доли 0..1 (не проценты и не ГиБ), chats — projectId (не имя проекта).
// duty — блок «Дежурство» стенда второй модели (нет поля или null — блок не рисуется).
export type { LocalModelStats };

// Стенд второй модели (разрез v2, §5): список движков для переключателя и блок «Дежурство».
// Типы — в общем слое (lib/api.ts), здесь только ре-экспорт для панели и её компонентов.
export type {
  LocalModelEngine,
  LocalModelEngineName,
  LocalModelDuty,
  LocalModelDutyArbiter,
  LocalModelDutyReason,
  LocalModelDutyLease,
  LocalModelDutyAnswers,
  LocalModelDutyCall,
  LocalModelArbiterState,
  LocalModelArbiterMode,
  LocalModelChainLink,
  LocalModelJobKind,
} from '../../lib/api';

// Тип ответа /api/local-model/stats/media?since=<unix ms> (вкладка «Медиа», ComfyUI).
// Источник истины — DTO бэкенда (LocalModelStatsController.cs, LocalModelMediaResponse):
// время — unix-мс, chужие прогоны анонимны, gpus может быть null.
export type { LocalModelMedia, MediaGpu, MediaSteps, MediaRunning, MediaQueue, MediaDone };

// false — живые вызовы через общий lib/api.ts (JWT, ошибки, офлайн-кэш GET).
// true — панель и значок на моках (localModel.fixtures.ts) — режим витрины.
export const USE_MOCK = false;

// Список движков для переключателя «Основная / Bonsai» (макет §2): по нему решается,
// показывать ли переключатель и какой стенд открыть по умолчанию.
export function fetchLocalModelEngines(): Promise<LocalModelEngine[]> {
  return api.localModel.engines();
}

// Тонкая обёртка вызова: панель и значок знают только эту функцию,
// а не транспорт. Дефолт 5m — как DEFAULT_WINDOW в ThroughputChart (дублируем
// литералом, чтобы тонкая обёртка не тянула модуль графика).
// Без engine — основной стенд (прежний URL и ответ); 'bonsai' — стенд второй модели
// с блоком duty. Активный стенд опрашивается раз в 2 с, неактивный — нет (макет §6).
export function fetchLocalModelStats(
  window: '1m' | '5m' | '15m' | '1h' = '5m',
  since?: number,
  engine?: LocalModelEngineName,
): Promise<LocalModelStats> {
  return api.localModel.stats(window, since, engine);
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
