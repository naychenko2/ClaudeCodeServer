using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// Агрегат фичи (режим «Исполнение фичи», docs/research/turn-plan-feature-mode-cut-2026-10.md):
// чистая проекция задач трекера (эпик + дочерние) в конвейер фичи. НЕ второй источник правды —
// держит ссылки на TaskItem'ы трекера и вычисляет только то, что не лежит в полях: счётчики
// вердиктов, прогресс, готовность. Эпик и дочерние — те же TaskItem, что в сторе.
public sealed class FeatureRun
{
    // Id задачи-эпика фичи.
    public string FeatureId { get; init; } = "";
    // Эпик фичи (role=feature, FeatureId=null).
    public TaskItem Epic { get; init; } = null!;
    // Шаги и доработки (role=step/rework) — в порядке, в котором задачи дал вызывающий.
    public List<TaskItem> Steps { get; init; } = [];
    // Ревью (role=review) — в порядке вызывающего.
    public List<TaskItem> Reviews { get; init; } = [];
    // Проверки (role=qa) — в порядке вызывающего.
    public List<TaskItem> Qa { get; init; } = [];
    // Финал (role=final) — последний; null — финала нет.
    public TaskItem? Final { get; init; }
    // Мерж (role=merge) — последний; null — мержа нет.
    public TaskItem? Merge { get; init; }
    // «Ждёт вас»: с какого момента и почему (у эпика).
    public DateTime? AwaitingHumanSince { get; init; }
    public string? AwaitingHumanReason { get; init; }
    // Счётчики исходов витков (вердиктов) по ревью+проверке+финалу.
    public FeatureVerdicts Verdicts { get; init; } = new();
    // Доля решённых витков: (ревью/проверка/финал с вердиктом) / (все такие задачи). 0..1.
    public double Progress { get; init; }
    // Готово: финал закрыт с accepted/accepted_with_remarks.
    public bool Ready { get; init; }
    // Разрез (role=cut): задачи с ролью cut и FeatureId == epic.Id.
    // У роли cut featureId необязателен — задача без него честно даёт «нет в трекере».
    public List<TaskItem> Cut { get; init; } = [];
    // План (role=plan): задачи с ролью plan и FeatureId == epic.Id.
    public List<TaskItem> Plan { get; init; } = [];
    // Конвейер: 7 стадий в порядке жизненного цикла (cut → plan → prod → review → qa → final → merge).
    // Арифметика стадий — на бэке; фронт только показывает (шаг Ф5, Кира).
    public List<FeatureStage> Stages { get; init; } = [];
    // Цепочки шагов (Б2): по одной на шаг (stepRef) — задача шага + её доработки (role=rework,
    // тот же stepRef), по времени. Несут витки (связь ревью/проверки/финала через ReviewOf), исход,
    // длительность, источник возврата и линии. Фронт рисует из этого цепочку кругов в деталях шага.
    public List<FeatureStepChain> StepChains { get; init; } = [];
    // Возвраты по источнику (ревью/проверка/финал): число возвратов и сколько из них открыто
    // (доработка ещё не done) — по ним фронт красит бейдж в тревожный тон.
    public FeatureReturnsBySource ReturnsBySource { get; init; } = new();
    // Линии (back/front/прочее): исполнители (уникальные, по первому появлению) и счётчики.
    // Из этого фронт рисует «Бэк · Денис · 3 из 4».
    public List<FeatureLane> Lanes { get; init; } = [];
    // «Сейчас»: все InProgress-задачи фичи, по убыванию ClaudeStartedAt. У закрытой фичи — пусто.
    // Стадия пилюли фронт берёт у первого элемента; имена — уникальные через запятую.
    public List<FeatureNow> Now { get; init; } = [];
    // Тревога: stopped → wait явный → wait «мерж»; один элемент либо null.
    public FeatureAttention? Attention { get; init; }
    // Счётчик шагов: принятые (Done) / все (из StepChains).
    public int StepsAccepted { get; init; }
    public int StepsTotal { get; init; }
    // Последняя активность: максимум по CompletedAt/ClaudeStartedAt задач фичи и времени мержа.
    public DateTime? LastActivityAt { get; init; }
    // Закрыта: merge-задача Done или эпик Done.
    public bool Closed { get; init; }
    // Итог смерженной фичи: только у закрытой, иначе null.
    public FeatureSummary? Summary { get; init; }
}

