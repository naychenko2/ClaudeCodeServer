// Фича «Локальные модели»: тонкая обёртка над живым REST. Контракт ответа —
// DTO бэкенда (LocalModelStatsController.cs), тип LocalModelStats живёт в
// ./types.ts (слой фичи, как у соседних features: JWT и ошибки — там же).
//
// USE_MOCK = false — живые вызовы. Моки (mockLocalModelStats и др.) — только
// фикстуры тестов/витрины (localModel.fixtures.ts), в рантайм не участвуют.

import { request } from '../../lib/offline';
import type {
  CloudAnswerSpeedResponse,
  LocalModelEngine,
  LocalModelEngineName,
  LocalModelMedia,
  LocalModelStats,
  LocalModelTurnResponse,
  MediaDone,
  MediaGpu,
  MediaHistoryPage,
  MediaHistoryQuery,
  MediaHistoryRecord,
  MediaQueue,
  MediaRunning,
  MediaSteps,
  TranscriptionSnapshot,
} from './types';

// Методы REST «Локальных моделей»; lib/api.ts подмешивает их в объект api spread'ом
// (localModelApi.localModel.* остаётся прежним адресом для всех потребителей).
export const localModelApi = {
  // «Локальные модели» (фича local-model-stats): снимок панели — только админ
  // (настройка глобальная), итог последнего хода чата — его владелец. DTO — записи
  // LocalModelStatsController / SessionLocalModelController (типы выше).
  localModel: {
    // GET /api/local-model/engines: список движков для переключателя вкладок моделей
    // (макет §2). Один configured — переключателя нет, стенд того движка открывается сразу.
    // live: true — тот же класс, что у /video/* и статуса выкатки: список запрашивается
    // при каждом открытии панели (useLocalModelEngines — один вызов, без таймера;
    // 2 с — опрос /stats внутри вкладки, 15 с — опрос значка в рельсе), и подставленный
    // из IndexedDB список сказал бы «второй движок есть» про движок, который уже выключили
    // (или наоборот).
    engines: () => request<LocalModelEngine[]>('/local-model/engines', { live: true }),
    // GET /api/local-model/stats?window=1m|5m|15m|1h&since=<unix ms>&engine=main|<имя движка>.
    // Без engine — основной стенд, URL и ответ прежние (обратная совместимость, разрез v2, §5).
    // engine — только ключ словаря ('main' или имя движка LocalEngines), не имя модели и не URL.
    // live: true — снимок стенда отвечает за «что происходит СЕЙЧАС»: ключ кэша зависит от
    // window/since/engine, поэтому опрос ещё и копил бы по записи на каждый тик, а при обрыве
    // связи подставлял бы СТАРОЕ состояние второй движок как текущее — ровно то вранье, на котором
    // держится вся панель.
    stats: (window: '1m' | '5m' | '15m' | '1h' = '15m', since?: number, engine?: LocalModelEngineName) => {
      const qs = new URLSearchParams({ window });
      if (since != null) qs.set('since', String(since));
      if (engine != null) qs.set('engine', engine);
      return request<LocalModelStats>(`/local-model/stats?${qs.toString()}`, { live: true });
    },
    // GET /api/local-model/hardware?window=1m|5m|15m|1h&node=<key> — снимок ОДНОГО узла
    // (вкладки «Железо»/«Стенды»; только админ: 403 не-админу, 404 без флага). window — окно
    // графика «Температуры» (дефолт 15m). node — ключ узла из реестра бэкенда; без node
    // берётся worker — URL и ответ прежние (обратная совместимость). Неизвестный node — 404.
    // Тип ответа — в feature-слое (hardwareTypes.ts); type-only импорт, без цикла рантайма.
    hardware: (window: '1m' | '5m' | '15m' | '1h' = '15m', node?: string) => {
      const nodeQs = node != null ? `&node=${encodeURIComponent(node)}` : '';
      return request<import('./hardwareTypes').HardwareReport>(`/local-model/hardware?window=${window}${nodeQs}`);
    },
    // GET /api/local-model/hardware/farm — сводка всех узлов фермы (карточка «Ферма»,
    // макет А; только админ). Порядок farmNodes — как в конфиге бэкенда.
    hardwareFarm: () => request<import('./hardwareTypes').HardwareFarm>('/local-model/hardware/farm'),
    // GET /api/sessions/{id}/local-model: lastTurn — null, если ходов не было
    // Статистика последнего хода — по-чатовой класс: в офлайне она полезна («где шёл ход»),
    // а весит меньше сотни байт.
    lastTurn: (sessionId: string) =>
      request<LocalModelTurnResponse>(`/sessions/${encodeURIComponent(sessionId)}/local-model`, { chatScope: sessionId }),
    // GET /api/local-model/stats/media?since=<unix ms>: вкладка «Медиа» (ComfyUI).
    // since — unix-мс, фильтрует lastHour (только завершённые новее момента).
    media: (since?: number) => {
      const qs = new URLSearchParams();
      if (since != null) qs.set('since', String(since));
      const suffix = qs.toString() ? `?${qs.toString()}` : '';
      return request<LocalModelMedia>(`/local-model/stats/media${suffix}`);
    },
    // GET /api/local-model/transcription: снимок конвейера транскрибации (вкладка
    // «Встречи», предложение v1): только админ, опрос раз в 5 с при открытой вкладке.
    // live: true — тот же смысл, что у /stats: снимок отвечает за «что происходит
    // СЕЙЧАС», и подставленный из IndexedDB ответ сказал бы «конвейер жив» про конвейер,
    // который уже встал, — ровно то вранье, ради которого вкладка и сделана.
    transcription: () => request<TranscriptionSnapshot>('/local-model/transcription', { live: true }),
    // GET /api/local-model/history: история медиа-генераций за 7 дней (ADR-030).
    // Пустые фильтры не передаются; без query — вся история за 7 дней, страница 50.
    history: (q?: MediaHistoryQuery) => {
      const qs = new URLSearchParams();
      if (q) {
        if (q.kind) qs.set('kind', q.kind);
        if (q.model) qs.set('model', q.model);
        if (q.outcome) qs.set('outcome', q.outcome);
        if (q.login) qs.set('login', q.login);
        if (q.from) qs.set('from', q.from);
        if (q.to) qs.set('to', q.to);
        if (q.cursor) qs.set('cursor', q.cursor);
        if (q.limit != null) qs.set('limit', String(q.limit));
      }
      const suffix = qs.toString() ? `?${qs.toString()}` : '';
      return request<MediaHistoryPage>(`/local-model/history${suffix}`);
    },
  },
};


