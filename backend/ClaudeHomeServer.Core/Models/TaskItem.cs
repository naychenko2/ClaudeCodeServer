using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Models;

// Задача пользователя; может быть привязана к проекту или быть личной (ProjectId == null).
// Хранение — data/tasks.json (TaskManager).
public class TaskItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string? ProjectId { get; set; }
    public string? OwnerId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";   // markdown
    public TaskItemStatus Status { get; set; } = TaskItemStatus.Todo;
    // Колонка на доске проекта (BoardColumn.Id); null = дефолтная колонка своей категории.
    // Status остаётся категорией — за ним вся семантика; ColumnId лишь уточняет размещение.
    public string? ColumnId { get; set; }
    public TaskItemPriority Priority { get; set; } = TaskItemPriority.Medium;
    public string? DueDate { get; set; }   // ISO: YYYY-MM-DD
    public string? DueTime { get; set; }   // HH:MM
    // Напоминание: офсет до срока в минутах (0 = в момент срока); null — без напоминания
    public int? ReminderMinutes { get; set; }
    // UTC-отметка отправленного напоминания (идемпотентность планировщика);
    // сбрасывается при изменении срока или офсета
    public DateTime? ReminderSentAt { get; set; }
    public TaskItemAssignee? Assignee { get; set; }
    // Правило повторения; при завершении экземпляра создаётся следующий (см. TaskManager)
    public TaskRecurrence? Recurrence { get; set; }
    // Общий id серии повторяющейся задачи (= id первого экземпляра)
    public string? SeriesId { get; set; }
    public string? LinkedSessionId { get; set; }
    // Исполнение от лица персоны: сессия-исполнитель ведётся с её характером,
    // моделью и памятью (null — обычный Claude)
    public string? PersonaId { get; set; }
    // Время жизни чата исполнения (мин от последней активности); null — бессрочно.
    // Применяется один раз при создании сессии в TaskExecutionService.ExecuteAsync;
    // имеет смысл только при Assignee=Claude. Дефолт 1440 (сутки) на новых задачах.
    public int? ExecutionExpiresAfterMinutes { get; set; } = 1440;
    // Уровень модели исполнителя: под что именно эта работа — проектирование (Strong),
    // реализация по плану (Medium), механическая правка (Weak). null — не задан: модель
    // возьмётся от персоны-исполнителя и назначения места (см. TaskExecutionService.ResolveExecutorModel).
    public ModelTier? ModelTier { get; set; }
    // Отдельное git worktree для чата-исполнителя: путь СУЩЕСТВУЮЩЕГО дерева (хостовый) и
    // его ветка. Дерево здесь не создаётся — поле означает «работай в этом дереве» (штаб
    // «Командной реализации» раздаёт исполнителям своё). null — исполнитель стартует в корне
    // проекта; у личной задачи (ProjectId == null) поля не хранятся — worktree без репы
    // проекта не бывает (TaskManager.NormalizeWorktree).
    public string? WorktreePath { get; set; }
    public string? WorktreeBranch { get; set; }
    // Claude-исполнитель: отметка запуска (идемпотентность автозапуска, переживает рестарт)
    public DateTime? ClaudeStartedAt { get; set; }
    // Итог последнего запуска: success | error; null — ещё выполняется или не запускалась
    public string? ClaudeResult { get; set; }
    // Исполнитель встал насовсем (не «ход упал», а «дальше работать нечем»): отметка времени
    // и причина на проводе (auth_failed — не удалось авторизоваться у провайдера модели).
    // Отдельного статуса нет намеренно — задача остаётся InProgress, признак лишь снимает
    // с «в работе» немой характер (уведомление владельцу + пометка в карточке).
    // Сбрасывается при повторном запуске исполнителя (TaskManager.MarkClaudeStarted).
    public DateTime? ExecutorStoppedAt { get; set; }
    public string? ExecutorStopReason { get; set; }
    // W4-SEAM2 (хвост R-SEAM2 п.1): признак «продолжение этой пометки уже отправлено». Ставится
    // перед доставкой продолжения, гасится вместе с пометкой (ClearExecutorStopped) или новой
    // пометкой (MarkExecutorStopped); при бое доставки — снимается, чтобы повторное снятие
    // паузы попробовал снова. Без него сбой между доставкой и снятием пометки заставлял
    // повторное снятие слать продолжение заново. Аддитивная nullable-поле: BackupSchema.Version
    // не растёт (прецедент ExecutorLaunchedBySessionId/ReportToSessionId).
    public DateTime? HoldResumeDeliveredAt { get; set; }
    // Исполнитель ждёт устройство локального проекта (ADR-016, вариант А плана §5): момент
    // постановки в ожидание и причина для человека. ClaudeStartedAt при этом пуст — запуска
    // не было. Стартует DeviceOnlineDispatcher при выходе устройства в онлайн; через 24 ч —
    // остановка с причиной device_wait_expired. Снимается запуском (MarkClaudeStarted).
    public DateTime? DeviceWaitSince { get; set; }
    public string? DeviceWaitReason { get; set; }
    // Пометка снятия человеком по карточке блокера (волна 1 team-blocker-honest, дефект
    // f3965801): «Задача будет закрыта как снятая, исполнитель получит отбой» — диалог
    // ОБЕЩАЕТ это. Поле делает пометку сильнее позднего tasks_complete: гонка
    // неустранима (исполнитель мог писать отчёт в момент снятия), но после того как
    // человек нажал «снять», задача НИКОГДА не должна выглядеть штатно выполненной.
    // TaskManager.Update проверяет поле и не меняет Status/Outcome/ResultMarkdown;
    // доклад исполнителя при желании дописывается отдельной строкой. Setter: ставит
    // DropSubtaskAsync после Update (init был бы слишком узким — Update сам ставит
    // Status=Done, и пометка идёт поверх).
    public DateTime? DroppedByHumanAt { get; set; }
    // Страховка «ход кончился, а задачу никто не закрыл» (TaskExecutionService.CheckStalledExecutorAsync):
    // отметки о напоминании исполнителю и об уведомлении человека. Ровно по одному на запуск —
    // обе сбрасываются перезапуском исполнителя (MarkClaudeStarted), а не каждым ходом:
    // иначе многошаговая задача получала бы напоминание после каждого своего хода.
    public DateTime? ExecutorNudgedAt { get; set; }
    public DateTime? ExecutorStaleAlertedAt { get; set; }
    // Идемпотентность join двух независимых сигналов завершения (CT-8): R — конец хода
    // (ClaudeResult) и D — Status=Done (tasks_complete/PUT). CAS TaskManager.TryMarkCompletionDelivered
    // гарантирует ровно одну доставку L0/Z-доклада вне зависимости от порядка их прихода.
    // НЕ переносится в SpawnNextOccurrence (как ClaudeResult — серия начинается заново).
    // Сбрасывается при штатном переоткрытии (уход из Done через TaskManager.Update,
    // расследование 88444536): переоткрытая для доработки карточка обязана исполняться снова.
    public bool CompletionDelivered { get; set; }
    // Markdown-описание итога выполнения (прикрепляет исполнитель через tasks_complete/
    // tasks_update). null — результата нет; "" — очищен. Не переносится в следующий
    // экземпляр регулярной задачи (как ClaudeResult/LinkedSessionId — серия начинается заново).
    public string? ResultMarkdown { get; set; }
    public List<string> LinkedFiles { get; set; } = [];
    public List<TaskSubtask> Subtasks { get; set; } = [];
    public List<string> Labels { get; set; } = [];
    // Связь с чекбоксом заметки (флаг notes-task-sync): задача создана из строки-чекбокса
    // заметки. Завершение задачи ставит галочку в заметке и наоборот. Line — 0-based
    // индекс строки в контенте заметки на момент промоута (best-effort при правках).
    public string? SourceNoteId { get; set; }
    public int? SourceNoteLine { get; set; }
    // Происхождение задачи: персона-постановщик (создала задачу через tasks_create из своего
    // чата) и чат, породивший задачу. Оба null у задач из UI/API без персоны (старые задачи —
    // тоже null, обратная совместимость). SourceSessionId — общее поле двух фич: уведомление
    // постановщика о завершении (L0) и иерархия чатов (Session.ParentSessionId вычисляется
    // из него — второй источник истины не заводим).
    public string? CreatedByPersonaId { get; set; }
    public string? SourceSessionId { get; set; }
    // Кто последним запустил исполнителя (tasks_run_executor из чата): доклад о
    // завершении идёт в эту сессию, а не только в SourceSessionId (создателя карточки). Повторный
    // запуск перезаписывает. Оба null — запуск человеком без чата (кнопка в UI не шлёт X-Caller-Session-Id и поля сбрасывает) или старые задачи: адресат
    // прежний. Аддитивные nullable-поля: tasks.json читается штатно, BackupSchema.Version не растёт.
    public string? ExecutorLaunchedBySessionId { get; set; }
    // Нужно только для аудита: персона авто-хода доклада берётся из сессии-адресата, это поле не читается.
    public string? ExecutorLaunchedByPersonaId { get; set; }
    // Адресат доклада, унаследованный от родительской задачи: если задачу создала сессия — чат-исполнитель
    // другой задачи T (планировщик, QA), доклад идёт туда, куда он сам докладывает по T (оркестратору),
    // а не этой молчаливой сессии. Ставит только tasks_create при создании; аддитивное nullable-поле,
    // BackupSchema.Version не растёт.
    public string? ReportToSessionId { get; set; }
    // ─── Фича (режим «Исполнение фичи», docs/research/turn-plan-feature-mode-cut-2026-10.md) ───
    // Аддитивные nullable-поля: размеченные по-новому задачи получают их, старые — null.
    // BackupSchema.Version не растёт (прецедент ExecutorLaunchedBySessionId/ReportToSessionId).
    // Id задачи-эпика фичи; у эпика самого null.
    public string? FeatureId { get; set; }
    // Роль задачи в фиче: feature · cut · plan · step · rework · review · qa · final · merge · consult.
    public string? Role { get; set; }
    // Код шага из плана («B1», «F2»); у step/rework, по нему доработка склеивается со своим шагом.
    public string? StepRef { get; set; }
    // Линия («бэк», «фронт»); подпись берётся как есть.
    public string? Lane { get; set; }
    // Id задач, которые оценивает это ревью/проверка/финал. Пусто/у final,qa — вся фича.
    public List<string>? ReviewOf { get; set; }
    // Структурный список шагов плана (честный прогресс фичи, разрез Александра §2): знаменатель
    // «сколько всего» берётся из PlanSteps задач role=plan, а не из markdown-плана. Аддитивное
    // nullable-поле: допустимо ТОЛЬКО у role=plan (валидация — TaskManager), у прочих ролей и в
    // старых задачах — null. BackupSchema.Version не растёт (прецедент ReviewOf).
    public List<PlannedStep>? PlanSteps { get; set; }
    // «Ждёт вас»: с какого момента и почему (merge, decision); у эпика.
    public DateTime? AwaitingHumanSince { get; set; }
    public string? AwaitingHumanReason { get; set; }
    // Удержание фичи (волна 4, разрез §1): null — работа разрешена; иначе — причина (FeatureHoldReasons).
    // Ставится только на эпике (role=feature) и только человеком. Аддитивное nullable-поле: старый
    // tasks.json без «hold» читается как null, BackupSchema.Version не растёт.
    public FeatureHold? Hold { get; set; }
    // «Голос фичи» (волна 6, B3): короткий текст оркестратора/человека о состоянии фичи. Только у эпика
    // (role=feature), пишет TaskManager.SetFeatureNote. Аддитивное nullable-поле: старый tasks.json
    // без «note» читается как null, BackupSchema.Version не растёт.
    public FeatureNote? Note { get; set; }
    // Срок из первой оценки оркестратора после старта фичи (FeatureEstimate.EtaMinutes): пишется один раз
    // SetFeatureNote и не перезаписывается, снятие голоса его не трогает. Аддитивное nullable-поле.
    public int? InitialEtaMinutes { get; set; }
    // Глубина цепочки делегирования: 0 — создана не из чата-исполнителя (обычный чат/UI/API);
    // из чата-исполнителя родительской задачи — её DelegationDepth + 1 (TaskManager.Create).
    // Анти-рекурсия делегирования проверяется гейтом по глубине хода (DelegatedTurnGate /
    // DenyOnDelegatedTurn, agentDepth), а не из этой модели; поле фиксирует длину цепочки.
    public int DelegationDepth { get; set; }
    // Порядок карточки на Kanban-доске (ручная сортировка внутри колонки).
    // double — чтобы вставлять между соседями через midpoint без перенумерации.
    // 0 = не назначен (миграция/сортировка по дефолту); задаётся в Create и при drag.
    public double Order { get; set; }
    // Вид карточки: обычная задача или дефект. Дефект обязан пройти правила DefectRules
    // (см. Services/DefectRules.cs): нельзя создать сразу в Done, требуются шаги Repro
    // на review-колонке и вердикт Verification (или ClosedWithoutCheck) при закрытии.
    public TaskKind Kind { get; set; } = TaskKind.Task;
    // Шаги воспроизведения дефекта. Steps обязателен только при попадании в review-колонку
    // (прочерчено в DefectRules.EnsureReproOnReview). Expected/Actual — свободные поля
    // постановщика для контекста.
    public DefectRepro? Repro { get; set; }
    // Вердикт проверки дефекта. VerifiedAt ставит сервер, PersonaId — из X-Caller-Session-Id
    // (заголовок подделываем — это гигиена атрибуции, не защита). What/Result присылает клиент.
    // Сбрасывается при уходе из Done вместе с Outcome.
    public TaskVerification? Verification { get; set; }
    // Итоговый исход дефекта. ClosedWithoutCheck — для внутренних путей закрытия (галочка
    // заметки, снятие волной), которые не оставляют системного Verification. Прочие значения
    // ставит человек/персона. Сбрасывается при уходе из Done.
    public DefectOutcome? Outcome { get; set; }
    // Критерии приёмки (измеримые) и свидетельства их выполнения. Аддитивные поля: старые
    // задачи читаются пустыми списками, BackupSchema.Version не растёт. Закрыть задачу с
    // непустым Acceptance можно, только когда на каждый критерий есть свидетельство
    // (гейт один — AcceptanceRules, вызывается из TaskManager на переходе в Done).
    // Явный "acceptance": null в JSON затирал бы инициализатор — сеттер возвращает пустой список
    public List<AcceptanceCriterion> Acceptance { get => _acceptance; set => _acceptance = value ?? []; }
    public List<AcceptanceEvidence> Evidence { get => _evidence; set => _evidence = value ?? []; }
    private List<AcceptanceCriterion> _acceptance = [];
    // Последний результат предгейта шага (NC-P1b): кладёт только TaskManager.RecordPregate по вердикту
    // ITaskPregate. Аддитивное поле: старые задачи читаются с null. Закрытие шага не блокирует — PregateRules
    public PregateRecord? Pregate { get; set; }
    private List<AcceptanceEvidence> _evidence = [];
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    // Дата+время завершения: когда статус стал Done. null — не завершена.
    // Сбрасывается при переоткрытии (статус ≠ Done). Не переносится в следующий
    // экземпляр регулярной задачи (как ClaudeResult/ResultMarkdown — серия начинается заново).
    public DateTime? CompletedAt { get; set; }
}