// «Сейчас»: одна InProgress-задача фичи (кто и что делает, сколько идёт).
public sealed class FeatureNow
{
    // Id задачи.
    public string TaskId { get; init; } = "";
    // Стадия конвейера: cut | plan | prod | review | qa | final | merge.
    public string Stage { get; init; } = "";
    // Исполнитель (PersonaId задачи).
    public string? PersonaId { get; init; }
    // Код шага (у step/rework).
    public string? StepRef { get; init; }
    // Название задачи.
    public string Title { get; init; } = "";
    // Старт: ClaudeStartedAt задачи (запуск исполнителя); null — не запускалась.
    public DateTime? StartedAt { get; init; }
    // Связанный чат исполнителя.
    public string? LinkedSessionId { get; init; }
}

// Тревога фичи: один элемент либо null. Приоритет: stopped → wait явный → wait «мерж».
public sealed class FeatureAttention
{
    // wait | stopped.
    public string Kind { get; init; } = "";
    // Id задачи, к которой относится тревога (у «мерж» — финал).
    public string? TaskId { get; init; }
    // С какого момента: ExecutorStoppedAt (stopped) / AwaitingHumanSince (wait) / CompletedAt финала (мерж).
    public DateTime? Since { get; init; }
    // Причина: ExecutorStopReason (stopped) / AwaitingHumanReason (wait) / "мерж" (мерж).
    public string? Reason { get; init; }
    // Исполнитель (PersonaId задачи).
    public string? PersonaId { get; init; }
    // Связанный чат исполнителя.
    public string? LinkedSessionId { get; init; }
}

// Итог смерженной фичи: только у закрытой фичи, иначе null.
public sealed class FeatureSummary
{
    // Момент мержа: CompletedAt merge-задачи; null — мержа нет (итог по закрытию эпика).
    public DateTime? MergedAt { get; init; }
    // Длительность (минуты): от старта первой задачи до мержа/закрытия.
    public double DurationMinutes { get; init; }
    // Число задач фичи (эпик + дочерние).
    public int Tasks { get; init; }
    // Число шагов (из StepChains).
    public int Steps { get; init; }
    // Число возвратов (Loops).
    public int Loops { get; init; }
}

// Счётчики исходов витков (вердиктов) ревью/проверки/финала.
public sealed class FeatureVerdicts
{
    public int Accepted { get; init; }
    public int AcceptedWithRemarks { get; init; }
    public int Returned { get; init; }
    public int Withdrawn { get; init; }
    // ↺: возвраты + «с правками» (работа вернулась к исполнителю).
    public int Loops => Returned + AcceptedWithRemarks;
}

// Стадия конвейера фичи: состояние, тон, число проходов, задачи, момент запуска.
// Арифметику считает бэк (FeatureProjector.Build); фронт только показывает (шаг Ф5).
public sealed class FeatureStage
{
    // Ключ стадии: cut | plan | prod | review | qa | final | merge.
    public string Key { get; init; } = "";
    // Состояние: done — все задачи стадии закрыты; active — первая непройденная;
    // pending — ещё не начата; absent — ноль задач, а следующая стадия уже начата.
    public string State { get; init; } = "pending";
    // Тон (только у active, иначе null): run — идёт; wait — ждёт человека;
    // stopped — исполнитель встал; loop — работа вернулась (доработка идёт).
    public string? Tone { get; init; }
    // Число проходов через стадию: у prod — 1 + возвраты (returned/awr у review/qa/final),
    // у остальных — число задач. Потолок 5 ставит фронт, бэк отдаёт честное число.
    public int Passes { get; init; }
    // Id задач стадии (в порядке, в котором их дал вызывающий).
    public List<string> TaskIds { get; init; } = [];
    // ClaudeStartedAt идущей (InProgress) задачи стадии; null — не идёт.
    public DateTime? RunningSince { get; init; }
    // Витки стадии (Б2; только у стадий qa/final): каждая задача стадии — виток,
    // формат тот же, что у цепочки шагов. У остальных стадий — пусто.
    public List<FeatureRound> Rounds { get; init; } = [];
    // Шаг с наибольшим числом витков (Б2; только у стадии review) — для строки «дольше всех D1c».
    // null — витков нет ни у одного шага.
    public string? LongestStepRef { get; init; }
    // Исполнители стадии (Б5): уникальные PersonaId задач стадии в порядке первого появления;
    // пустые id не включаются. У cut/plan/merge — PersonaId их задач; у prod — исполнители шагов
    // и доработок; у review/qa/final — ревьюеры/проверяющие. По ним фронт рисует подпись
    // «Егор · 1 задача» (текст подписи — Кира, бэк отдаёт сырые данные).
    public List<string> PersonaIds { get; init; } = [];
    // Число задач стадии (Б5): для подписи «1 задача», «3 задачи».
    public int TaskCount { get; init; }
    // Исход последней по времени закрытой задачи стадии (Б5): returned | accepted_with_remarks |
    // accepted | withdrawn; «running», если задача стадии идёт; null — стадия без задач или без
    // вердикта (у стадий без вердиктов cut/plan/merge/prod — null, кроме случая, когда у задачи
    // есть Verification.Verdict — тогда он же). Значения строго из пяти, как у rounds[].verdict.
    public string? Verdict { get; init; }
}

