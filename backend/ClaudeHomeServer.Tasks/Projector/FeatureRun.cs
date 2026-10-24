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
    // Удержание фичи (W4-HOLD-B1): копия поля эпика (TaskItem.Hold); null — работа разрешена.
    public FeatureHold? Hold { get; init; }
    // Счётчики исходов витков (вердиктов) по ревью+проверке+финалу.
    public FeatureVerdicts Verdicts { get; init; } = new();
    // Доля решённых витков: (ревью/проверка/финал с вердиктом) / (все такие задачи). 0..1.
    public double Progress { get; init; }
    // Готово: финал закрыт с accepted/accepted_with_remarks.
    public bool Ready { get; init; }
    // Разрез (role=cut): задачи с ролью cut и FeatureId == epic.Id.
    // У роли cut featureId необязателен — задача без него честно даёт «нет в трекере».
    public List<TaskItem> Cut { get; init; } = [];
    // Дизайн (role=design): задачи дизайнера фичи (W4-DES-B). Стадия необязательна:
    // без них этап «design» — skipped, на готовность не влияет.
    public List<TaskItem> Design { get; init; } = [];
    // План (role=plan): задачи с ролью plan и FeatureId == epic.Id.
    public List<TaskItem> Plan { get; init; } = [];
    // Безролевые задачи фичи (role=null): ни в одну стадию не попадают, но входят в
    // ленту событий (W4-DES-B: хронология — ВСЕ задачи с featureId).
    public List<TaskItem> Unroled { get; init; } = [];
    // Конвейер: 8 стадий в явном порядке жизненного цикла (cut → design → plan → prod →
    // review → qa → final → merge); «design» необязательна (см. FeatureProjector.BuildCore).
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
    // W4-HOLD-D2 (контракт §1, :364): id задач, прерванных паузой, — помеченные feature_held и
    // не закрытые. Фронт блока «на паузе» и ответ ручки POST hold читают это поле, а не Now:
    // на паузе Now гасится намеренно (W4-HOLD-D). Порядок — тот, в котором задачи дал вызывающий.
    public List<string> Interrupted { get; init; } = [];
    // Состояние фичи (единая точка правды; из него выводится bucket строки индекса и порядок
    // OrderRuns — см. FeatureProjector.Bucket). 11 значений, приоритет сверху вниз (первое
    // совпавшее побеждает): merged → closed → paused → stopped → waiting → draft → merging →
    // merge_pending → running → not_started → idle. Значения — константы FeatureStates (camelCase, как в JSON).
    public string State { get; init; } = FeatureStates.Idle;
    // Тревога: stopped → wait явный → merge; один элемент либо null.
    public FeatureAttention? Attention { get; init; }
    // Счётчик шагов: принятые ревью / знаменатель (= StepProgress.Accepted / Total). Семантика
    // изменена сознательно (честный прогресс): «принят» — не «сдан исполнителем (Done)», а
    // «последний виток ревью/проверки/финала — accepted/accepted_with_remarks и доработок не осталось»;
    // знаменатель — шаги плана (planSteps) ∪ заведённые шаги, а не только заведённые.
    public int StepsAccepted { get; init; }
    public int StepsTotal { get; init; }
    // Честный прогресс по шагам: корзины accepted/review/work/notCreated (сумма = Total),
    // вне знаменателя — withdrawn и unlinkedReworks. Вся арифметика на бэке.
    public FeatureStepProgress StepProgress { get; init; } = new();
    // Последняя активность: максимум по CompletedAt/ClaudeStartedAt задач фичи и времени мержа.
    public DateTime? LastActivityAt { get; init; }
    // W4-ANB (разрез §4, решение Андрея 24.10): «тихо» — фича стоит без движения ≥ 24 часов.
    // Выводится здесь (единая точка правды): не закрыта, сейчас ничего не идёт, и с последней
    // активности (LastActivityAt; её никогда не было — с создания эпика) прошло ≥ порога.
    // Граница включена: ровно 24 часа — «тихо». Строка индекса берёт это поле, не пересчитывает.
    public bool Quiet { get; init; }
    // Закрыта: выводится из State (merged или closed) — единая точка правды
    // (старое «merge Done || epic Done» — вторая точка, закрывало фичу частичным мержем).
    public bool Closed { get; init; }
    // Итог смерженной фичи: только у закрытой, иначе null.
    public FeatureSummary? Summary { get; init; }
}

