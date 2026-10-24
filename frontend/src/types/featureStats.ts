// Статистика фичи (волна 3 «Обзор фич», Б-проекции) — ТОЛЬКО в ответе
// `GET /api/tasks/features/{id}` (поле `stats`). Источник правды — БЭК:
// проектор считает таймлайн, разбивку, «дольше всех» и расход; фронт только
// показывает. `source: 'approx' | 'journal'` — откуда взяты числа (приближённо
// по полям задач vs. по журналу ходов); `truncated` — лента/расход обрезаны.
//
// Все поля nullable-терпимы там, где бэк может не иметь данных (null — нет
// значения); UI обязан пережить отсутствие любого (старые ответы не несут stats).

// Источник чисел: 'approx' — приближённо по полям задач; 'journal' — по журналу ходов.
export type FeatureStatsSource = 'approx' | 'journal';

// Таймлайн фичи: интервалы стадий, сегменты (работа/ожидание/простой/ошибка)
// по двум трекам (main/review), текущее состояние и итоги по минутам.
export interface FeatureTimeline {
  // Начало таймлайна (ISO-строка).
  from: string;
  // Конец таймлайна (ISO-строка).
  to: string;
  // Границы полосы стадии: ключ + старт/край (barEnd — правый край полосы, W5-DEF;
  // null — активности не было).
  stages: { key: string; startedAt: string | null; barEnd: string | null }[];
  // Сегменты: непрерывные куски времени с видом, треком, стадией и задачей.
  segments: {
    from: string;
    to: string;
    // work — работа; waiting — ожидание; idle — простой; error — ошибка; notWorking — не работало.
    kind: 'work' | 'waiting' | 'idle' | 'error' | 'notWorking';
    // Трек: main — основная линия; review — ревью/проверка/финал.
    track: 'main' | 'review';
    // Стадия, к которой относится сегмент.
    stage: string;
    // Задача, к которой относится сегмент; null — не привязано к задаче.
    taskId: string | null;
  }[];
  // Текущее состояние (если фича не закрыта): kind + с какого момента + задача; null — закрыта.
  current: { kind: 'waiting' | 'stopped' | 'merge_pending'; since: string; taskId: string | null } | null;
  // Итоги по минутам.
  totals: {
    // Простой (стены) всего: от старта до конца.
    elapsedMinutes: number;
    // Стена работы (объединение интервалов, параллельные не складываются).
    workWallMinutes: number;
    // Сумма минут по агентам (складывается, параллельные учитываются).
    agentsSumMinutes: number;
    // Минут «не работало» (waiting + idle + error).
    notWorkingMinutes: number;
    // Минут ожидания; null — не считалось.
    waitingMinutes: number | null;
    // Минут простоя; null — не считалось.
    idleMinutes: number | null;
    // Число эпизодов ожидания; null — не считалось.
    waitingCount: number | null;
    // Самое длинное ожидание (минуты); null — не было.
    longestWaitMinutes: number | null;
    // Число ошибок; null — не считалось.
    errorCount: number | null;
    // Минут в ошибках; null — не считалось.
    errorMinutes: number | null;
  };
}

// Группа разбивки: ключ (персона/модель/роль) + минуты + токены + стоимость + чаты.
export interface FeatureBreakdownGroup {
  // Ключ группы: personaId / model / role.
  key: string;
  // Минуты (стена) по группе.
  minutes: number;
  // Токены; null — не считалось.
  tokens: number | null;
  // Стоимость (USD); null — не считалась.
  costUsd: number | null;
  // Чаты, к которым относится группа.
  chatIds: string[];
}

// Разбивка расхода по трём измерениям: персоны, модели, роли.
export interface FeatureBreakdown {
  personas: FeatureBreakdownGroup[];
  models: FeatureBreakdownGroup[];
  roles: FeatureBreakdownGroup[];
}

// «Дольше всех сейчас»: задача, которая идёт дольше всех в данный момент.
export interface FeatureLongestNow {
  // Id задачи.
  taskId: string;
  // Чат исполнителя; null — нет.
  chatId: string | null;
  // Исполнитель (PersonaId); null — нет.
  personaId: string | null;
  // Код шага (у step/rework); null — нет.
  stepRef: string | null;
  // Сколько идёт (минуты).
  minutes: number;
  // Токены; null — не считалось.
  tokens: number | null;
  // Доля в общем расходе (0..1); null — не считалась.
  share: number | null;
}

// Расход фичи: токены, стоимость, источник (journal/approx) и общая (shared) часть.
export interface FeatureSpend {
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  // Стоимость (USD).
  costUsd: number;
  // true — стоимость частичная (не все модели дали цену).
  costPartial: boolean;
  // Модели, у которых цена скрыта (не отдаётся).
  hiddenCostModels: string[];
  // Момент расчёта (ISO-строка).
  from: string;
  // Общая (shared) часть расхода: ходы, общие для нескольких фич; null — нет.
  shared: {
    inputTokens: number;
    outputTokens: number;
    cacheReadTokens: number;
    cacheCreationTokens: number;
    costUsd: number;
    // Число общих ходов.
    turns: number;
    // Метод расчёта: journal — по журналу; approx — приближённо.
    method: 'journal' | 'approx';
  } | null;
}

// Статистика фичи (только в GET features/{id}).
export interface FeatureStats {
  // Источник чисел: approx — приближённо; journal — по журналу ходов.
  source: FeatureStatsSource;
  // true — данные обрезаны (лимит ленты/расхода).
  truncated: boolean;
  // Таймлайн.
  timeline: FeatureTimeline;
  // Разбивка по персонам/моделям/ролям.
  breakdown: FeatureBreakdown;
  // «Дольше всех сейчас»; null — никто не идёт.
  longestNow: FeatureLongestNow | null;
  // Расход; null — не считался.
  spend: FeatureSpend | null;
}