// Виток: один круг ревью/проверки/финала. Живёт в цепочке шагов (FeatureStepChain.Rounds)
// и в стадиях qa/final (FeatureStage.Rounds). `verdict` строго из пяти значений:
// returned | accepted_with_remarks | accepted | withdrawn | running (running — виток открыт,
// вердикта ещё нет). «Принял с правками» (accepted_with_remarks) считается возвратом: работа
// вернулась к исполнителю.
public sealed class FeatureRound
{
    // Номер витка (1-based) в цепочке/стадии.
    public int N { get; init; }
    // Исход витка: returned | accepted_with_remarks | accepted | withdrawn | running.
    public string Verdict { get; init; } = "running";
    // Id задачи ревью/проверки/финала, которая и есть этот виток.
    public string? ReviewTaskId { get; init; }
    // Исполнитель витка (PersonaId задачи ревью/проверки/финала).
    public string? PersonaId { get; init; }
    // Старт витка: ClaudeStartedAt задачи, иначе CreatedAt.
    public DateTime? StartedAt { get; init; }
    // Закрытие витка: CompletedAt задачи; null — ещё идёт.
    public DateTime? CompletedAt { get; init; }
    // Причина: Verification.Notes.
    public string? Reason { get; init; }
}

// Цепочка шага: задача шага (role=step) + её доработки (role=rework, тот же stepRef),
// в порядке времени. Шаг = stepRef: по нему доработка склеивается со своим шагом.
public sealed class FeatureStepChain
{
    // Код шага («Ш1», «D1c»).
    public string StepRef { get; init; } = "";
    // Линия шага как есть (Lane первой задачи цепочки).
    public string? Lane { get; init; }
    // Название шага (первая задача цепочки).
    public string Title { get; init; } = "";
    // Исполнитель шага (PersonaId первой задачи цепочки).
    public string? PersonaId { get; init; }
    // Задачи цепочки (шаг + доработки) в порядке времени.
    public List<string> TaskIds { get; init; } = [];
    // Исход: accepted | withdrawn | returned | running | pending.
    public string Outcome { get; init; } = "pending";
    // Шапка деталей шага (строка по-русски): принят | идёт | ждёт ревью | снят с выкладки | закрыт без повторного ревью.
    public string Status { get; init; } = "";
    // Длительность (минуты): от старта первой задачи цепочки (ClaudeStartedAt, иначе CreatedAt)
    // до закрытия последней; у идущей — до сейчас.
    public double DurationMinutes { get; init; }
    // Откуда родилась доработка: review | qa | final | null (связи ReviewOf нет — метки нет).
    public string? Source { get; init; }
    // Число витков с возвратом (returned/accepted_with_remarks) по этому шагу.
    public int Loops { get; init; }
    // Витки цепочки: ревью/проверки/финалы, связанные с ней через ReviewOf, в порядке времени.
    // Одно ревью на два шага попадает в обе цепочки (связка многие-ко-многие).
    public List<FeatureRound> Rounds { get; init; } = [];
}

// Возвраты одного источника (ревью/проверка/финал): число возвратов и сколько из них открыто.
public sealed class FeatureSourceReturns
{
    // Возвраты этого источника: число уникальных задач ревью/проверки/финала с вердиктом
    // returned/accepted_with_remarks.
    public int Count { get; init; }
    // Сколько из них, чья доработка ещё не done (фронт красит бейдж в тревожный тон).
    public int Open { get; init; }
}

// Возвраты по источникам: ревью/проверка/финал.
public sealed class FeatureReturnsBySource
{
    public FeatureSourceReturns Review { get; init; } = new();
    public FeatureSourceReturns Qa { get; init; } = new();
    public FeatureSourceReturns Final { get; init; } = new();
}

// Линия фичи (back/front/прочее): исполнители и счётчики.
public sealed class FeatureLane
{
    // Нормализованная линия: back | front | other (нормализация как laneOf во фронтовом PipelineFlow.tsx).
    public string Lane { get; init; } = "other";
    // Исполнители (уникальные, в порядке первого появления).
    public List<string> PersonaIds { get; init; } = [];
    // Всего шагов в линии.
    public int Total { get; init; }
    // Принятые (Done) шаги.
    public int Accepted { get; init; }
    // Шаги с возвратом (их ревью/проверка/финал вернул: returned/accepted_with_remarks).
    public int Returns { get; init; }
}
