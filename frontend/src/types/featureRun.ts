// Модель фичи для режима «Исполнение фичи» в «Плане хода».
//
// Источник правды — БЭК: агрегат `FeatureRun` считает `FeatureProjector.Build`
// (backend/ClaudeHomeServer.Tasks/Projector/FeatureRun.cs) и приходит готовым из
// REST `GET /api/tasks/features?sessionId=…`. Фронт НЕ пересчитывает конвейер —
// только отображает. Ниже — зеркало полей бэкового агрегата (camelCase, как
// сериализует MVC); задачи внутри — подмножество `TaskItem`, которое несёт ответ.
//
// Что считать источником: бэк. `progress` — `double 0..1` (доля решённых витков),
// `verdicts.loops` — СЧЁТЧИК (`returned + acceptedWithRemarks`), не boolean:
// UI-условие «были правки» = `loops > 0`.

// Задача в агрегате фичи: подмножество `TaskItem` бэка (camelCase), которое
// использует карточка. Все поля аддитивны и nullable — UI обязан пережить
// отсутствие любого (старые задачи без разметки не несут feature-полей).
import type { StepProgress } from '../lib/featureProgress';

export interface FeatureTask {
  id: string;
  title: string;
  // Проект задачи (бэк шлёт полный TaskItem): нужен футеру карточки для openTaskInSection.
  projectId?: string | null;
  status?: 'todo' | 'inProgress' | 'done' | null;
  kind?: 'task' | 'defect' | null;
  // Роль в конвейере: feature · cut · plan · step · rework · review · qa · final · merge · consult.
  role?: string | null;
  featureId?: string | null;           // id эпика
  stepRef?: string | null;            // код шага («Ш1», «D1c»)
  lane?: string | null;               // линия («бэк», «фронт», «тест»)
  reviewOf?: string[] | null;         // id задач, которые оценивает это ревью/проверка/финал
  worktreeBranch?: string | null;
  personaId?: string | null;
  createdAt?: string | null;
  completedAt?: string | null;
  // Запуск исполнителя: отметка старта Claude-исполнителя (у step/rework/финала).
  // Уже приходит в JSON — просто не было описано.
  claudeStartedAt?: string | null;
  // Остановка исполнителя: когда встал и почему (карточка «ждёт человека»).
  executorStoppedAt?: string | null;
  executorStopReason?: string | null;
  // Связанный чат исполнителя (по нему «Сейчас»/тревога открывают чат).
  linkedSessionId?: string | null;
  awaitingHumanSince?: string | null;
  awaitingHumanReason?: string | null;
  verification?: { verdict?: string | null; notes?: string | null } | null;
}

// Исход витка: строго пять значений (бэк: `FeatureRound.Verdict`).
// `running` — виток открыт, вердикта ещё нет. «Принял с правками»
// (accepted_with_remarks) считается возвратом: работа вернулась к исполнителю.
export type FeatureRoundVerdict =
  | 'returned'
  | 'accepted_with_remarks'
  | 'accepted'
  | 'withdrawn'
  | 'running';

// Один виток ревью/проверки/финала (бэк: `FeatureRound`). Живёт в цепочке шагов
// (`FeatureStepChain.rounds`) и в стадиях qa/final (`FeatureStage.rounds`).
// Порядок витков в цепочке даёт бэк — по завершению (CompletedAt, при равенстве —
// по старту задачи); `startedAt` здесь только для показа, по нему НЕ сортировать
// (решение Александра, шов 2б: старты внутри цепочки могут идти не по возрастанию).
export interface FeatureRound {
  n: number;
  verdict: FeatureRoundVerdict;
  // Id задачи ревью/проверки/финала, которая и есть этот виток.
  reviewTaskId?: string | null;
  // Исполнитель витка (PersonaId задачи).
  personaId?: string | null;
  // Старт витка: ClaudeStartedAt задачи, иначе CreatedAt.
  startedAt?: string | null;
  // Закрытие витка: CompletedAt задачи; null — ещё идёт.
  completedAt?: string | null;
  // Причина: Verification.Notes.
  reason?: string | null;
  // Б7: чат витка (LinkedSessionId задачи ревью/проверки/финала, fail-closed); null — нет.
  reviewChatId: string | null;
}

// Счётчики исходов витков (бэк: `FeatureVerdicts`). `loops` — СЧЁТЧИК
// (`returned + acceptedWithRemarks`), не boolean: «были правки» = `loops > 0`.
export interface FeatureVerdicts {
  accepted: number;
  acceptedWithRemarks: number;
  returned: number;
  withdrawn: number;
  loops: number;
}