// Удержание фичи (TaskItem.Hold, волна 4 §1): Reason — словарь FeatureHoldReasons (draft | manual |
// queue), Since — момент постановки, BySessionId — чат постановки (null — из UI без чата), ByUser —
// ставил человек (оркестратор только предлагает). Позиционная запись — как AcceptanceCriterion:
// round-trip через JSON без конвертера.
public record FeatureHold(string Reason, DateTime Since, string? BySessionId, bool ByUser);

// Голос фичи (TaskItem.Note): Text — не длиннее FeatureNote.MaxTextLength, At/StageAtWrite/StateAtWrite
// штампует сервер в момент записи (по ним считается «устарело»), BySessionId — чат автора (null — из UI
// без чата; наружу не отдаётся: поле живёт в сторе, с wire его снимает конвертер типа на границе
// сериализации — Main/Services/FeatureNoteJsonConverter.cs), ByUser — писал человек. Позиционная
// запись — как FeatureHold.
// StageAtWrite — стадия конвейера, StateAtWrite — КАТЕГОРИЯ фичи (brief.kind: running/waiting_you/
// error/paused/queued/draft/idle/merged/closed) на момент записи (B15, дефект 415c1c80; merged —
// девятая категория, «Фильтр фич по состоянию», R-Б1/2); имя поля и
// JSON-контракт прежние — голоса, записанные до B15, хранят СОСТОЯНИЕ («running», «paused»…), они
// достраиваются до категории таблицей FeatureBrief.KindByState (fail-closed для неизвестного).
// Estimate — оценка оркестратора (макет 2 сводки фичи): пишется только вместе с голосом, штамп
// At/StageAtWrite/StateAtWrite общий; записи без оценки читаются с Estimate = null.
public record FeatureNote(string Text, DateTime At, string? BySessionId, bool ByUser,
    string StageAtWrite, string StateAtWrite, FeatureEstimate? Estimate = null)
{
    public const int MaxTextLength = 600;
}