// Тип ответа /api/local-model/stats?window=15m|1h&since=<unix ms>&engine=main|<имя движка>.
// Источник истины — DTO бэкенда (LocalModelStatsController.cs): время — unix-мс,
// kv — доли 0..1 (не проценты и не ГиБ), chats — projectId (не имя проекта).
// duty — блок «Дежурство» стенда второй модели (нет поля или null — блок не рисуется).
export type { LocalModelStats };

// Стенд второй модели (разрез v2, §5): список движков для переключателя и блок «Дежурство».
// Типы — в ./types.ts, здесь только ре-экспорт для панели и её компонентов;
// lib/api.ts переэкспортирует их через `export type *` (адрес типов прежний).
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
} from './types';

// Тип ответа /api/local-model/stats/media?since=<unix ms> (вкладка «Медиа», ComfyUI).
// Источник истины — DTO бэкенда (LocalModelStatsController.cs, LocalModelMediaResponse):
// время — unix-мс, chужие прогоны анонимны, gpus может быть null.
export type { LocalModelMedia, MediaGpu, MediaSteps, MediaRunning, MediaQueue, MediaDone };

// Типы ответа /api/local-model/transcription (вкладка «Встречи»). Источник истины —
// таблица раздела «Черновик данных с бэкенда» предложения v1
// (docs/mockups/local-model-transcription-proposal.md); бэкенда ещё нет, форма полей
// сверяется при его постановке.
export type {
  TranscriptionSnapshot,
  TranscriptionPhase,
  TranscriptionStage,
  TranscriptionLevel,
  TranscriptionDepState,
  TranscriptionQueuedFile,
  TranscriptionFail,
  TranscriptionMeeting,
  TranscriptionDifyRow,
  TranscriptionDep,
} from './types';

// false — живые вызовы напрямую через `request` из lib/offline (JWT, ошибки, офлайн-кэш GET),
// минуя `api` (иначе цикл: api подмешивает этот модуль).
// true — панель и значок на моках (localModel.fixtures.ts) — режим витрины.
export const USE_MOCK = false;

// Список движков для переключателя вкладок моделей (макет §2): по нему решается,
// показывать ли переключатель и какой стенд открыть по умолчанию.
export function fetchLocalModelEngines(): Promise<LocalModelEngine[]> {
  return localModelApi.localModel.engines();
}

// Тонкая обёртка вызова: панель и значок знают только эту функцию,
// а не транспорт. Дефолт 5m — как DEFAULT_WINDOW в ThroughputChart (дублируем
// литералом, чтобы тонкая обёртка не тянула модуль графика).
// Без engine — основной стенд (прежний URL и ответ); имя движка LocalEngines (например 'gemma') — стенд второй модели
// с блоком duty. Активный стенд опрашивается раз в 2 с, неактивный — нет (макет §6).
export function fetchLocalModelStats(
  window: '1m' | '5m' | '15m' | '1h' = '5m',
  since?: number,
  engine?: LocalModelEngineName,
): Promise<LocalModelStats> {
  return localModelApi.localModel.stats(window, since, engine);
}

// Вкладка «Медиа»: снимок ComfyUI. since — unix-мс, фильтрует lastHour.
export function fetchLocalModelMedia(since?: number): Promise<LocalModelMedia> {
  return localModelApi.localModel.media(since);
}

// История медиа-генераций (ADR-030): страница 7-дневного журнала. Пустой q —
// вся история за 7 дней. Ошибки — как у request: status 403/404/410 на поле
// ошибки, body 410 — { reason }.
export function fetchMediaHistory(q?: MediaHistoryQuery): Promise<MediaHistoryPage> {
  return localModelApi.localModel.history(q);
}

export type { MediaHistoryRecord, MediaHistoryPage, MediaHistoryQuery };

// «Облачные модели: скорость ответа» — GET /api/spend/answer-speed (админский).
// Отдельная функция, а не метод localModelApi: эндпоинт живёт в разделе расхода,
// и подмешивать его в api.localModel значило бы перекрыть api.spend из features/spend.
// live: true — это живой снимок за сутки; из офлайн-кэша его подставлять нельзя:
// подсказки чисел опираются на свежий журнал расхода.
export function fetchCloudAnswerSpeed(from: string, to: string): Promise<CloudAnswerSpeedResponse> {
  const qs = new URLSearchParams({ from, to });
  return request<CloudAnswerSpeedResponse>(`/spend/answer-speed?${qs.toString()}`, { live: true });
}