// Ключ стадии конвейера (бэк: `FeatureStage.Key`), в порядке жизненного цикла.
// design — необязательный этап после cut (W4-DES-B): без задач — state 'skipped'.
export type StageKey = 'cut' | 'design' | 'plan' | 'prod' | 'review' | 'qa' | 'final' | 'merge';
// Состояние стадии (бэк: `FeatureStage.State`).
// `skipped` — необязательный этап, которого фича не проходила (design без задач,
// W4-DES-B); бэк отдаёт его для этапа design (дефект a2155c1b).
export type StageState = 'done' | 'active' | 'pending' | 'absent' | 'skipped';
// Тон активной стадии (бэк: `FeatureStage.Tone`): run — идёт; wait — ждёт человека;
// stopped — исполнитель встал; loop — работа вернулась (доработка идёт).
export type StageTone = 'run' | 'wait' | 'stopped' | 'loop';

// Состояние фичи (бэк: `FeatureRun.State`, константы `FeatureStates`; 11 значений,
// camelCase как в JSON). Единая точка правды: из неё бэк выводит bucket строки
// индекса и порядок OrderRuns. Приоритет (первое совпавшее побеждает):
// merged → closed → paused → stopped → waiting → draft → merging → merge_pending →
// running → not_started → idle (W4-HOLD-B1).
export type FeatureState =
  | 'not_started'
  | 'running'
  | 'waiting'
  | 'stopped'
  | 'merge_pending'
  | 'merging'
  | 'merged'
  | 'closed'
  | 'idle'
  | 'paused'
  | 'draft';

// Исполнитель стадии (бэк: `FeatureStageExecutor`): одна запись на задачу стадии.
// Бэк отдаёт сырые данные (TaskRole/StepRef/Lane, статус, исполнителя) — подпись роли
// (roleLabel) строит фронт. Статус выведен из полей задачи: running — идёт; waiting — ждёт
// человека; stopped — исполнитель встал; done — закрыта; pending — не стартовала.
export interface FeatureStageExecutor {
  // Исполнитель (PersonaId задачи); null — нет.
  personaId: string | null;
  // Id задачи.
  taskId: string;
  // Роль задачи как есть (step/rework/review/qa/final/merge/cut/plan/consult); null — нет.
  taskRole: string | null;
  // Код шага (у step/rework); null — нет.
  stepRef: string | null;
  // Линия как есть (back/front/…); null — нет.
  lane: string | null;
  // Статус: running | waiting | stopped | done | pending.
  status: 'running' | 'waiting' | 'stopped' | 'done' | 'pending';
  // Чат исполнителя (LinkedSessionId, fail-closed); null — нет.
  chatId: string | null;
  // true — чат исполнителя в архиве.
  chatArchived: boolean;
  // Короткая подпись модели; null — нет.
  modelLabel: string | null;
  // Полное имя модели; null — нет.
  modelFull: string | null;
  // W4-3B: связь «оценивает →»: task.reviewOf, отфильтрованный по задачам этого run.
  // Висящие (задачи нет) и чужые id наружу не выходят — их число в reviewOfOutside.
  // Аддитивное: старые ответы их не несут — UI обязан пережить отсутствие.
  reviewOf?: string[] | null;
  reviewOfOutside?: number;
}

// Стадия конвейера фичи (бэк: `FeatureStage`). Арифметику считает бэк, фронт
// только показывает (шаг Ф5).
export interface FeatureStage {
  key: StageKey;
  state: StageState;
  // Тон — только у active, иначе null.
  tone?: StageTone | null;
  // Число проходов через стадию (у prod — 1 + возвраты, у остальных — число задач).
  passes: number;
  // Id задач стадии (в порядке, в котором их дал вызывающий).
  taskIds: string[];
  // ClaudeStartedAt идущей (InProgress) задачи стадии; null — не идёт.
  runningSince?: string | null;
  // Витки стадии (только у стадий qa/final); у остальных — пусто.
  rounds: FeatureRound[];
  // Шаг с наибольшим числом витков (только у стадии review); null — витков нет.
  longestStepRef?: string | null;
  // F8: исполнители стадии (имена для подписи-мета), число задач стадии (склонение
  // «N задач» в мета) и исход стадии (returned/accepted_with_remarks/accepted/withdrawn/
  // running → слово в мета). Аддитивные и nullable: старые ответы их не несут — UI обязан
  // пережить отсутствие (без подписи-мета).
  personaIds?: string[];
  taskCount?: number;
  verdict?: FeatureRoundVerdict | null;
  // Б2: начало стадии (мин. ClaudeStartedAt задач стадии); null — ни одна не стартовала.
  firstStartedAt: string | null;
  // Б2: конец стадии (макс. CompletedAt); null — стадия ещё не завершилась (не все Done).
  lastCompletedAt: string | null;
  // Б2: длительность по часам стены (минуты): объединение интервалов задач стадии
  // (параллельные не складываются); пустая стадия → 0.
  approxMinutes: number;
  // Б2: пересекается ли стадия с идущей (интервалы пересекаются с задачами ДРУГОЙ стадии,
  // которая ещё идёт); стадия без интервалов → false.
  overlapsActive: boolean;
  // Б2: исполнители стадии (по одному на задачу стадии, в порядке, в котором задачи дал
  // вызывающий). Сырые данные — подпись роли строит фронт (roleLabel).
  executors: FeatureStageExecutor[];
}