// Документ фичи (волна 3 «Обзор фич», вкладка «Планы и макеты»): разрез (cut), план (plan)
// или консультация (consult) — СПИСОК из GET features/{id}, без текста. Текст (ResultMarkdown)
// в ответ не попадает: вкладка берёт список, а текст подгружает по клику (GET /api/tasks/{id}).
// role — из TaskItem.Role как есть (cut/plan/consult; step сюда не попадает). hasText — есть ли
// ResultMarkdown (после Trim); mockupPaths — пути макетов из ResultMarkdown (docs/mockups/….{html,md}).
public sealed record FeaturePlan(
    // Id задачи-документа.
    string TaskId,
    // Роль как есть: cut | plan | consult.
    string Role,
    // Название задачи.
    string Title,
    // Автор (PersonaId задачи); null — без персоны.
    string? AuthorPersonaId,
    // Момент создания задачи.
    DateTime CreatedAt,
    // Момент закрытия задачи; null — ещё не закрыта.
    DateTime? CompletedAt,
    // Есть ли текст (ResultMarkdown не пуст после Trim).
    bool HasText,
    // Пути макетов (docs/mockups/….{html,md}) из ResultMarkdown: distinct, порядок появления,
    // ≤ 20, пути с «..» отброшены.
    List<string> MockupPaths);

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
    // W4-ANB (разрез §4): отметки сторожа зависшего исполнителя (TaskExecutionService.ClassifyStall),
    // поля задачи как есть. «Зависла» — StaleAlertedAt != null (сторож уже позвал человека);
    // предупреждение — NudgedAt != null («окликнули, ждём»). Обе сбрасываются перезапуском
    // исполнителя (MarkClaudeStarted), поэтому признак живой.
    public DateTime? NudgedAt { get; init; }
    public DateTime? StaleAlertedAt { get; init; }
}

// Тревога фичи: один элемент либо null. Приоритет: stopped → wait явный → merge.
public sealed class FeatureAttention
{
    // wait | stopped | merge.
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
    // Число шагов (знаменатель StepProgress.Total).
    public int Steps { get; init; }
    // Шагов не принято: Total − Accepted (снятые withdrawn уже вне Total). Заполняется у merged и
    // closed: «закрыта вручную с непринятыми шагами» = State == closed ∧ StepsOpen > 0.
    public int StepsOpen { get; init; }
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
    // Ключ стадии: cut | design | plan | prod | review | qa | final | merge.
    public string Key { get; init; } = "";
    // Состояние: done — все задачи стадии закрыты; active — первая непройденная;
    // pending — ещё не начата; absent — ноль задач, а следующая стадия уже начата;
    // skipped — необязательная стадия «design» без задач (W4-DES-B).
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
    // Начало стадии (Б2): минимальный ClaudeStartedAt задач стадии; null — ни одна не стартовала.
    public DateTime? FirstStartedAt { get; init; }
    // Конец стадии (Б2): максимальный CompletedAt всех задач, ТОЛЬКО если все задачи закрыты
    // (Done); незакрытая стадия конца не имеет — null. По этому концу живут длительность
    // (ApproxMinutes), пересечения и интервалы (StageLastCompletedAt). Край полосы Ганта —
    // отдельное поле BarEnd (W5-DEF-хвост): две семантики разведены, поле не смешивает их.
    public DateTime? LastCompletedAt { get; init; }
    // Правый край полосы Ганта (W5-DEF): стадия в работе (есть незакрытая задача, которая не
    // встала — идёт, ждёт человека/устройства или дала результат) — now; вставшая — момент
    // остановки; не в работе — максимум CompletedAt закрытых задач. null — активности не было.
    public DateTime? BarEnd { get; init; }
    // Длительность по часам стены (Б2, минуты): ОБЪЕДИНЕНИЕ интервалов [ClaudeStartedAt,
    // CompletedAt] задач стадии — вставшая задача обрывается на ExecutorStoppedAt (валидной
    // пометке остановки), идущая открыта до now; параллельные задачи не складываются;
    // округление вверх до минуты. Пустая стадия (ни одна задача не стартовала) → 0.
    public int ApproxMinutes { get; init; }
    // Пересечение с идущим (Б2): интервалы стадии пересекаются с интервалами задач ДРУГОЙ стадии,
    // которая ещё идёт (имеет ≥1 задачу, не все Done). Стадия без интервалов — false.
    public bool OverlapsActive { get; init; }
    // Исполнители стадии (Б2): по одному на задачу стадии, в порядке, в котором задачи дал
    // вызывающий. Бэк отдаёт сырые данные (TaskRole/StepRef/Lane, статус, исполнителя) —
    // подпись роли (roleLabel) строит фронт.
    public List<FeatureStageExecutor> Executors { get; init; } = [];
}