// Оценка оркестратора (FeatureNote.Estimate): Done/InProgress/Total — единицы работы (шаги плана плюс
// стадии конвейера), Percent — когда шаги неравные по объёму (null — доля done/total), Remaining —
// ещё не начатое (первый пункт — «дальше»), EtaMinutes — срок до готовности (null — не берётся
// оценить), Why — почему срок заметно отличается от обычного. Ограничения — Validate.
public record FeatureEstimate(int Done, int Total, int InProgress, int? Percent,
    List<string>? Remaining, int? EtaMinutes, string? Why)
{
    public const int MaxTotal = 50;
    public const int MaxRemaining = 8;
    public const int MaxRemainingLength = 40;
    public const int MaxWhyLength = 160;

    // Текст отказа или null. Remaining/Why вызывающий нормализует Normalize до проверки.
    public string? Validate()
    {
        if (Total is < 1 or > MaxTotal) return $"featureEstimate.total: от 1 до {MaxTotal}";
        if (Done < 0 || Done > Total) return "featureEstimate.done: от 0 до total";
        if (InProgress < 0 || InProgress > Total - Done) return "featureEstimate.inProgress: от 0 до total − done";
        if (Percent is < 0 or > 100) return "featureEstimate.percent: от 0 до 100";
        var remaining = Remaining ?? [];
        if (remaining.Count > MaxRemaining) return $"featureEstimate.remaining: не больше {MaxRemaining} пунктов";
        if (remaining.Any(r => r.Length == 0 || r.Length > MaxRemainingLength))
            return $"featureEstimate.remaining: пункт непустой, не длиннее {MaxRemainingLength} символов";
        if (EtaMinutes is < 1) return "featureEstimate.etaMinutes: не меньше 1";
        if (Why is { Length: > MaxWhyLength }) return $"featureEstimate.why: не длиннее {MaxWhyLength} символов";
        return null;
    }

    // Trim пунктов и why; пустой why — null. Remaining null (ключа нет) — пустой список: после
    // Normalize Remaining не null.
    public FeatureEstimate Normalize() => this with
    {
        Remaining = (Remaining ?? []).Select(r => (r ?? "").Trim()).ToList(),
        Why = string.IsNullOrWhiteSpace(Why) ? null : Why.Trim(),
    };
}

