// Статистика фичи (волна 3 «Обзор фич», Б-проекции) — ТОЛЬКО в ответе
// `GET /api/tasks/features/{id}` (поле `stats`). Источник правды — БЭК:
// проектор считает таймлайн, разбивку, «дольше всех» и расход; фронт только
// показывает. `source: 'approx' | 'journal'` — откуда взяты числа (приближённо
// по полям задач vs. по журналу ходов); `truncated` — лента/расход обрезаны.
//
// Все поля nullable-терпимы там, где бэк может не иметь данных (null — нет
// значения); UI обязан пережить отсутствие любого (старые ответы не несут stats).

import type { StageKey } from './featureRun';

// Источник чисел: 'approx' — приближённо по полям задач; 'journal' — по журналу ходов.
export type FeatureStatsSource = 'approx' | 'journal';

// Ход внутри интервала стадии (бэк: `StageTurn`): закрытый ход
// задачи. Живой хвост идущей задачи — НЕ ход, его тут нет.
export interface StageTurn {
  // Id задачи, чей ход.
  taskId: string;
  // Начало/конец хода (ISO-строки).
  from: string;
  to: string;
  // Исход хода (finished/cancelled/interrupted/error…); null — не зафиксирован.
  outcome: string | null;
  // Модель хода; null — не известна.
  model: string | null;
}

// Контракт живого хода (GC-F10b): `turns` — только закрытые ходы, живой хвост — не ход.
// Фронт не ждёт живого хода от бэка и не расширяет StageTurn: синтезирует его сам — для
// интервала с live: true неоконченной фичи и задачи из run.now, состоящей в taskIds этого
// интервала (бэк это гарантирует: id живой хвоста попадает в taskIds вместе с хвостом,
// FeatureStageIntervals.cs:79-92,107), ход [max(старт задачи, конец её последнего закрытого
// хода) – «сейчас» рендера]. Признак синтезированного хода для UI — его 'to' ровно равен
// «сейчас» рендера: он печатается «…–идёт» и рисуется на полосе как живой (kind 'live').
// Поля StageTurn при этом не меняются: синтезированный ход — конструкция фронта, не данные бэка.

// Интервал работы стадии (бэк: `StageInterval`): склейка
// смежных ходов одной стадии. From/To — границы, Minutes — минуты стены между ними.
export interface StageInterval {
  // Начало/конец интервала (ISO-строки).
  from: string;
  to: string;
  // Минуты (бэк: FeatureTimeMath.Minutes(From, To)).
  minutes: number;
  // true — интервал дотянут до «сейчас» (в нём живой хвост идущей задачи).
  live: boolean;
  // Исход ПОСЛЕДНЕГО хода интервала, если он cancelled/interrupted/error; иначе null.
  lastOutcome: string | null;
  // Id задач, чьи ходы вошли в интервал (уникальные, по первому появлению).
  taskIds: string[];
  // Ходы интервала в хронологическом порядке.
  turns: StageTurn[];
}

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
  // Интервалы работы по стадиям (GC-B2, «Хронология»/Гант): стадия → её интервалы из
  // журнала ходов (бэк: `FeatureTimeline.IntervalsByStage`). null — ТОЛЬКО режим approx
  // (журнала нет / он не полный). Полный журнал БЕЗ ходов — пустой объект (не null):
  // «интервалов нет» и «интервалы не считались» — разные состояния, UI различает их
  // по null. Аддитивное: старые ответы поля не несут — UI обязан пережить отсутствие.
  intervalsByStage?: Partial<Record<StageKey, StageInterval[]>> | null;
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
    // Самый долгий эпизод ожидания (ST-B2): минуты + задача + начало (ISO, всегда заполнено);
    // null — эпизодов нет или источник approx.
    longestWait?: { minutes: number; taskId: string | null; from: string } | null;
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

// Квантили одной метрики: медиана и p25–p75 (линейная интерполяция, тип 7).
export interface TypicalPercentiles {
  median: number;
  p25: number;
  p75: number;
}

// «Типичная фича проекта» (ST-B1): медиана и p25–p75 по СМЕРЖЕННЫМ фичам того же проекта и
// владельца; текущая фича в выборку не входит. null — смерженных меньше трёх (шкалы сравнения
// нет). Ожидание — null, если меньше трёх смерженных фич имеют полный журнал.
export interface TypicalFeatureStats {
  // Число смерженных фич в выборке (текущая не считается).
  count: number;
  // Из них — фич с полным журналом: размер выборки ожидания.
  waitingCount: number;
  // Длительность (минуты).
  duration: TypicalPercentiles;
  // Ожидание человека (минуты); null — не достаточно фич с журналом.
  waiting: TypicalPercentiles | null;
  // Число возвратов.
  loops: TypicalPercentiles;
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
  // «Типичная фича проекта»; null — смерженных меньше трёх. Опциональна: старые ответы
  // поля не несут.
  typical?: TypicalFeatureStats | null;
}