// Исполнитель стадии (Б2): одна запись на задачу стадии. Сырые данные для подписи:
// роль/код шага/линия как есть (подпись строит фронт), статус выведен из полей задачи:
// running — идёт (ClaudeStartedAt, без завершения, не остановлена, не ждёт); waiting — ждёт
// человека (AwaitingHumanSince); stopped — исполнитель встал (ExecutorStoppedAt); done — закрыта;
// pending — не стартовала (остальное).
public sealed class FeatureStageExecutor
{
    // Исполнитель (PersonaId задачи).
    public string? PersonaId { get; init; }
    // Id задачи.
    public string TaskId { get; init; } = "";
    // Роль задачи как есть (step/rework/review/qa/final/merge/cut/plan/consult).
    public string? TaskRole { get; init; }
    // Код шага (у step/rework).
    public string? StepRef { get; init; }
    // Линия как есть (back/front/…).
    public string? Lane { get; init; }
    // Статус: running | waiting | stopped | done | pending.
    public string Status { get; init; } = "pending";
    // Чат исполнителя (Б7: LinkedSessionId, fail-closed, как остальной агрегат); пока null.
    public string? ChatId { get; init; }
    // Чат исполнителя архивирован (Б7); пока false.
    public bool ChatArchived { get; init; }
    // Модель исполнителя (Б4: короткая подпись и полное имя); пока null.
    public string? ModelLabel { get; init; }
    public string? ModelFull { get; init; }
    // W4-3B: связь «оценивает →»: task.ReviewOf, отфильтрованный по задачам этого run.
    // Висящие (задачи нет) и чужие id наружу не выходят — их число в ReviewOfOutside.
    public List<string> ReviewOf { get; init; } = [];
    public int ReviewOfOutside { get; init; }
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
    // Чат витка (Б7: LinkedSessionId задачи ревью/проверки/финала, fail-closed: наружу только
    // если в снимке сессий владельца; чужой/удалённый/неизвестный чат → null).
    public string? ReviewChatId { get; init; }
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
    // Чат шага (Б7: LinkedSessionId задачи-шага, fail-closed: наружу только если в снимке
    // сессий владельца; чужой/удалённый/неизвестный чат → null).
    public string? ChatId { get; init; }
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
    // Знаменатель линии: шаги плана ∪ заведённые шаги линии (снятые withdrawn не входят).
    public int Total { get; init; }
    // Принятые ревью шаги (не «Done»: сдан ≠ принят).
    public int Accepted { get; init; }
    // Сдан, ждёт ревью (все задачи цепочки Done, витка нет или он идёт).
    public int Review { get; init; }
    // В работе (задача идёт/не начата либо последний виток returned).
    public int Work { get; init; }
    // Шаг есть в плане, задачи нет.
    public int NotCreated { get; init; }
    // Шаги с возвратом (их ревью/проверка/финал вернул: returned/accepted_with_remarks).
    public int Returns { get; init; }
}