// Цепочка шага (бэк: `FeatureStepChain`): задача шага + её доработки, в порядке времени.
export interface FeatureStepChain {
  // Код шага («Ш1», «D1c»).
  stepRef: string;
  // Линия шага как есть.
  lane?: string | null;
  // Название шага (первая задача цепочки).
  title: string;
  // Исполнитель шага (PersonaId первой задачи цепочки).
  personaId?: string | null;
  // Задачи цепочки (шаг + доработки) в порядке времени.
  taskIds: string[];
  // Исход: accepted | withdrawn | returned | running | pending.
  outcome: string;
  // Шапка деталей шага (строка по-русски).
  status: string;
  // Длительность (минуты): от старта первой задачи до закрытия последней.
  durationMinutes: number;
  // Откуда родилась доработка: review | qa | final | null.
  source?: string | null;
  // Число витков с возвратом (returned/accepted_with_remarks) по этому шагу.
  loops: number;
  // Витки цепочки: ревью/проверки/финалы, связанные через ReviewOf, в порядке времени.
  rounds: FeatureRound[];
  // Б7: чат исполнителя шага (LinkedSessionId, fail-closed); null — нет.
  chatId: string | null;
}

// Возвраты одного источника (ревью/проверка/финал) (бэк: `FeatureSourceReturns`).
export interface FeatureSourceReturns {
  // Число уникальных задач с вердиктом returned/accepted_with_remarks.
  count: number;
  // Сколько из них, чья доработка ещё не done (бейдж в тревожный тон).
  open: number;
}

// Возвраты по источникам: ревью/проверка/финал (бэк: `FeatureReturnsBySource`).
export interface FeatureReturnsBySource {
  review: FeatureSourceReturns;
  qa: FeatureSourceReturns;
  final: FeatureSourceReturns;
}

// Линия фичи (back/front/прочее) (бэк: `FeatureLane`): исполнители и счётчики.
export interface FeatureLane {
  // Нормализованная линия: back | front | other.
  lane: string;
  // Исполнители (уникальные, в порядке первого появления).
  personaIds: string[];
  // Всего шагов в линии.
  total: number;
  // Принятые (Done) шаги.
  accepted: number;
  // Шаги с возвратом.
  returns: number;
}

// «Сейчас»: одна InProgress-задача фичи (бэк: `FeatureNow`).
export interface FeatureNow {
  taskId: string;
  // Стадия конвейера: cut | plan | prod | review | qa | final | merge.
  stage: string;
  // Исполнитель (PersonaId задачи).
  personaId?: string | null;
  // Код шага (у step/rework).
  stepRef?: string | null;
  title: string;
  // Старт: ClaudeStartedAt задачи; null — не запускалась.
  startedAt?: string | null;
  // Связанный чат исполнителя.
  linkedSessionId?: string | null;
}

// Тревога фичи (бэк: `FeatureAttention`): один элемент либо null.
// Приоритет: stopped → wait явный → wait «мерж».
export interface FeatureAttention {
  // wait | stopped | merge (merge — только пока merge-задачи нет, финал принят).
  kind: 'wait' | 'stopped' | 'merge';
  // Id задачи, к которой относится тревога (у «мерж» — финал).
  taskId?: string | null;
  // С какого момента: ExecutorStoppedAt / AwaitingHumanSince / CompletedAt финала.
  since?: string | null;
  // Причина: ExecutorStopReason / AwaitingHumanReason / "мерж".
  reason?: string | null;
  // Исполнитель (PersonaId задачи).
  personaId?: string | null;
  // Связанный чат исполнителя.
  linkedSessionId?: string | null;
}