// Критерий приёмки задачи: Id стабилен (на него ссылается свидетельство), Text — измеримая формулировка.
public record AcceptanceCriterion(string Id, string Text);

// Свидетельство по критерию: How — чем проверено, Result — что получилось (вывод, цифры).
public record AcceptanceEvidence(string CriterionId, string How, string Result);

// Результат предгейта: Sha — коммит дерева, по которому гнали проверки; Green — вердикт; Summary — вывод
// красных проверок (для зелёного пусто); RedKey — «отпечаток» причины красного (имена красных
// проверок), RedStreak — сколько красных подряд по одному RedKey (зелёный обнуляет).
// NothingApplicable — отдельный вердикт «ни одна проверка не применима» (Green при этом false: «зелёным» он не считается,
// ни одна проверка не выполнена); закрытие шага он разрешает.
public record PregateRecord(string Sha, bool Green, string Summary, DateTime At, int RedStreak = 0, string? RedKey = null,
    bool NothingApplicable = false);

public class TaskSubtask
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "";
    public bool IsDone { get; set; }
}

// Шаг плана (TaskItem.PlanSteps): код шага (StepRef), его название (Title) и линия (Lane).
// StepRef — ключ шага (тот же, что у step/rework): непуст и уникален внутри задачи.
// Title — человекочитаемое название шага; Lane — линия (бэк/фронт), как у шагов.
// Лимиты — константы (единая точка для валидации в TaskManager и для тестов).
public class PlannedStep
{
    // Потолок числа шагов плана на задачу: план — не таблица на тысячу строк.
    public const int MaxSteps = 200;
    // Максимальная длина кода шага (StepRef) и названия (Title). Длина — в UTF-16 code units
    // (string.Length): эмодзи/суперскрипты съедают по 2 единицы (минор 8, оговорено).
    public const int MaxStepRefLength = 40;
    public const int MaxTitleLength = 200;

    public string StepRef { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Lane { get; set; }
}

public enum TaskItemStatus { Todo, InProgress, Done }
public enum TaskItemPriority { Low, Medium, High, Urgent }
public enum TaskItemAssignee { Me, Claude }

// Правило повторения задачи. Weekdays — ISO-дни недели (1=Пн … 7=Вс), только для Weekly.
// Until — последняя допустимая дата серии (YYYY-MM-DD, включительно).
public class TaskRecurrence
{
    public TaskRecurrenceType Type { get; set; } = TaskRecurrenceType.Daily;
    public int Interval { get; set; } = 1;   // каждые N дней/недель/месяцев/лет
    public List<int>? Weekdays { get; set; }
    public string? Until { get; set; }
}

// None — wire-сентинел в UpdateTaskRequest: «убрать повторение» (аналог "" у строк)
public enum TaskRecurrenceType { None, Daily, Weekly, Monthly, Yearly }