// Честный прогресс фичи по шагам (единица учёта — шаг, код stepRef; ключ шага без stepRef — id
// задачи). Корзины взаимоисключающие: Total = Accepted + Review + Work + NotCreated. Вне знаменателя:
// Withdrawn (шаг снят ревью с выкладки) и UnlinkedReworks (доработки без привязки к шагу —
// ни stepRef, ни reviewOf; новую цепочку не заводят).
public sealed class FeatureStepProgress
{
    // pending — задач role=plan нет, или у всех пустой planSteps; partial — списки есть только у
    // части планов; cut — у всех; done — cut, total > 0 и все шаги приняты. Снятые (withdrawn)
    // планы в маппинг не входят.
    public string PlanState { get; init; } = "pending";
    public int Total { get; init; }
    public int Accepted { get; init; }
    public int Review { get; init; }
    public int Work { get; init; }
    public int NotCreated { get; init; }
    public int Withdrawn { get; init; }
    public int UnlinkedReworks { get; init; }
}

// Состояния фичи (FeatureRun.State) — единая точка правды: 11 значений, camelCase (как в JSON).
// Приоритет расчёта (первое совпавшее побеждает): merged → closed → paused → stopped → waiting →
// draft → merging → merge_pending → running → not_started → idle (W4-HOLD-B1, разрез §1). Из state
// выводится bucket строки индекса (FeatureProjector.Bucket) и порядок OrderRuns — они не расходятся.
public static class FeatureStates
{
    // merge-задача Done И (финал принят ИЛИ эпик Done) — фича смержена. Частичный мерж
    // (merge Done, финал не принят, эпик открыт) фичу не смерживает (инцидент 21.10).
    public const string Merged = "merged";
    // Эпик Done и не merged — фича закрыта: решение человека «фича закончена»
    // (даже если работа ещё идёт; без мержа — «честный мерж»).
    public const string Closed = "closed";
    // Удержание человека — по общему предикату FeatureProjector.IsPaused (FeatureHoldReasons
    // manual/queue): «фичу не вести». Стоит после closed (закрытая фича закрыта, даже если
    // удержание не сняли) и перед stopped: пауза, прервавшая исполнителей, — решение человека,
    // а не тревога «ждёт вас» (W4-HOLD-B1).
    public const string Paused = "paused";
    // Есть остановленная задача (тревога kind=stopped).
    public const string Stopped = "stopped";
    // Явное ожидание человека (тревога kind=wait, КРОМЕ ожидания «мерж»).
    public const string Waiting = "waiting";
    // Черновик (Hold есть, FeatureProjector.IsPaused false — reason draft): фичу готовят,
    // работа не разрешена (роли подготовки пропускает FeatureProjector.HoldAllows). После
    // stopped/waiting: тревоги подготовки (упавший исполнитель, ожидание ответа) остаются видны.
    // «Очередь» (queue) — не третье значение: по IsPaused это та же пауза (W4-HOLD-G).
    public const string Draft = "draft";
    // merge-задача запущена (InProgress) — мерж идёт.
    public const string Merging = "merging";
    // Финал принят, merge-задачи нет ИЛИ она создана, но не запущена (Todo) — ждём
    // согласия/запуска мержа (тревога «мерж»).
    public const string MergePending = "merge_pending";
    // Now не пуст — идут задачи.
    public const string Running = "running";
    // Ни одна задача фичи не стартовала (нет ни одного ClaudeStartedAt).
    public const string NotStarted = "not_started";
    // Иначе: ничего не идёт, ничего не ждёт, фича не закрыта.
    public const string Idle = "idle";
}