// Итог смерженной фичи (бэк: `FeatureSummary`): только у закрытой, иначе null.
export interface FeatureSummary {
  // Момент мержа: CompletedAt merge-задачи; null — мержа нет (итог по закрытию эпика).
  mergedAt?: string | null;
  // Длительность (минуты): от старта первой задачи до мержа/закрытия.
  durationMinutes: number;
  // Число задач фичи (эпик + дочерние).
  tasks: number;
  // Число шагов (из StepChains).
  steps: number;
  // Число возвратов (Loops).
  loops: number;
}

// Удержание фичи (бэк: `FeatureRun.Hold`, W4-HOLD-B1): копия поля эпика. reason —
// 'draft' | 'manual' | 'queue' (FeatureHoldReasons); since — момент постановки
// удержания (FeatureHold.Since). null/undefined — удержания нет, работа разрешена.
export interface FeatureHold {
  reason: string;
  since?: string | null;
}

// Агрегат фичи — зеркало бэкового `FeatureRun`. `epic`/`steps`/`reviews`/`qa`/
// `final`/`merge` — задачи трекера, сгруппированные проектором по роли;
// `verdicts`/`progress`/`ready` вычисляет бэк, фронт только показывает.
// Старые поля (epic/steps/reviews/qa/final/merge/verdicts/progress/ready) пока
// использует `FeatureRunView` (страница фичи) — не удалять.
// Новые поля (cut/plan/stages/stepChains/now/attention/returnsBySource/lanes/
// stepsAccepted/stepsTotal/lastActivityAt/closed/summary) — аддитивные и
// nullable-терпимые: опциональны, UI обязан пережить их отсутствие (старые данные).
export interface FeatureRun {
  featureId: string;
  // Состояние фичи (бэк: `FeatureRun.State`, 11 значений, всегда отдаётся).
  // Единая точка правды: из неё выводится bucket строки индекса и порядок OrderRuns.
  state: FeatureState;
  // Удержание фичи (W4-HOLD-B1): null/undefined — удержания нет.
  hold?: FeatureHold | null;
  // p.32: nullable-терпимо (опционально), как и остальные новые поля — UI обязан
  // пережить отсутствие epic (защита в taskCounters: run.epic ? [run.epic] : []).
  epic?: FeatureTask;
  steps: FeatureTask[];
  reviews: FeatureTask[];
  qa: FeatureTask[];
  final: FeatureTask | null;
  merge: FeatureTask | null;
  // Разрез (role=cut) и план (role=plan) фичи.
  cut?: FeatureTask[];
  plan?: FeatureTask[];
  awaitingHumanSince?: string | null;
  awaitingHumanReason?: string | null;
  verdicts: FeatureVerdicts;
  progress: number;   // доля решённых витков, 0..1
  ready: boolean;     // финал закрыт с accepted/accepted_with_remarks
  // Конвейер: 8 стадий в порядке жизненного цикла (cut → design → plan → prod → review → qa →
  // final → merge); design необязательна — без задач её сегмент skipped (W4-DES-B).
  stages?: FeatureStage[];
  // Цепочки шагов: по одной на шаг (stepRef) — задача шага + её доработки, по времени.
  stepChains?: FeatureStepChain[];
  // «Сейчас»: все InProgress-задачи фичи, по убыванию ClaudeStartedAt. У закрытой — пусто.
  now?: FeatureNow[];
  // Тревога: stopped → wait явный → wait «мерж»; один элемент либо null.
  attention?: FeatureAttention | null;
  // Возвраты по источникам (ревью/проверка/финал).
  returnsBySource?: FeatureReturnsBySource;
  // Линии (back/front/прочее): исполнители и счётчики.
  lanes?: FeatureLane[];
  // Счётчик шагов: принятые (Done) / все (из StepChains).
  stepsAccepted?: number;
  stepsTotal?: number;
  // Честный прогресс по шагам (волна 3, Ф-pr): planState/total/accepted/review/work/
  // notCreated/withdrawn/unlinkedReworks. Аддитивное: старые ответы не несут.
  stepProgress?: StepProgress;
  // Последняя активность: максимум по CompletedAt/ClaudeStartedAt задач фичи и времени мержа.
  lastActivityAt?: string | null;
  // Закрыта: merge-задача Done или эпик Done.
  closed?: boolean;
  // Итог смерженной фичи: только у закрытой, иначе null.
  summary?: FeatureSummary | null;
}
