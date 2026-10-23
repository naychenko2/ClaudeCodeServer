using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Journal;
using ClaudeHomeServer.Services.Tasks.Projector;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Tasks;

// Задачи: in-memory + data/tasks.json (по образцу ProjectManager)
public class TaskManager : ClaudeHomeServer.Services.Composition.ITaskStatusReader, IDisposable
{
    private readonly ConcurrentDictionary<string, TaskItem> _tasks = new();
    private readonly string _storePath;
    private readonly Lock _saveLock = new();

    /// <summary>
    /// Формат записи ТОЛЬКО этого стора. <see cref="JsonFileStore"/> общий для всех сторов,
    /// поэтому опции передаются точечно в <c>JsonFileStore.Save</c> отсюда, а его дефолт
    /// (и, значит, формат остальных сторов) не меняется.
    ///
    /// Что даёт: у дефолтного энкодера System.Text.Json любой не-ASCII символ уезжает в
    /// <c>\uXXXX</c> — кириллическая буква занимает 6 байт вместо 2 в UTF-8. Задачи почти
    /// целиком состоят из русского текста (заголовок, описание, итог), и на проде это
    /// давало 56 МБ файла при ~18 МБ полезных данных. Каждая запись стора — полная
    /// пересериализация, так что лишние байты — это ещё и лишние секунды.
    ///
    /// <c>UnsafeRelaxedJsonEscaping</c> здесь безопасен: файл читает только
    /// System.Text.Json, ни в HTML, ни в JS он не встраивается (наружу задачи уходят
    /// сериализатором ASP.NET со своими опциями). «Unsafe» в имени — ровно про
    /// HTML-контекст, которого у файлового стора нет.
    ///
    /// <c>WriteIndented = false</c> выставлен явно, хотя он и совпадает с дефолтом:
    /// формат стора — осознанное решение, а не побочный эффект чужого дефолта.
    /// </summary>
    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Окно дебаунса записи стора по умолчанию. Переопределяется ключом
    /// <c>Tasks:SaveDebounceMs</c>; 0 и меньше — таймер не заводится вовсе и запись идёт
    /// синхронно, как раньше (нужно тестам и как аварийный откат поведения).
    ///
    /// Почему секунда: типичная нагрузка — пачка изменений одной задачи подряд
    /// (создание → привязка сессии → статус → подзадачи → итог), она укладывается в
    /// доли секунды и схлопывается в ОДНУ запись вместо пяти. При этом потолок
    /// «протухания» файла на диске — 1 с, что на порядок меньше уже принятых в продукте
    /// 30 с автосохранения сессий. Больше секунды брать незачем: выигрыш от схлопывания
    /// дальше почти не растёт, а окно потери при НЕштатной смерти процесса растёт линейно
    /// (штатная остановка сбрасывает всё, см. <see cref="Flush"/>).
    /// </summary>
    private static readonly TimeSpan DefaultSaveDebounce = TimeSpan.FromMilliseconds(1000);
    private readonly TimeSpan _saveDebounce;
    // null — режим синхронной записи (Tasks:SaveDebounceMs <= 0)
    private readonly Timer? _saveTimer;
    // Отдельный короткий лок на «грязный» флаг: брать под ним _saveLock нельзя, обратный
    // порядок (сначала _saveLock, потом _dirtyLock) — единственный разрешённый.
    private readonly Lock _dirtyLock = new();
    private bool _dirty;
    private bool _timerArmed;
    private bool _disposed;
    private readonly IProjectEventLogService? _events;
    private readonly ITaskNotificationDispatcher? _notif;
    // Узкий шов чтения персоны по id (Core): Tasks нужно только имя для лога
    // проактивного уведомления о спавне следующего экземпляра регулярной задачи.
    // Полный PersonaManager ради одного `GetByIdInternal` — overkill.
    private readonly IPersonaLookup? _personas;
    // Журнал событий фичи (волна 3, Б8b): append-only JSONL. Запись переходов задач
    // фичи (старт/остановка/ожидание/статус/вердикт/снятие) — см. WriteJournalTransitions.
    // Опциональный: тесты без журнала (null) пишут в воздух. Та же вертикаль (Tasks.Journal),
    // новых швов на другие вертикали нет.
    private readonly FeatureJournalStore? _journal;
    // Логгер для страховки записи журнала: хранилище глотает свои ошибки, но сбой
    // не должен ронять мутацию задачи — логируем и идём дальше.
    private readonly ILogger<TaskManager>? _log;

    // Единственный путь в Done (UI/MCP/планировщик — всё через Update). Подписчик —
    // TaskExecutionService.TryDeliverCompletionAsync (join сигналов R/D, см. CompletionDelivered).
    public event Action<TaskItem>? TaskCompleted;

    public TaskManager(IConfiguration config, IProjectEventLogService? events = null,
        ITaskNotificationDispatcher? notif = null, IPersonaLookup? personas = null,
        FeatureJournalStore? journal = null, ILogger<TaskManager>? log = null)
    {
        _events = events;
        _notif = notif;
        _personas = personas;
        _journal = journal;
        _log = log;
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        _storePath = Path.Combine(dataDir, "tasks.json");
        _saveDebounce = int.TryParse(config["Tasks:SaveDebounceMs"], out var ms)
            ? TimeSpan.FromMilliseconds(ms)
            : DefaultSaveDebounce;
        // Таймер одноразовый: заводится в Infinite и взводится каждый раз вручную из
        // ScheduleSave — периодический тик впустую будил бы процесс при простое.
        if (_saveDebounce > TimeSpan.Zero)
            _saveTimer = new Timer(_ => OnSaveTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Load();
        // Раньше здесь конструктор ставил СТАТИЧЕСКИЕ резолверы Session (ParentSessionId/TaskDone).
        // Снят: вычисляемые «связи» сессии теперь считает SessionTaskLinks (Core) поверх
        // ITaskLookup (адаптер в Program.cs), и спин-модель Session больше не зависит от
        // вертикали Tasks.
    }

    // Запись в проектный лог (только для задач с projectId — личные в лог не попадают).
    // Actor — персона-исполнитель (по id, фронт резолвит в подпись) либо «user»/«system».
    private void LogTask(TaskItem task, string type, string summary) =>
        _events?.Append(task.ProjectId ?? "", task.OwnerId ?? "", type, task.PersonaId ?? "user", summary, task.Id);

    // ─── Журнал событий фичи (волна 3, Б8b) ─────────────────────────────────────
    // Снимок 6 полей задачи, которые пишут события в журнал фичи. Сравнивается ДО/ПОСЛЕ
    // мутации: событие пишется ТОЛЬКО при смене значения (идемпотентность).
    private struct TaskTransition
    {
        public DateTime? ClaudeStartedAt;
        public DateTime? ExecutorStoppedAt;
        public string? ExecutorStopReason;
        public DateTime? AwaitingHumanSince;
        public string? AwaitingHumanReason;
        public DateTime? DeviceWaitSince;
        public TaskItemStatus Status;
        public string? Verdict;
        public DateTime? DroppedByHumanAt;
    }

    // Снимок текущего состояния 6 полей задачи.
    private static TaskTransition Capture(TaskItem t) => new()
    {
        ClaudeStartedAt = t.ClaudeStartedAt,
        ExecutorStoppedAt = t.ExecutorStoppedAt,
        ExecutorStopReason = t.ExecutorStopReason,
        AwaitingHumanSince = t.AwaitingHumanSince,
        AwaitingHumanReason = t.AwaitingHumanReason,
        DeviceWaitSince = t.DeviceWaitSince,
        Status = t.Status,
        Verdict = t.Verification?.Verdict,
        DroppedByHumanAt = t.DroppedByHumanAt,
    };

    // Сравнивает 6 полей задачи ДО (before) и ПОСЛЕ (текущее состояние task) мутации и
    // пишет в журнал фичи события для каждого изменившегося поля. Время события — значение
    // самого поля (started/stopped/waiting/dropped); у снятий (stop_cleared/waiting_cleared)
    // и у status/verdict — момент мутации (DateTime.UtcNow). Задача без фичи не пишется.
    // Сбой записи не роняет мутацию (AppendJournal).
    private void WriteJournalTransitions(TaskItem task, TaskTransition before)
    {
        // featureId: у эпика (Role=feature) — его собственный id, у дочерней — FeatureId.
        var featureId = task.Role == TaskRoles.Feature ? task.Id : task.FeatureId;
        if (featureId is null) return;
        var ownerId = task.OwnerId ?? "";
        var now = DateTime.UtcNow;

        // started: ClaudeStartedAt задан (смена значения).
        if (before.ClaudeStartedAt != task.ClaudeStartedAt && task.ClaudeStartedAt is not null)
            AppendJournal(ownerId, featureId, new FeatureJournalEntry
            {
                At = task.ClaudeStartedAt.Value,
                Kind = "started",
                TaskId = task.Id,
                StartedAt = task.ClaudeStartedAt,
            });

        // stopped / stop_cleared: ExecutorStoppedAt задан или снят.
        if (before.ExecutorStoppedAt != task.ExecutorStoppedAt)
        {
            if (task.ExecutorStoppedAt is not null)
                AppendJournal(ownerId, featureId, new FeatureJournalEntry
                {
                    At = task.ExecutorStoppedAt.Value,
                    Kind = "stopped",
                    TaskId = task.Id,
                    Reason = task.ExecutorStopReason,
                });
            else
                AppendJournal(ownerId, featureId, new FeatureJournalEntry
                {
                    At = now,
                    Kind = "stop_cleared",
                    TaskId = task.Id,
                });
        }

        // waiting / waiting_cleared: AwaitingHumanSince задан или снят.
        if (before.AwaitingHumanSince != task.AwaitingHumanSince)
        {
            if (task.AwaitingHumanSince is not null)
                AppendJournal(ownerId, featureId, new FeatureJournalEntry
                {
                    At = task.AwaitingHumanSince.Value,
                    Kind = "waiting",
                    TaskId = task.Id,
                    Reason = task.AwaitingHumanReason,
                });
            else
                AppendJournal(ownerId, featureId, new FeatureJournalEntry
                {
                    At = now,
                    Kind = "waiting_cleared",
                    TaskId = task.Id,
                });
        }

        // device_wait / device_wait_cleared: DeviceWaitSince задан или снят (ожидание
        // устройства локального проекта — «простой» для timeline, рекомендация Александра).
        if (before.DeviceWaitSince != task.DeviceWaitSince)
        {
            if (task.DeviceWaitSince is not null)
                AppendJournal(ownerId, featureId, new FeatureJournalEntry
                {
                    At = task.DeviceWaitSince.Value,
                    Kind = "device_wait",
                    TaskId = task.Id,
                    Reason = task.DeviceWaitReason,
                });
            else
                AppendJournal(ownerId, featureId, new FeatureJournalEntry
                {
                    At = now,
                    Kind = "device_wait_cleared",
                    TaskId = task.Id,
                });
        }

        // status: смена статуса (from/to).
        if (before.Status != task.Status)
            AppendJournal(ownerId, featureId, new FeatureJournalEntry
            {
                At = now,
                Kind = "status",
                TaskId = task.Id,
                From = before.Status.ToString(),
                To = task.Status.ToString(),
            });

        // verdict: смена вердикта.
        if (before.Verdict != task.Verification?.Verdict)
            AppendJournal(ownerId, featureId, new FeatureJournalEntry
            {
                At = now,
                Kind = "verdict",
                TaskId = task.Id,
                Verdict = task.Verification?.Verdict,
            });

        // dropped: DroppedByHumanAt задан (снятие человеком).
        if (before.DroppedByHumanAt != task.DroppedByHumanAt && task.DroppedByHumanAt is not null)
            AppendJournal(ownerId, featureId, new FeatureJournalEntry
            {
                At = task.DroppedByHumanAt.Value,
                Kind = "dropped",
                TaskId = task.Id,
            });
    }

    // Запись в журнал фичи со страховкой: хранилище глотает свои ошибки, но сбой не
    // должен ронять мутацию задачи — логируем и идём дальше.
    private void AppendJournal(string ownerId, string featureId, FeatureJournalEntry entry)
    {
        if (_journal is null) return;
        try
        {
            _journal.Append(ownerId, featureId, entry);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Журнал фичи: не удалось записать {Kind} для {Feature}", entry.Kind, featureId);
        }
    }

    // Каскад: файл журнала фичи удаляется ТОЛЬКО вместе с её ЭПИКОМ (Role=feature,
    // ключ — id самого эпика). У дочерней задачи (шаг/ревью/консультация) эпик жив,
    // поэтому её журнал НЕ сносится — иначе удаление ЛЮБОГО шага стирало бы всю историю
    // фичи (critical: журнал — невосстановимая история). DeleteByProject наследует:
    // сносятся журналы эпиков удаляемого проекта, а эпик, живущий в другом проекте
    // (не в removed), не затрагивается.
    private void DeleteJournalFor(TaskItem task)
    {
        if (_journal is null) return;
        if (task.Role != TaskRoles.Feature) return;
        var featureId = task.Id;
        try
        {
            _journal.Delete(task.OwnerId ?? "", featureId);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Журнал фичи: не удалось удалить {Feature}", featureId);
        }
    }

    public TaskItem? GetById(string id) => _tasks.GetValueOrDefault(id);

    // Задача, к которой привязана сессия (Claude-исполнитель)
    public TaskItem? GetBySession(string sessionId) =>
        _tasks.Values.FirstOrDefault(t => t.LinkedSessionId == sessionId);

    // Задачи, делегированные из указанного чата-постановщика и ещё ждущие своего исполнителя:
    // координатор в цикле «до готово» уходит в фазу waiting, пока такие задачи не закроются.
    // Условия «живая» — без терминальных состояний, без уже доставленного доклада и без
    // остановки исполнителя: см. комментарий у TaskExecutionService.HasLiveDelegatedTask,
    // который фильтрует по тому же набору. Здесь отдаём ВСЕ задачи чата — фильтр на стороне
    // вызывающего, чтобы один линейный проход по стору остался здесь.
    public IReadOnlyCollection<TaskItem> GetBySourceSession(string sourceSessionId) =>
        _tasks.Values.Where(t => t.SourceSessionId == sourceSessionId).ToList();

    public IReadOnlyCollection<TaskItem> GetByOwner(string userId) =>
        _tasks.Values.Where(t => t.OwnerId == userId)
            .OrderBy(t => t.DueDate ?? "9999").ThenBy(t => t.CreatedAt).ToList();

    public IReadOnlyCollection<TaskItem> GetByProject(string projectId) =>
        _tasks.Values.Where(t => t.ProjectId == projectId)
            .OrderBy(t => t.DueDate ?? "9999").ThenBy(t => t.CreatedAt).ToList();

    // Задачи ВЛАДЕЛЬЦА, промоутнутые из чекбоксов конкретной заметки.
    // Фильтр по ownerId обязателен: id заметки личного vault владельца не содержит —
    // это base64 от «personal|{относительный путь}», поэтому у двух пользователей
    // заметка по одному пути (например дневник Journal/{дата}.md) даёт побайтово
    // одинаковый noteId. Без фильтра чужой пользователь читал и правил бы задачи
    // соседа через ручки заметок.
    public IReadOnlyCollection<TaskItem> GetBySourceNote(string ownerId, string noteId) =>
        _tasks.Values.Where(t => t.OwnerId == ownerId && t.SourceNoteId == noteId).ToList();

    public TaskItem Create(string? projectId, string ownerId, CreateTaskRequest req,
        BoardColumn? targetColumn = null)
    {
        // Клиентский id (офлайн-создание) с идемпотентностью: повтор POST с тем же id
        // при потерянном ack возвращает существующую задачу — без дубля. Если id занят
        // другим владельцем — игнорируем присланный, генерируем новый.
        var id = string.IsNullOrEmpty(req.Id) ? Guid.NewGuid().ToString() : req.Id;
        if (_tasks.TryGetValue(id, out var dup))
        {
            if (dup.OwnerId == ownerId) return dup;
            id = Guid.NewGuid().ToString();
        }

        // Персона-исполнитель: assignee=Claude выставляется автоматом, если модель забыла
        var hasPersona = !string.IsNullOrEmpty(req.PersonaId);
        var task = new TaskItem
        {
            Id = id,
            ProjectId = projectId,
            OwnerId = ownerId,
            Title = req.Title,
            Description = req.Description ?? "",
            Status = req.Status ?? TaskItemStatus.Todo,
            ColumnId = string.IsNullOrEmpty(req.ColumnId) ? null : req.ColumnId,
            Priority = req.Priority ?? TaskItemPriority.Medium,
            DueDate = string.IsNullOrEmpty(req.DueDate) ? null : req.DueDate,
            DueTime = string.IsNullOrEmpty(req.DueTime) ? null : req.DueTime,
            ReminderMinutes = req.ReminderMinutes is < 0 ? null : req.ReminderMinutes,
            Recurrence = req.Recurrence is { Type: not TaskRecurrenceType.None } ? req.Recurrence : null,
            // personaId подразумевает исполнителя Claude — нормализуем, если модель передала не то
            Assignee = hasPersona ? TaskItemAssignee.Claude : (req.Assignee ?? TaskItemAssignee.Me),
            LinkedSessionId = req.LinkedSessionId,
            PersonaId = hasPersona ? req.PersonaId : null,
            // Не передано — дефолт 1440 (сутки); отрицательное — бессрочно; N>=0 — TTL
            ExecutionExpiresAfterMinutes = req.ExecutionExpiresAfterMinutes switch
            {
                null => 1440,
                < 0 => null,
                _ => req.ExecutionExpiresAfterMinutes,
            },
            // Уровень модели исполнителя: мусор и пустая строка — «не задан» (валидация — в контроллере)
            ModelTier = ModelTiers.TryParse(req.ModelTier, out var tier) ? tier : null,
            // Дерево исполнителя: пустое значение = «не задано» (нормализация — ниже)
            WorktreePath = Trimmed(req.WorktreePath),
            WorktreeBranch = Trimmed(req.WorktreeBranch),
            ResultMarkdown = req.ResultMarkdown,
            LinkedFiles = req.LinkedFiles ?? [],
            Subtasks = req.Subtasks?.Select(s => new TaskSubtask { Title = s.Title }).ToList() ?? [],
            Labels = req.Labels ?? [],
            SourceNoteId = req.SourceNoteId,
            SourceNoteLine = req.SourceNoteLine,
            CreatedByPersonaId = string.IsNullOrEmpty(req.CreatedByPersonaId) ? null : req.CreatedByPersonaId,
            SourceSessionId = string.IsNullOrEmpty(req.SourceSessionId) ? null : req.SourceSessionId,
            ReportToSessionId = string.IsNullOrEmpty(req.ReportToSessionId) ? null : req.ReportToSessionId,
            DelegationDepth = ComputeDelegationDepth(req.SourceSessionId),
            Order = NextOrder(ownerId),
            // Создаём задачу сразу готовой (редко) — фиксируем момент завершения
            CompletedAt = req.Status == TaskItemStatus.Done ? DateTime.UtcNow : null,
            Kind = req.Kind ?? TaskKind.Task,
            Repro = req.Repro,
            // Фича (режим «Исполнение фичи»): разметка, пустая строка = «не задано»
            FeatureId = Trimmed(req.FeatureId),
            Role = Trimmed(req.Role),
            StepRef = Trimmed(req.StepRef),
            Lane = Trimmed(req.Lane),
            ReviewOf = req.ReviewOf,
            PlanSteps = NormalizePlanStepsForRole(NormalizePlanSteps(req.PlanSteps), Trimmed(req.Role)),
            AwaitingHumanSince = req.AwaitingHumanSince,
            AwaitingHumanReason = Trimmed(req.AwaitingHumanReason),
            Verification = req.Verification,
            Acceptance = AcceptanceRules.Normalize(req.Acceptance),
        };
        // Серия регулярной задачи начинается с её первого экземпляра
        if (task.Recurrence is not null) task.SeriesId = task.Id;

        // Наследование featureId: если задачу создаёт чат-исполнитель задачи-фичи
        // (тот же поиск родителя, что ComputeDelegationDepth), а featureId не задан
        // явно — привязываем к эпике. Role=feature у родителя — обязательное условие,
        // иначе контекст ложный (см. FindOwningFeature).
        if (task.FeatureId is null)
        {
            var owning = FindOwningFeature(req.SourceSessionId, null, ownerId);
            if (owning is not null)
                task.FeatureId = owning.Id;
        }

        // Дефект внутри фичи: роль qa (агент, заводящий баг, сам не знает, что он
        // в фиче — сервер ставит роль, чтобы FeatureProjector не потерял дефект).
        if (task.Role is null && task.FeatureId is not null && task.Kind == TaskKind.Defect)
            task.Role = TaskRoles.Qa;

        // Инвариант: исполнитель-персона подразумевает исполнение силами Claude
        NormalizePersonaAssignee(task);
        NormalizeWorktree(task);
        // Дефект: нельзя создать сразу в Done (по статусу или по целевой колонке),
        // нельзя попасть в review-колонку без шагов воспроизведения (DefectRules; no-op
        // для обычных задач). Бросает до вставки в словарь — при отказе состояние
        // менеджера не меняется. Гейт EnsureReproOnReview смотрит на targetColumn.Role,
        // review-признак резолвится вызывающей стороной (BoardColumnHelper.IsReview).
        DefectRules.EnsureNotClosedAtCreate(task, targetColumn);
        DefectRules.EnsureReproOnReview(task, targetColumn);
        // Критерии приёмки: создать сразу закрытой задачу с критериями нельзя — свидетельств ещё нет
        if (task.Status == TaskItemStatus.Done || targetColumn is { Category: TaskItemStatus.Done })
            AcceptanceRules.EnsureClosable(task.Acceptance, task.Evidence);
        // Фича: fail-closed валидация featureId/role/verdict/reviewOf/planSteps (до вставки в
        // словарь, чтобы при отказе состояние менеджера не изменилось). При создании связь
        // доработки с ревью (enforceReworkReviewOf) проверяется всегда.
        ValidateFeatureFields(task.FeatureId, task.Role, task.Verification?.Verdict,
            task.ReviewOf, task.PersonaId, ownerId,
            planSteps: task.PlanSteps, enforceReworkReviewOf: true);
        EnsureReturnReason(task.Verification);
        // Инвариант (шов Александра 63a69b77 п.1, замечание 7): вердикт и исход дефекта — только
        // у закрытой задачи, по образцу Update (при уходе из Done обнуляются и они, и CompletedAt).
        // Без этого незакрытая задача получила бы вердикт, и опора «без CompletedAt остаются только
        // идущие витки» (FeatureStepAccounting, порядок витков) перестала бы быть правдой.
        if (task.Status != TaskItemStatus.Done)
        {
            task.Verification = null;
            task.Outcome = null;
        }
        _tasks[task.Id] = task;
        // created: эпик фичи — первая запись её журнала (журнал точный, только если
        // начинается с created; дочерние задачи фичи created не пишут).
        if (task.Role == TaskRoles.Feature)
            AppendJournal(task.OwnerId ?? "", task.Id, new FeatureJournalEntry
            {
                At = task.CreatedAt,
                Kind = "created",
                TaskId = task.Id,
            });
        // Переходы: начальные значения полей (свежая задача → before = дефолт Todo).
        WriteJournalTransitions(task, new TaskTransition { Status = TaskItemStatus.Todo });
        ScheduleSave();
        LogTask(task, ProjectEventTypes.TaskCreated, $"Создана задача «{task.Title}»");
        return task;
    }

    // Персона-исполнитель имеет смысл только у Claude-исполнения: если задаче назначена
    // персона, принудительно ставим Assignee=Claude (иначе автозапуск планировщиком,
    // завязанный на Assignee==Claude, не подхватит задачу — рассинхрон).
    private static void NormalizePersonaAssignee(TaskItem task)
    {
        if (task.PersonaId is not null) task.Assignee = TaskItemAssignee.Claude;
    }

    // Инвариант: worktree — дерево репы ПРОЕКТА. У личной задачи (и у задачи, которую сделали
    // личной) хранить путь незачем — исполнитель всё равно стартует в чате вне проекта.
    private static void NormalizeWorktree(TaskItem task)
    {
        if (task.ProjectId is not null) return;
        task.WorktreePath = null;
        task.WorktreeBranch = null;
    }

    // Пустая/пробельная строка запроса = «значение не задано»
    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Нормализация списка шагов плана (миноры 5+6): КОПИЯ (входной список не мутируем —
    // вызывающий мог удержать ссылку) + трим StepRef/Title. Trim: «Б1» и « Б1» — один код, не
    // дубль. Дубль stepRef — OrdinalIgnoreCase (после трима) в ValidateFeatureFields: «B1»/«b1»
    // и «Б1»/«б1» — один шаг; Ordinal (не culture) — детерминизм и скорость.
    // null в StepRef/Title оставляем null (не «»): валидатор их ловит (пустой stepRef, null
    // title → 400, а не NRE). null — «не задано» (не менять); пустой список — «снять список».
    private static List<PlannedStep>? NormalizePlanSteps(List<PlannedStep>? steps)
    {
        if (steps is null) return null;
        var copy = new List<PlannedStep>(steps.Count);
        foreach (var s in steps)
        {
            if (s is null) { copy.Add(null!); continue; }
            copy.Add(new PlannedStep
            {
                StepRef = s.StepRef?.Trim(),
                Title = s.Title?.Trim(),
                Lane = s.Lane,
            });
        }
        return copy;
    }

    // Инвариант «PlanSteps только у role=plan»: пустой список у НЕ-плана — артефакт (снят/не
    // задан), нормализуем в null, чтобы не держать пустой список там, где его не ждём (без
    // исключений). У плана пустой список — легитимный «снять список» (тест
    // Update_ПланСЯвнымПустымСписком), оставляем не-null.
    private static List<PlannedStep>? NormalizePlanStepsForRole(List<PlannedStep>? steps, string? role) =>
        steps is { Count: 0 } && role != TaskRoles.Plan ? null : steps;

    // Валидация полей фичи (fail-closed, режим «Исполнение фичи»). Проверяет ЭФФЕКТИВНОЕ
    // состояние — то, что окажется на задаче после операции: Create передаёт только что
    // заполненную task, Update — слияние req и текущего состояния. ownerId — владелец задачи;
    // по нему сверяются ВСЕ ссылки (эпик и reviewOf), иначе чужой владелец читал бы разметку.
    // planSteps — эффективный список шагов плана (только у role=plan); enforceReworkReviewOf —
    // проверять ли обязательную связь доработки с ревью (Create — всегда, Update — только если
    // поле reviewOf меняют). roleBefore — роль ДО операции (Create — null, Update — текущая
    // роль задачи); featureIdBefore — фича ДО операции (Create — null, Update — текущая фича
    // задачи): гард role=merge срабатывает при СМЕНЕ роли на merge ИЛИ при переносе merge-задачи
    // в другую фичу. Бросает InvalidOperationException с внятным текстом — вызывающая сторона
    // превращает в 400.
    // Тихо отбрасывать неверное значение нельзя: агент не узнает, что разметка не легла.
    // Причина возврата проверяется только у присланного вердикта: хранимый уже прошёл проверку при записи.
    // Санирование (trim, пусто → null) — здесь, единственная точка для REST и MCP: пробелы вокруг
    // допустимого значения не должны давать разный исход у двух дверей, а «   » — попадать в стор
    private static void EnsureReturnReason(TaskVerification? verification)
    {
        if (verification is null) return;
        verification.ReturnReason = string.IsNullOrWhiteSpace(verification.ReturnReason)
            ? null : verification.ReturnReason.Trim();
        TaskReturnReasons.Validate(verification.Verdict, verification.ReturnReason, verification.Notes);
    }

    private void ValidateFeatureFields(
        string? featureId, string? role, string? verdict,
        List<string>? reviewOf, string? personaId, string ownerId,
        List<PlannedStep>? planSteps = null, bool enforceReworkReviewOf = false,
        string? roleBefore = null, string? featureIdBefore = null)
    {
        // role — только из списка TaskRoles
        if (role is not null && !TaskRoles.All.Contains(role))
            throw new InvalidOperationException(
                $"Недопустимая роль фичи «{role}». Допустимые: {string.Join(", ", TaskRoles.All)}.");

        // Привязанная к фиче роль (не feature/cut/plan) обязана указывать на живой эпик:
        // иначе задача «висяк» — роль есть, а фичи нет (fail-closed, шаг 1.2). featureId
        // null для не-привязанных ролей (feature/cut/plan) — валидно, как и раньше.
        if (role is not null && !TaskRoles.FeatureIdOptional.Contains(role) && featureId is null)
            throw new InvalidOperationException(
                $"Роль {role} требует featureId — укажите id эпика фичи.");

        // verdict — только из списка TaskVerdicts
        if (verdict is not null && !TaskVerdicts.All.Contains(verdict))
            throw new InvalidOperationException(
                $"Недопустимый вердикт «{verdict}». Допустимые: {string.Join(", ", TaskVerdicts.All)}.");

        // Эпик фичи не запускается исполнителем: role=feature + personaId — рассинхрон
        // (NormalizePersonaAssignee сделал бы его Claude-задачей, которую можно было запустить).
        if (role == TaskRoles.Feature && personaId is not null)
            throw new InvalidOperationException("Эпик фичи не запускается исполнителем: уберите personaId.");

        // featureId: эпик существует, тот же владелец и Role=feature
        if (featureId is not null)
        {
            var epic = GetById(featureId);
            if (epic is null)
                throw new InvalidOperationException($"Фича не найдена: задача-эпик «{featureId}» не существует.");
            if (epic.OwnerId != ownerId)
                throw new InvalidOperationException("Эпик принадлежит другому владельцу.");
            if (epic.Role != TaskRoles.Feature)
                throw new InvalidOperationException($"Это не эпик фичи: у задачи «{featureId}» роль ≠ feature.");

            // role=merge: фича обязана быть Ready — финал принят (тот же хелпер, что в проекторе).
            // Гард Ready — для СОЗДАНИЯ и СМЕНЫ роли на merge (roleBefore != merge).
            // Перенос уже существующей merge-задачи в другую фичу запрещён целиком: Done-merge
            // из A в Ready-фичу B объявлял бы B смерженной чужой задачей (MergedAt чужой), а перенос
            // в смерженную тихо открывал её (merged → merge_pending); штатных сценариев переноса нет.
            // Частичный мерж/выкатка куска — без роли, с featureId эпика; «полный мерж без финала»
            // человек оформляет закрытием эпика (правило закрытия даёт merged). Flag обхода не заводим.
            // Update merge-задачи без смены роли И фичи (roleBefore == merge, featureId == featureIdBefore)
            // — гард молчит.
            var becameMerge = roleBefore is null || roleBefore != TaskRoles.Merge;
            var mergeMoved = roleBefore == TaskRoles.Merge && featureId != featureIdBefore;
            if (role == TaskRoles.Merge && mergeMoved)
                throw new InvalidOperationException(
                    "Роль merge привязана к своей фиче: перенос merge-задачи в другую фичу запрещён. "
                    + "Завершённый или частичный мерж оформляйте новой задачей (без роли для куска работы) в нужной фиче.");
            if (role == TaskRoles.Merge && becameMerge
                && !FeatureProjector.IsFinalAccepted(FeatureFinal(featureId, ownerId)))
                throw new InvalidOperationException(
                    "Фича ещё не готова к мержу: финал не принят. Кусок работы (частичный мерж, выкатка части) "
                    + "заводите без роли, с featureId эпика. Закрыть фичу без финала может человек — закрытием эпика.");
        }

        // reviewOf: каждая ссылка — на задачу той же фичи (featureId) и того же владельца
        if (reviewOf is not null)
        {
            foreach (var refId in reviewOf)
            {
                var target = GetById(refId);
                if (target is null)
                    throw new InvalidOperationException($"reviewOf: задача «{refId}» не найдена.");
                if (target.OwnerId != ownerId)
                    throw new InvalidOperationException($"reviewOf: задача «{refId}» принадлежит другому владельцу.");
                if (target.FeatureId != featureId)
                    throw new InvalidOperationException($"reviewOf: задача «{refId}» из другой фичи.");
            }
        }

        // PlanSteps: допустимы ТОЛЬКО у role=plan (у прочих ролей — 400); у плана — лимиты
        // числа шагов, длины stepRef/title и уникальность stepRef (разрез §2, честный прогресс).
        // Тупик роли (major 3): «role != plan → 400» срабатывал на ЛЮБОМ непустом списке, даже
        // пустом [] — а [] ≠ null, и null в update значит «не менять», поэтому план со списком
        // нельзя было ни перевести в step/rework, ни снять список. Правило: 400 только когда
        // список НЕПУСТОЙ и роль не plan; пустой список — «снять список» (у плана — очищает,
        // у не-плана — no-op). Смена роли с плана разрешена: при переводе на не-план список
        // очищается (см. Update) — иначе план с planSteps застревал бы навсегда.
        if (planSteps is not null && planSteps.Count > 0 && role != TaskRoles.Plan)
            throw new InvalidOperationException(
                "PlanSteps допустимы только у задачи-плана (role=plan).");
        if (planSteps is not null)
        {
            if (planSteps.Count > PlannedStep.MaxSteps)
                throw new InvalidOperationException(
                    $"Слишком много шагов плана: {planSteps.Count} (лимит {PlannedStep.MaxSteps}).");
            // Дубль stepRef — единый сравнитель кодов шагов (FeatureStepAccounting.StepKeyComparer,
            // OrdinalIgnoreCase, после трима, минор 5): «B1»/«b1» и «Б1»/«б1» — один шаг.
            var seen = new HashSet<string>(FeatureStepAccounting.StepKeyComparer);
            foreach (var step in planSteps)
            {
                // Элемент-null из JSON (planSteps:[null]) — внятный 400, а не NRE (major 2)
                if (step is null)
                    throw new InvalidOperationException(
                        "Шаг плана (planSteps) — null: ожидался объект {stepRef, title, lane}.");
                if (string.IsNullOrWhiteSpace(step.StepRef))
                    throw new InvalidOperationException("Код шага плана (stepRef) не может быть пустым.");
                if (step.StepRef.Length > PlannedStep.MaxStepRefLength)
                    throw new InvalidOperationException(
                        $"Код шага плана «{step.StepRef}» длиннее {PlannedStep.MaxStepRefLength} символов.");
                // Title — non-nullable string, но JSON кладёт null; null-title — 400, а не NRE (major 2)
                if (step.Title is null)
                    throw new InvalidOperationException("Название шага плана (title) не может быть null.");
                if (step.Title.Length > PlannedStep.MaxTitleLength)
                    throw new InvalidOperationException(
                        $"Название шага плана «{step.StepRef}» длиннее {PlannedStep.MaxTitleLength} символов.");
                if (!seen.Add(step.StepRef.Trim()))
                    throw new InvalidOperationException($"Дублируется код шага плана «{step.StepRef}».");
            }
        }

        // Rework: обязательная связь с ревью/проверкой/финалом той же фичи (fail-closed, §1).
        // Create — всегда; Update — только если поле reviewOf меняют (старые доработки без
        // reviewOf можно править, не трогая связь, и можно привязывать). Цикл reviewOf выше
        // уже проверил существование/владельца/фичу каждой ссылки — здесь только роль цели.
        if (enforceReworkReviewOf && role == TaskRoles.Rework)
        {
            if (reviewOf is null || reviewOf.Count == 0)
                throw new InvalidOperationException(
                    "Доработка (role=rework) обязана ссылаться на ревью/проверку/финал (reviewOf) той же фичи.");
            foreach (var refId in reviewOf)
            {
                var target = GetById(refId);
                if (target is not null
                    && target.Role is not (TaskRoles.Review or TaskRoles.Qa or TaskRoles.Final))
                    throw new InvalidOperationException(
                        $"Доработка (role=rework) ссылается на задачу «{refId}» с ролью «{target.Role}» — "
                        + "ожидался review/qa/final той же фичи.");
            }
        }
    }

    // Глубина цепочки делегирования новой задачи: если её создали из чата, который сам
    // сейчас исполняет родительскую задачу (её LinkedSessionId == sourceSessionId) —
    // глубина родителя + 1; иначе (обычный чат, UI, API) — 0.
    private int ComputeDelegationDepth(string? sourceSessionId) =>
        !string.IsNullOrEmpty(sourceSessionId) && GetBySession(sourceSessionId) is { } parent
            ? parent.DelegationDepth + 1
            : 0;

    // Родительская задача-фича (эпик) по контексту исполнителя: ищем задачу, которую
    // исполняет сессия-источник (тот же поиск, что ComputeDelegationDepth), и поднимаемся
    // к её фиче. executorLaunchedBySessionId — запасной путь: если sourceSessionId не дал
    // результата, пробуем сессию, запустившую исполнителя. null — контекст ложный или отсутствует.
    //
    // В жизни сессия исполняет НЕ эпик (Role=feature), а шаг/ревью/проверку/финал фичи:
    // эпик не запускается исполнителем (гейт 1.4 — role=feature + personaId — отказ).
    // У такой задачи FeatureId указывает на эпик — поднимаемся к нему. Если сама задача —
    // эпик (Role=feature, FeatureId=null), возвращаем её (обход гейта в тестах).
    private TaskItem? FindOwningFeature(string? sourceSessionId, string? executorLaunchedBySessionId, string ownerId)
    {
        TaskItem? candidate = null;
        if (!string.IsNullOrEmpty(sourceSessionId))
            candidate = GetBySession(sourceSessionId);
        if (candidate is null && !string.IsNullOrEmpty(executorLaunchedBySessionId))
            candidate = GetBySession(executorLaunchedBySessionId);
        if (candidate is null) return null;
        if (candidate.OwnerId != ownerId) return null;
        if (candidate.FeatureId is not null) return GetById(candidate.FeatureId);
        return candidate.Role == TaskRoles.Feature ? candidate : null;
    }

    // Финал фичи: тот же выбор, что в проекторе (FeatureProjector.BuildCore:
    // children.LastOrDefault(role=final)) в порядке GetByOwner. null — финала нет.
    // Единая точка правды для гарда role=merge (ValidateFeatureFields) и ComputeState.
    private TaskItem? FeatureFinal(string featureId, string ownerId) =>
        GetByOwner(ownerId)
            .Where(t => t.FeatureId == featureId)
            .LastOrDefault(t => t.Role == TaskRoles.Final);

    // Следующее значение Order для новой задачи владельца — в конец глобального порядка.
    // Внутри колонки относительный порядок сохраняется (сортировка на доске по Order).
    private double NextOrder(string ownerId) =>
        _tasks.Values.Where(t => t.OwnerId == ownerId).Select(t => t.Order).DefaultIfEmpty(0).Max() + 1000;

    // Гейты DefectRules смотрят на ИСХОДНОЕ состояние карточки, а не на переданный
    // переход: kind считается из task (а не req.Kind), а колонка для ревью-гейта — это
    // колонка, в которой карточка ОКАЖЕТСЯ после Update (effectiveColumn: новая из req,
    // иначе — текущая). Раньше оба признака брались из запроса, и обход сводился к
    // паре «kind: "task"» + «status: "done»» либо «repro: {}» без columnId на карточке,
    // уже стоящей в review-колонке.
    // isAgentCall (фикс-волна 4 team-blocker-honest): true для путей, которые приходят
    // ОТ персоны-исполнителя (MCP tools_update/tasks_complete/tags_apply через
    // TasksToolset/WorkspaceToolset). false для HTTP PUT — это человек. Используется
    // ТОЛЬКО гардом против затирания снятой задачи: человек вправе вернуть снятую задачу
    // в работу и снять пометку, а агенту мы обязаны отказать внятно — иначе «200 OK и
    // ничего не изменилось» (находка фикс-волны 4, мутация в TaskManagerTests).
    public TaskItem? Update(string id, UpdateTaskRequest req, BoardColumn? effectiveColumn = null,
        bool isAgentCall = false, bool callerIsExecutor = false)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null) return null;
        var statusBefore = task.Status;
        // Снимок 6 полей ДО мутации — для журнала фичи (WriteJournalTransitions перед ScheduleSave).
        var before = Capture(task);

        // Запоминаем статус до правки — для фиксации момента завершения (CompletedAt)
        var wasDone = task.Status == TaskItemStatus.Done;

        // Дефект: отказ вычисляется ДО первой мутации — этот метод правит хранимый объект
        // по ссылке, а не копию, поэтому эффективное состояние (что БУДЕТ после применения
        // req) собираем заранее и проверяем DefectRules на нём, не трогая task. Kind берём
        // из task, не из req — иначе дефект обходит гейты переданной сменой вида (находка 1
        // ревью Глеба): клиентский kind=Task на дефекте раньше превращал effective.Kind=Task
        // и снимал все гейты DefectRules, карточка закрывалась без Verification. Вид immutable
        // после создания: присылка req.Kind != task.Kind просто игнорируется (на UI форма
        // редактирования шлёт kind при каждом сохранении — сегмент «Задача / Дефект» легитимный
        // кейс переключения, отдельный коммит на запрет менять вид не заказывался).
        // Бросает InvalidOperationException — вызывающая сторона (контроллер) превращает в 400.

        // Защита от затирания (фикс-волна 4 team-blocker-honest, дефект f3965801): после снятия
        // человеком по карточке блокера поздний tasks_complete не должен переписывать
        // Status/Outcome — иначе задача выглядит штатно выполненной с отчётом исполнителя.
        // Гард применяется ТОЛЬКО к агентскому пути (isAgentCall=true): человек вправе
        // вернуть снятую задачу в работу — перевод из Done в Todo/InProgress снимает
        // пометку DroppedByHumanAt и сохраняет новый статус штатно. На человеческом пути
        // НЕ отказываем внятным 400, а на агентском — обязаны: иначе поздний tasks_complete
        // «молча» глотал Status, а человек не понимал, почему 200 OK и ничего не поменялось
        // (мутация в TaskManagerTests). Гонка неустранима (исполнитель мог писать отчёт в
        // момент снятия), поэтому ResultMarkdown исполнителя дописывается к пометке снятия
        // отдельной строкой, а Status/Outcome/Verification остаются от штаба. Поле — маркер
        // снятия; null — обычная задача, никакой защиты.
        bool humanReturnsToWork = false;
        if (task.DroppedByHumanAt is not null)
        {
            bool triesStatus = req.Status is not null;
            bool triesOutcome = req.Outcome is not null;
            bool triesVerification = req.Verification is not null;
            if (isAgentCall && (triesStatus || triesOutcome || triesVerification))
                throw new InvalidOperationException(
                    $"Задача «{task.Title}» снята человеком: правка статуса/исхода/вердикта " +
                    "заблокирована. Чтобы вернуть задачу в работу, перетащите её в нужную колонку в UI.");
            // Человек возвращает задачу в работу (Status в Todo/InProgress) — снимаем
            // пометку снятия и сохраняем новый статус штатно. Возврат В Done человеком
            // оставляем редким: снятую задачу человек обычно снова делает активной,
            // а закрыть руками — отдельный кейс, и пометка остаётся.
            humanReturnsToWork = !isAgentCall
                && triesStatus && req.Status != TaskItemStatus.Done;
            if (humanReturnsToWork)
                task.DroppedByHumanAt = null;
            if (!humanReturnsToWork)
            {
                req = req with
                {
                    Status = null,
                    Outcome = null,
                    Verification = null,
                    ResultMarkdown = AppendExecutorNote(task.ResultMarkdown, req.ResultMarkdown),
                };
            }
        }

        var effective = new TaskItem
        {
            Kind = task.Kind,
            Status = req.Status ?? task.Status,
            Repro = req.Repro ?? task.Repro,
            Verification = req.Verification ?? task.Verification,
            Outcome = req.Outcome ?? task.Outcome,
        };
        DefectRules.EnsureVerificationOnClose(effective);
        DefectRules.EnsureReproOnReview(effective, effectiveColumn);

        // Критерии приёмки и свидетельства: эффективное состояние считаем до мутации (как гейты
        // выше). Чат-исполнитель своей задачи критерии не меняет — иначе гейт обходится удалением
        // критерия; постановщик и человек меняют свободно. Свидетельства добавляют все.
        var newAcceptance = req.Acceptance is null ? null : AcceptanceRules.Normalize(req.Acceptance);
        if (callerIsExecutor && newAcceptance is not null
            && !AcceptanceRules.SameCriteria(newAcceptance, task.Acceptance))
            throw new InvalidOperationException(
                "Исполнитель не может менять критерии приёмки (acceptance): их задаёт постановщик. " +
                "Свидетельства по критериям передавайте в evidence.");
        var effAcceptance = newAcceptance ?? task.Acceptance;
        var effEvidence = req.Evidence is null
            ? AcceptanceRules.DropStaleEvidence(task.Evidence, task.Acceptance, effAcceptance)
            : AcceptanceRules.MergeEvidence(
                AcceptanceRules.DropStaleEvidence(task.Evidence, task.Acceptance, effAcceptance),
                req.Evidence, effAcceptance);
        // Критерий убрали — его свидетельство осиротело и отбрасывается
        effEvidence = effEvidence.Where(e => effAcceptance.Any(c => c.Id == e.CriterionId)).ToList();
        if (effective.Status == TaskItemStatus.Done && task.Status != TaskItemStatus.Done)
            AcceptanceRules.EnsureClosable(effAcceptance, effEvidence);

        // Фича: fail-closed валидация на ЭФФЕКТИВНОМ состоянии (req ?? task), до мутации.
        // null в req = не менять (берём из task), "" = сбросить (null) — та же семантика,
        // что и при применении полей ниже.
        var effFeatureId = req.FeatureId is null ? task.FeatureId : Trimmed(req.FeatureId);
        var effRole = req.Role is null ? task.Role : Trimmed(req.Role);
        var effVerdict = (req.Verification ?? task.Verification)?.Verdict;
        var effReviewOf = req.ReviewOf is null ? task.ReviewOf : req.ReviewOf;
        var effPersonaId = req.PersonaId is null ? task.PersonaId : (req.PersonaId == "" ? null : req.PersonaId);
        var effPlanSteps = req.PlanSteps is null ? task.PlanSteps : NormalizePlanSteps(req.PlanSteps);
        // Тупик роли (major 3): перевод плана на не-план очищает список — эффективный список
        // считаем null, иначе валидатор видел бы «planSteps ≠ null + role ≠ plan» → ложный 400.
        if (task.Role == TaskRoles.Plan && effRole != TaskRoles.Plan)
            effPlanSteps = null;
        // Смена роли плана на не-план И явный НЕПУСТОЙ planSteps в одном запросе — несовместимы:
        // перевод очищает список (выше), поэтому присланный список исчезнет молча (тихая потеря;
        // регресс: до дельты этот ход давал 400 «PlanSteps только у плана»). Отказываем внятным 400:
        // сначала переведите задачу, потом задайте список, либо снимите список пустым []. Пустой []
        // со сменой роли — допустим (снимает), поэтому проверка — только на непустой.
        if (task.Role == TaskRoles.Plan && effRole != TaskRoles.Plan
            && req.PlanSteps is not null && req.PlanSteps.Count > 0)
            throw new InvalidOperationException(
                "Смена роли на не-план и задание planSteps в одном запросе несовместимы: "
                + "сначала переведите задачу, потом задайте список, либо снимите список пустым массивом.");
        // Связь доработки с ревью (enforceReworkReviewOf) проверяем, когда: (а) меняют поле
        // reviewOf, или (б) эффективная роль ТОЛЬКО СЕЙЧАС стала rework (задача раньше не была
        // rework) — иначе агент создаёт step и tasks_update {role:"rework"} обходит гейт (major 1
        // ревью): роль менялась свободно, а гейт срабатывал только при явном reviewOf. Старые
        // доработки без reviewOf можно править, не трогая связь, и можно привязывать: role уже
        // rework и reviewOf не меняют → enforce=false.
        var enforceReworkReviewOf = req.ReviewOf is not null
            || (effRole == TaskRoles.Rework && task.Role != TaskRoles.Rework);
        ValidateFeatureFields(effFeatureId, effRole, effVerdict, effReviewOf, effPersonaId, task.OwnerId ?? "",
            planSteps: effPlanSteps, enforceReworkReviewOf, roleBefore: task.Role, featureIdBefore: task.FeatureId);
        EnsureReturnReason(req.Verification);

        if (req.Title is not null) task.Title = req.Title;
        if (req.Description is not null) task.Description = req.Description;
        if (req.Status is not null) task.Status = req.Status.Value;
        if (req.Priority is not null) task.Priority = req.Priority.Value;
        // Пустая строка = очистить поле, null = не менять
        var dueBefore = (task.DueDate, task.DueTime, task.ReminderMinutes);
        if (req.DueDate is not null) task.DueDate = req.DueDate == "" ? null : req.DueDate;
        if (req.DueTime is not null) task.DueTime = req.DueTime == "" ? null : req.DueTime;
        // Для int-поля семантика очистки — отрицательное значение (аналог "" у строк)
        if (req.ReminderMinutes is not null)
            task.ReminderMinutes = req.ReminderMinutes < 0 ? null : req.ReminderMinutes;
        // Срок или офсет поменялись — напоминание должно сработать заново
        if (dueBefore != (task.DueDate, task.DueTime, task.ReminderMinutes))
            task.ReminderSentAt = null;
        if (req.Assignee is not null) task.Assignee = req.Assignee;
        // Type=None — убрать повторение (сентинел), null — не менять
        if (req.Recurrence is not null)
        {
            task.Recurrence = req.Recurrence.Type == TaskRecurrenceType.None ? null : req.Recurrence;
            if (task.Recurrence is not null) task.SeriesId ??= task.Id;
        }
        if (req.LinkedSessionId is not null)
            task.LinkedSessionId = req.LinkedSessionId == "" ? null : req.LinkedSessionId;
        // Персона-исполнитель: null = не менять, "" = убрать (как у строковых полей)
        if (req.PersonaId is not null)
            task.PersonaId = req.PersonaId == "" ? null : req.PersonaId;
        // Время жизни чата исполнения: null = не менять, отрицательное = бессрочно, N>=0 = TTL
        if (req.ExecutionExpiresAfterMinutes is not null)
            task.ExecutionExpiresAfterMinutes = req.ExecutionExpiresAfterMinutes < 0 ? null : req.ExecutionExpiresAfterMinutes;
        // Уровень модели: null = не менять, "" (и мусор — его отсекает контроллер) = сбросить
        if (req.ModelTier is not null)
            task.ModelTier = ModelTiers.TryParse(req.ModelTier, out var tier) ? tier : null;
        // Дерево чата-исполнителя: null = не менять, "" (и пробелы) = сбросить
        if (req.WorktreePath is not null) task.WorktreePath = Trimmed(req.WorktreePath);
        if (req.WorktreeBranch is not null) task.WorktreeBranch = Trimmed(req.WorktreeBranch);
        if (req.ExecutorLaunchedBySessionId is not null) task.ExecutorLaunchedBySessionId = Trimmed(req.ExecutorLaunchedBySessionId);
        if (req.ExecutorLaunchedByPersonaId is not null) task.ExecutorLaunchedByPersonaId = Trimmed(req.ExecutorLaunchedByPersonaId);
        if (req.ResultMarkdown is not null) task.ResultMarkdown = req.ResultMarkdown;
        if (req.LinkedFiles is not null) task.LinkedFiles = req.LinkedFiles;
        if (req.Labels is not null) task.Labels = req.Labels;
        if (req.Order is not null) task.Order = req.Order.Value;
        // Колонка доски: явное значение ("" = сброс на дефолт), иначе — если статус сменили
        // НЕ через доску (columnId не прислали), колонка устаревает → сбрасываем на дефолт категории
        if (req.ColumnId is not null) task.ColumnId = req.ColumnId == "" ? null : req.ColumnId;
        else if (req.Status is not null) task.ColumnId = null;
        if (req.Subtasks is not null)
            task.Subtasks = req.Subtasks.Select(s => new TaskSubtask
            {
                Id = string.IsNullOrEmpty(s.Id) ? Guid.NewGuid().ToString() : s.Id,
                Title = s.Title,
                IsDone = s.IsDone,
            }).ToList();
        // Дефект: вид карточки, шаги воспроизведения и вердикт проверки — null = не менять,
        // объект = задать целиком (частичного обновления Repro/Verification нет: клиент
        // шлёт все поля разом, как и Labels/Subtasks). Kind: смена запрещена выше, поэтому
        // здесь req.Kind либо null, либо равен task.Kind.
        if (req.Repro is not null) task.Repro = req.Repro;
        if (req.Verification is not null) task.Verification = req.Verification;
        if (req.Outcome is not null) task.Outcome = req.Outcome;
        task.Acceptance = effAcceptance;
        task.Evidence = effEvidence;
        // Фича (режим «Исполнение фичи»): null = не менять, "" = сбросить (строки);
        // ReviewOf — null = не менять, список = задать целиком; AwaitingHuman* — null = не менять
        var roleBefore = task.Role;
        if (req.FeatureId is not null) task.FeatureId = Trimmed(req.FeatureId);
        if (req.Role is not null) task.Role = Trimmed(req.Role);
        if (req.StepRef is not null) task.StepRef = Trimmed(req.StepRef);
        if (req.Lane is not null) task.Lane = Trimmed(req.Lane);
        if (req.ReviewOf is not null) task.ReviewOf = req.ReviewOf;
        // effPlanSteps уже нормализован (копия + трим) — храним его, не пере-нормализуем;
        // пустой список у не-плана нормализуем в null (инвариант «PlanSteps только у role=plan»).
        if (req.PlanSteps is not null) task.PlanSteps = NormalizePlanStepsForRole(effPlanSteps, effRole);
        // Тупик роли (major 3): перевод плана на не-план очищает список шагов — иначе план с
        // planSteps нельзя было бы перевести в step/rework (валидатор: «PlanSteps только у плана»).
        // Снятие списка явным [] при сохранении роли plan — выше: остаётся пустым списком (не null).
        if (roleBefore == TaskRoles.Plan && effRole != TaskRoles.Plan)
            task.PlanSteps = null;
        if (req.AwaitingHumanSince is not null) task.AwaitingHumanSince = req.AwaitingHumanSince;
        if (req.AwaitingHumanReason is not null) task.AwaitingHumanReason = Trimmed(req.AwaitingHumanReason);

        // Завершение/переоткрытие: фиксируем дату+время перехода в Done.
        // Первый переход в Done → CompletedAt = сейчас; уход из Done → сброс.
        if (task.Status == TaskItemStatus.Done && !wasDone)
        {
            task.CompletedAt = DateTime.UtcNow;
            // Переход в Done — ожидание человека снято (waiting_cleared в журнале):
            // закрытая задача больше не «ждёт вас». Через публичный ClearAwaitingHuman
            // (боевой вызов, не мёртвый метод); before выравниваем, чтобы финальный
            // WriteJournalTransitions не дублировал waiting_cleared.
            ClearAwaitingHuman(id);
            before.AwaitingHumanSince = null;
            before.AwaitingHumanReason = null;
            // W4-HOLD-B2: закрытие эпика снимает удержание — закрытую фичу нельзя держать
            // на паузе (tasks_complete/PUT без отдельной ручки; hold-ручки сюда не доходят:
            // merged/closed → 409). Поле гасится здесь же — в той же функции перехода в Done.
            if (task.Role == TaskRoles.Feature) task.Hold = null;
        }
        else if (task.Status != TaskItemStatus.Done) task.CompletedAt = null;
        // Вердикт и исход дефекта — устаревшая проверка после переоткрытия (образец — CompletedAt)
        if (task.Status != TaskItemStatus.Done)
        {
            task.Verification = null;
            task.Outcome = null;
        }

        // Смена проекта задачи (после блока columnId — колонки старого проекта
        // в новом невалидны, сбрасываем). "" = личная, guid = проект.
        if (req.ProjectId is not null)
        {
            var newPid = req.ProjectId == "" ? null : req.ProjectId;
            if (task.ProjectId != newPid)
            {
                task.ProjectId = newPid;
                task.ColumnId = null;
            }
        }

        // Инвариант «персона ⇒ Claude» — после того как учли и Assignee, и PersonaId
        NormalizePersonaAssignee(task);
        // Инвариант «worktree только у проектной задачи» — после смены проекта выше
        NormalizeWorktree(task);
        task.UpdatedAt = DateTime.UtcNow;
        // Журнал фичи: переходы 6 полей (сравниваем ДО/ПОСЛЕ, не на тике SaveNow — см. Б8b).
        WriteJournalTransitions(task, before);
        ScheduleSave();
        // Завершение задачи фиксируем в логе (переход в Done — заметное командное событие)
        // и поднимаем сигнал D для join-а с сигналом R (конец хода) — ровно один раз на переход
        if (task.Status == TaskItemStatus.Done && statusBefore != TaskItemStatus.Done)
        {
            LogTask(task, ProjectEventTypes.TaskCompleted, $"Завершена задача «{task.Title}»");
            TaskCompleted?.Invoke(task);
        }
        return task;
    }

    // Следующий экземпляр регулярной задачи после завершения текущего.
    // Подзадачи копируются со сброшенными галочками; напоминание и правило переносятся.
    // null — серия закончена (Until), нет срока/правила.
    public TaskItem? SpawnNextOccurrence(TaskItem completed)
    {
        if (completed.Recurrence is null || completed.DueDate is null) return null;
        var nextDate = TaskRecurrenceCalculator.NextDueDate(completed.DueDate, completed.Recurrence);
        if (nextDate is null) return null;

        var task = new TaskItem
        {
            ProjectId = completed.ProjectId,
            OwnerId = completed.OwnerId,
            Title = completed.Title,
            Description = completed.Description,
            Priority = completed.Priority,
            DueDate = nextDate,
            DueTime = completed.DueTime,
            ReminderMinutes = completed.ReminderMinutes,
            Assignee = completed.Assignee,
            // Исполнитель-персона переносится в следующий экземпляр — иначе регулярная
            // задача теряла бы персону и падала на обычного Claude (ClaudeStartedAt/
            // ClaudeResult/LinkedSessionId у нового экземпляра дефолтные → отработает заново)
            PersonaId = completed.PersonaId,
            // Постановщик — устойчивый атрибут серии (как PersonaId): без переноса со 2-го
            // экземпляра терялось бы уведомление постановщика. SourceSessionId НЕ переносим —
            // конкретная сессия (как LinkedSessionId) у нового экземпляра начинается заново
            CreatedByPersonaId = completed.CreatedByPersonaId,
            // Глубина делегирования — устойчивый атрибут серии (как CreatedByPersonaId):
            // без переноса регулярная делегированная задача «сбрасывала» бы гард на 2-м экземпляре
            DelegationDepth = completed.DelegationDepth,
            Recurrence = completed.Recurrence,
            SeriesId = completed.SeriesId ?? completed.Id,
            LinkedFiles = [.. completed.LinkedFiles],
            Subtasks = completed.Subtasks.Select(s => new TaskSubtask { Title = s.Title }).ToList(),
            Labels = [.. completed.Labels],
            Order = NextOrder(completed.OwnerId ?? ""),
            // Вид карточки и шаги воспроизведения — устойчивый атрибут серии (как Title):
            // регулярный дефект остаётся дефектом. Verification/Outcome НЕ переносим —
            // это разовый вердикт закрытого экземпляра, новый начинается заново
            Kind = completed.Kind,
            Repro = completed.Repro,
        };
        _tasks[task.Id] = task;
        // Журнал фичи: новый экземпляр — свежая задача (before = дефолт); переходы пишутся,
        // если экземплеру перенесена фича (сегодня FeatureId не копируется → no-op).
        WriteJournalTransitions(task, new TaskTransition { Status = TaskItemStatus.Todo });
        ScheduleSave();
        LogTask(task, ProjectEventTypes.TaskSpawned, $"Создан следующий экземпляр: «{task.Title}»");
        // proactive-уведомление через единый NotificationService (②-2.1) — персистится в центре уведомлений
        if (!string.IsNullOrEmpty(task.PersonaId) && !string.IsNullOrEmpty(task.OwnerId))
        {
            var label = _personas?.GetByIdInternal(task.PersonaId) is { } p
                ? PersonaLabel.Of(p) : null;
            _ = _notif?.SendExecutionEventAsync(task.OwnerId, "",
                label is not null ? $"{label} подготовил следующую задачу" : "Создан следующий экземпляр задачи",
                task.Title, task.ProjectId, task.Id,
                "task_spawned", "Спавн регулярной", TaskUrl.Of(task));
        }
        return task;
    }

    // Отметка планировщика об отправленном напоминании (идемпотентность между тиками и рестартами)
    public TaskItem? MarkReminderSent(string id, DateTime atUtc)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null) return null;
        var before = Capture(task);
        task.ReminderSentAt = atUtc;
        WriteJournalTransitions(task, before);
        ScheduleSave();
        return task;
    }

    // Запуск Claude-исполнителя: связка с сессией + перевод в работу
    public TaskItem? MarkClaudeStarted(string id, string sessionId, DateTime atUtc)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null) return null;
        var before = Capture(task);
        task.LinkedSessionId = sessionId;
        task.ClaudeStartedAt = atUtc;
        task.ClaudeResult = null;
        // Пометка «исполнитель встал» снимается ЛЮБЫМ повторным запуском: человек починил
        // ключ и перезапустил — задача не должна остаться «остановленной» навсегда.
        // Если причина не ушла, следующий ход поставит пометку заново.
        task.ExecutorStoppedAt = null;
        task.ExecutorStopReason = null;
        // Отметки страховки «задача осталась в работе» — той же природы: новая попытка
        // получает и своё напоминание, и своё уведомление человеку (по одному на запуск).
        task.ExecutorNudgedAt = null;
        task.ExecutorStaleAlertedAt = null;
        // Запуск состоялся — ожидание устройства закончено
        task.DeviceWaitSince = null;
        task.DeviceWaitReason = null;
        // Исполнитель снова запущен — ожидание человека снято (waiting_cleared в журнале):
        // человек починил/перезапустил, задача больше не «ждёт вас». Через публичный
        // ClearAwaitingHuman (боевой вызов, не мёртвый метод); before выравниваем, чтобы
        // финальный WriteJournalTransitions не дублировал waiting_cleared.
        ClearAwaitingHuman(id);
        before.AwaitingHumanSince = null;
        before.AwaitingHumanReason = null;
        if (task.Status == TaskItemStatus.Todo) task.Status = TaskItemStatus.InProgress;
        task.UpdatedAt = DateTime.UtcNow;
        // Журнал фичи: started (ClaudeStartedAt) + stop_cleared (ExecutorStoppedAt снят)
        // + waiting_cleared (AwaitingHumanSince снят) + status.
        WriteJournalTransitions(task, before);
        ScheduleSave();
        return task;
    }

    // Исполнитель ждёт устройство локального проекта (ADR-016, план §5): момент постановки в
    // ожидание фиксируется ПЕРВЫМ разом — от него считается потолок, повторные попытки его не
    // сдвигают; причина обновляется (офлайн → «обновите агента»). Статус не трогаем: задача
    // остаётся в todo, запуска не было.
    // Запустивший исполнителя (адресат доклада о завершении); пустые значения сбрасывают поля
    public TaskItem? SetExecutorLauncher(string id, string? sessionId, string? personaId)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null) return null;
        var before = Capture(task);
        task.ExecutorLaunchedBySessionId = Trimmed(sessionId ?? "");
        task.ExecutorLaunchedByPersonaId = Trimmed(personaId ?? "");
        WriteJournalTransitions(task, before);
        ScheduleSave();
        return task;
    }

    public TaskItem? MarkDeviceWait(string id, DateTime atUtc, string? reason)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null) return null;
        var before = Capture(task);
        task.DeviceWaitSince ??= atUtc;
        task.DeviceWaitReason = reason;
        task.UpdatedAt = DateTime.UtcNow;
        WriteJournalTransitions(task, before);
        ScheduleSave();
        return task;
    }

    // Ожидание устройства снято без запуска (истёк потолок)
    public TaskItem? ClearDeviceWait(string id)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null) return null;
        var before = Capture(task);
        task.DeviceWaitSince = null;
        task.DeviceWaitReason = null;
        task.UpdatedAt = DateTime.UtcNow;
        WriteJournalTransitions(task, before);
        ScheduleSave();
        return task;
    }

    // Все задачи, ждущие устройство (для DeviceOnlineDispatcher: запуск и потолок ожидания)
    public IReadOnlyCollection<TaskItem> GetDeviceWaiting() =>
        _tasks.Values.Where(t => t.DeviceWaitSince is not null && t.Status != TaskItemStatus.Done).ToList();

    // Итог хода Claude-исполнителя (success/error)
    public TaskItem? MarkClaudeResult(string id, string result)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null) return null;
        var before = Capture(task);
        task.ClaudeResult = result;
        task.UpdatedAt = DateTime.UtcNow;
        WriteJournalTransitions(task, before);
        ScheduleSave();
        return task;
    }

    // Исполнитель встал насовсем (терминальный отказ хода — см. ExecutorStopClassifier):
    // статус НЕ трогаем (доска, фильтры, MCP и напоминания держатся за todo/inProgress/done),
    // пометка живёт отдельными полями и снимается перезапуском (MarkClaudeStarted).
    //
    // Только ТЕРМИНАЛЬНЫЕ причины: пометка означает «работа не идёт, задача ждёт человека»,
    // и восстановимой остановке (обрыв сабагента на середине — его добивают продолжением)
    // здесь места нет. Гард, а не соглашение: пометка видна в UI и гасит задачу для человека.
    public TaskItem? MarkExecutorStopped(string id, DateTime atUtc, string reason)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null || !ExecutorStopClassifier.IsTerminal(reason)) return task;
        var before = Capture(task);
        task.ExecutorStoppedAt = atUtc;
        task.ExecutorStopReason = reason;
        task.UpdatedAt = DateTime.UtcNow;
        WriteJournalTransitions(task, before);
        ScheduleSave();
        return task;
    }

    // Снять пометку остановки, только если она стоит ИМЕННО с этой причиной (продолжение
    // чата после «Стоп» снимает interrupted_by_user, но не терминальный отказ вроде 401).
    // null — пометки с такой причиной нет, снимать нечего.
    public TaskItem? ClearExecutorStopped(string id, string reason)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null || task.ExecutorStoppedAt is null || task.ExecutorStopReason != reason) return null;
        var before = Capture(task);
        task.ExecutorStoppedAt = null;
        task.ExecutorStopReason = null;
        task.UpdatedAt = DateTime.UtcNow;
        WriteJournalTransitions(task, before);
        ScheduleSave();
        return task;
    }

    // Снятие ожидания человека (аналог ClearExecutorStopped): единственная точка клира
    // AwaitingHumanSince/Reason. В Update null = «без изменения», явного клира там нет;
    // боевые вызовы — старт исполнителя (MarkClaudeStarted) и переход в Done (Update),
    // плюс внешний продюсер «ответ человека», когда появится. Журнал: waiting_cleared
    // (момент мутации, не значение снятого поля).
    public TaskItem? ClearAwaitingHuman(string id)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null || task.AwaitingHumanSince is null) return null;
        var before = Capture(task);
        task.AwaitingHumanSince = null;
        task.AwaitingHumanReason = null;
        task.UpdatedAt = DateTime.UtcNow;
        WriteJournalTransitions(task, before);
        ScheduleSave();
        return task;
    }

    // Удержание фичи (W4-HOLD-B2, контракт §1): ставят/снимают только ручки человека
    // (сервисный токен отказывает 403 в контроллере; 409 по состояниям — тоже там). Только
    // эпик (role=feature): у дочерних задачи Hold нет. В журнал фичи — ровно одна запись
    // действия: held с reason / hold_cleared с снятым reason (по ней таймлайн считает
    // интервал паузы). Прерывание идущих ходов (W4-HOLD-D): SetHold помечает исполнителей
    // в состоянии running пометкой feature_held — статус не трогает, журнал задачи пишет
    // существующий WriteJournalTransitions («stopped» с причиной feature_held).
    public TaskItem? SetHold(string id, string reason)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null || task.Role != TaskRoles.Feature) return null;
        var now = DateTime.UtcNow;
        // Идемпотентность повторной паузы (W4-HOLD-D2, R-HOLE-D находка 3): тот же reason —
        // без второй записи и без сдвига Since (контракт §1, :212: одна запись на паузу).
        // Другой reason — новая пауза: запись и Since обновляются. Под guard'ом только
        // запись Hold и журнал; цикл пометки — вне guard'а (R-HOLE-D находка 1):
        // повторная пауза с тем же reason обязана пересканировать ходы, начатые после первой.
        if (task.Hold is null || task.Hold.Reason != reason)
        {
            task.Hold = new FeatureHold(reason, now, null, true);
            task.UpdatedAt = now;
            AppendJournal(task.OwnerId ?? "", task.Id, new FeatureJournalEntry
            {
                At = now,
                Kind = "held",
                TaskId = task.Id,
                Reason = reason,
            });
        }
        // W4-HOLD-D (разрез §1): пауза отнимает идущие ходы. «Running» — «ход идёт сейчас»,
        // тот же предикат, что у статуса исполнителя в проекторе (W4-HOLD-D2): «запускался
        // когда-то» не годится — ход, закончившийся результатом, прерывать нечего. Эпик в
        // выборку не входит (FeatureId == null): ключевой чат оркестратора общий для нескольких
        // фич и паузой не прерывается. Уже остановленные по другой причине не трогается —
        // возвращать её пауза не должна. Уже помеченные feature_held пропускаем: их стоп
        // записан этой же паузой, двигать ExecutorStoppedAt нельзя — по нему проектор
        // scoped'ит interrupted на интервал текущей паузы.
        foreach (var t in _tasks.Values)
        {
            if (t.FeatureId != task.Id) continue;
            if (t.ExecutorStopReason == ExecutorStopClassifier.FeatureHeldReason) continue;
            if (!FeatureProjector.IsTurnRunning(t)) continue;
            MarkExecutorStopped(t.Id, now, ExecutorStopClassifier.FeatureHeldReason);
        }
        ScheduleSave();
        return task;
    }

    // Снятие паузы (W4-HOLD-D): только снимает состояние Hold — пометку feature_held НЕ
    // гасит: возврат прерванных шагов в работу (продолжение в тех же сессиях) — отдельный
    // шаг D с проверкой живости чата. Без него снятие паузы оставило бы задачи «остановленными».
    public TaskItem? ClearHold(string id)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null || task.Role != TaskRoles.Feature || task.Hold is null) return null;
        var reason = task.Hold.Reason;
        task.Hold = null;
        task.UpdatedAt = DateTime.UtcNow;
        AppendJournal(task.OwnerId ?? "", task.Id, new FeatureJournalEntry
        {
            At = DateTime.UtcNow,
            Kind = "hold_cleared",
            TaskId = task.Id,
            Reason = reason,
        });
        ScheduleSave();
        return task;
    }

    // FeatureHoldGate (W4-HOLD-G, контракт §1 :170): единственный гейт паузы на сервере — отказ
    // запуска исполнителя по задачам фичи с активным Hold. Формула одна и совпадает с «паузой»
    // проектора (FeatureProjector :1546): любой Hold кроме draft — пауза, закрытая и открытая
    // одинаково. Задача без фичи проходит всегда; висячий FeatureId (эпик не найден) — тоже,
    // как задача без фичи: это отдельный дефект трекера, блокировать из-за него работу не надо.
    // Возвращает null — запуск разрешён; иначе текст отказа для точки запуска (контракт :175).
    public string? FeatureHoldRefusal(TaskItem task)
    {
        if (string.IsNullOrEmpty(task.FeatureId)) return null;
        var epic = _tasks.GetValueOrDefault(task.FeatureId);
        if (epic is null || epic.Role != TaskRoles.Feature) return null;
        if (epic.Hold is null) return null;
        // draft — фичу готовят: пропускать только роли подготовки (cut/design/plan/consult),
        // рабочие (step/rework/review/qa/final/merge) — нет.
        if (epic.Hold.Reason == FeatureHoldReasons.Draft)
            return task.Role is TaskRoles.Cut or TaskRoles.Design or TaskRoles.Plan or TaskRoles.Consult
                ? null
                : "Фича — черновик, запустите её на странице фичи.";
        return "Фича на паузе, снимите паузу на странице фичи.";
    }

    // Пометка снятия человеком по карточке блокера (волна 1 team-blocker-honest, дефект
    // f3965801): ставится штабом в DropSubtaskAsync после Update(Status=Done). С этого
    // момента TaskManager.Update не меняет Status/Outcome, а ResultMarkdown исполнителя
    // дописывается отдельной строкой — иначе поздний tasks_complete затирал бы пометку
    // снятия штатным отчётом (находка живой приёмки). UpdatedAt двигаем, чтобы пометка
    // всплыла наверх списка и человек не потерял факт.
    public TaskItem? MarkDroppedByHuman(string id, DateTime atUtc)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null) return task;
        var before = Capture(task);
        task.DroppedByHumanAt = atUtc;
        task.UpdatedAt = DateTime.UtcNow;
        WriteJournalTransitions(task, before);
        ScheduleSave();
        return task;
    }

    // Страховка «ход кончился, а задача не закрыта»: отметка отправленного напоминания
    // исполнителю (nudge) либо уведомления человеку (alert). Как у MarkReminderSent, UpdatedAt
    // НЕ двигаем: тишину чата-исполнителя страховка считает в том числе от UpdatedAt задачи —
    // сдвиг им же оттягивал бы собственный порог, и второй шаг никогда бы не наступил.
    public TaskItem? MarkExecutorNudged(string id, DateTime atUtc)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null) return null;
        var before = Capture(task);
        task.ExecutorNudgedAt = atUtc;
        WriteJournalTransitions(task, before);
        ScheduleSave();
        return task;
    }

    public TaskItem? MarkExecutorStaleAlerted(string id, DateTime atUtc)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null) return null;
        var before = Capture(task);
        task.ExecutorStaleAlertedAt = atUtc;
        WriteJournalTransitions(task, before);
        ScheduleSave();
        return task;
    }

    // CAS: атомарно проверяет и занимает флаг доставки завершения — join двух независимых
    // сигналов (R/D) в TaskExecutionService.TryDeliverCompletionAsync может прийти из двух
    // потоков почти одновременно (SignalR-колбэк хода vs HTTP PUT tasks_complete), но
    // доставить доклад нужно ровно один раз. true — вызывающий занял флаг первым (доставляет),
    // false — уже занят (пропуск). Save — ВНЕ лока: не завязываемся на реентрантность
    // System.Threading.Lock (Save берёт тот же _saveLock).
    public bool TryMarkCompletionDelivered(string id)
    {
        bool claimed;
        var before = default(TaskTransition);
        TaskItem? task = null;
        lock (_saveLock)
        {
            task = _tasks.GetValueOrDefault(id);
            if (task is null || task.CompletionDelivered) return false;
            before = Capture(task);
            task.CompletionDelivered = true;
            claimed = true;
        }
        if (task is not null) WriteJournalTransitions(task, before);
        ScheduleSave();
        return claimed;
    }

    public bool Delete(string id)
    {
        if (!_tasks.TryRemove(id, out var task)) return false;
        // Каскад: эпик фичи, удалённый по id, уносит файл её журнала.
        DeleteJournalFor(task);
        ScheduleSave();
        LogTask(task, ProjectEventTypes.TaskDeleted, $"Удалена задача «{task.Title}»");
        return true;
    }

    // Зачистка при удалении проекта
    public IReadOnlyCollection<string> DeleteByProject(string projectId)
    {
        var removed = _tasks.Values.Where(t => t.ProjectId == projectId).ToList();
        foreach (var t in removed)
        {
            _tasks.TryRemove(t.Id, out _);
            // Каскад: эпик фичи, удалённый вместе с проектом, уносит файл её журнала.
            DeleteJournalFor(t);
        }
        if (removed.Count > 0) ScheduleSave();
        return removed.Select(t => t.Id).ToList();
    }

    private void Load()
    {
        // Опции чтения НЕ трогаем и энкодер сюда не тянем: он влияет только на запись.
        // Файл, написанный прежним форматом (с отступами и \uXXXX), — валидный JSON, и
        // этот же парсер читает его без единой правки. Миграции формата нет и не нужно:
        // первая же запись перепишет файл компактно.
        var list = JsonFileStore.Load<List<TaskItem>>(_storePath,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (list is null) return;
        foreach (var t in list)
            _tasks[t.Id] = t;
        MigrateOrders();
    }

    // Одноразовая миграция Order: задачам с Order == 0 (созданным до появления доски)
    // присваиваем возрастающие значения (шаг 1000) в текущем порядке сортировки —
    // чтобы на доске не было «смешанных нулей». Выполняется на владельца.
    private void MigrateOrders()
    {
        var changed = false;
        foreach (var group in _tasks.Values.GroupBy(t => t.OwnerId))
        {
            var unset = group.Where(t => t.Order == 0)
                .OrderBy(t => t.DueDate ?? "9999").ThenBy(t => t.CreatedAt).ToList();
            if (unset.Count == 0) continue;
            var baseOrder = group.Select(t => t.Order).Where(o => o != 0).DefaultIfEmpty(0).Max();
            // Снимки 6 полей ДО мутации — для журнала фичи (Order не отслеживается → no-op).
            var befores = unset.Select(Capture).ToList();
            for (var i = 0; i < unset.Count; i++)
            {
                unset[i].Order = baseOrder + (i + 1) * 1000;
                WriteJournalTransitions(unset[i], befores[i]);
            }
            changed = true;
        }
        if (changed) ScheduleSave();
    }

    // Пометить стор изменённым. Файл пишется не сразу: изменения копятся, а запись
    // случается не чаще раза в _saveDebounce (см. DefaultSaveDebounce). Пачка правок
    // одной задачи подряд схлопывается в одну пересериализацию файла целиком.
    // При _saveDebounce <= 0 (Tasks:SaveDebounceMs=0) пишем синхронно, как раньше.
    private void ScheduleSave()
    {
        bool writeNow;
        lock (_dirtyLock)
        {
            _dirty = true;
            // После Dispose таймера, который донесёт правку до диска, уже нет — пишем сами.
            writeNow = _saveTimer is null || _disposed;
            if (!writeNow)
            {
                // Таймер уже взведён — эта правка уедет в тот же сброс. Перевзводить нельзя:
                // непрерывный поток правок откладывал бы запись бесконечно.
                if (_timerArmed) return;
                _timerArmed = true;
                _saveTimer!.Change(_saveDebounce, Timeout.InfiniteTimeSpan);
            }
        }
        if (writeNow) Flush();
    }

    private void OnSaveTimer()
    {
        lock (_dirtyLock) _timerArmed = false;
        // Исключение из колбэка таймера некому поймать — оно уронит процесс. Стор не
        // настолько важен: логируем и ждём следующей правки (она взведёт таймер заново).
        try { Flush(); }
        catch (Exception ex) { Console.Error.WriteLine($"[TaskManager] не удалось сохранить {_storePath}: {ex.Message}"); }
    }

    /// <summary>
    /// Записать накопленные изменения немедленно. Идемпотентна: без несохранённых
    /// изменений ничего не делает. Нужна там, где состояние на диске обязано быть
    /// актуальным прямо сейчас: остановка приложения (<see cref="Dispose"/> и хук
    /// ApplicationStopping в Program.cs) и тесты, которые читают файл сразу после операции.
    /// </summary>
    public void Flush()
    {
        lock (_saveLock)
        {
            // Флаг гасим ДО снимка: правка, приехавшая в этот зазор, в худшем случае
            // получит лишнюю запись следом — но не потеряется.
            lock (_dirtyLock)
            {
                if (!_dirty) return;
                _dirty = false;
            }
            SaveNow();
        }
    }

    // Вызывать только под _saveLock.
    private void SaveNow() => JsonFileStore.Save(_storePath, _tasks.Values.ToList(), SaveOptions);

    // Гасим таймер и досбрасываем несохранённое. Зовётся контейнером DI при остановке
    // хоста; двойной вызов безопасен (форвардер ITaskStatusReader зарегистрирован
    // фабрикой, и контейнер отслеживает тот же экземпляр вторично).
    public void Dispose()
    {
        lock (_dirtyLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _saveTimer?.Dispose();
        Flush();
        GC.SuppressFinalize(this);
    }

    // Дописывание позднего доклада исполнителя к пометке снятия человеком (волна 1
    // team-blocker-honest, дефект f3965801): после снятия Status/Outcome заблокированы,
    // но ResultMarkdown остался открытым — гонка неустранима, и без доклада исполнителя
    // его работа пропадёт. Поэтому доклад дописывается отдельным разделом к существующему
    // тексту штаба (или подменяет его, если штаб ещё ничего не записал). Помечаем
    // раздел явно — иначе теряется граница между «снято человеком» и «итог хода».
    private static string? AppendExecutorNote(string? existing, string? incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming)) return existing;
        if (string.IsNullOrWhiteSpace(existing)) return incoming;
        return existing + "\n\n— Доклад исполнителя после снятия —\n" + incoming;
    }
}

public record CreateTaskRequest(
    string Title,
    // Клиентский id для офлайн-создания (идемпотентный replay). null/пусто → сервер генерит Guid.
    string? Id = null,
    string? Description = null,
    TaskItemStatus? Status = null,
    string? ColumnId = null,
    TaskItemPriority? Priority = null,
    string? DueDate = null,
    string? DueTime = null,
    int? ReminderMinutes = null,
    TaskItemAssignee? Assignee = null,
    TaskRecurrence? Recurrence = null,
    string? LinkedSessionId = null,
    // Исполнение от лица персоны (assignee=Claude); null/пусто — обычный Claude
    string? PersonaId = null,
    // Markdown-итог выполнения (null = не менять, "" = очистить)
    string? ResultMarkdown = null,
    List<string>? LinkedFiles = null,
    List<CreateSubtaskRequest>? Subtasks = null,
    List<string>? Labels = null,
    string? SourceNoteId = null,
    int? SourceNoteLine = null,
    // Время жизни чата исполнения (мин); не передано — дефолт 1440 (сутки),
    // отрицательное — бессрочно, N>=0 — TTL. Имеет смысл только при исполнителе Claude.
    int? ExecutionExpiresAfterMinutes = null,
    // Уровень модели исполнителя: "strong|medium|weak"; не передано/пусто — не задан
    string? ModelTier = null,
    // Происхождение: персона-постановщик и чат-источник (проставляет tasks-server из env
    // хода; UI/API их не шлют — null). См. TaskItem.CreatedByPersonaId/SourceSessionId.
    string? CreatedByPersonaId = null,
    string? SourceSessionId = null,
    // Дерево для чата-исполнителя (см. TaskItem.WorktreePath): путь СУЩЕСТВУЮЩЕГО worktree
    // проекта и его ветка. У личной задачи игнорируются.
    string? WorktreePath = null,
    string? WorktreeBranch = null,
    // Вид карточки: обычная задача или дефект (правила DefectRules); не передано — Task.
    TaskKind? Kind = null,
    // Шаги воспроизведения дефекта (DefectRules.EnsureReproOnReview требует Repro.Steps
    // при попадании в review-колонку); у обычной задачи игнорируется.
    DefectRepro? Repro = null,
    // Адресат доклада от родительской задачи (см. TaskItem.ReportToSessionId); UI/REST не шлют
    string? ReportToSessionId = null,
    // ─── Фича (режим «Исполнение фичи») ───
    // Id задачи-эпика фичи; null — задача вне фичи. Валидация: эпик существует,
    // тот же OwnerId и Role=feature (TaskManager.ValidateFeatureFields).
    string? FeatureId = null,
    // Роль задачи в фиче (TaskRoles): feature · cut · plan · step · rework · review · qa · final · merge · consult
    string? Role = null,
    // Код шага из плана («B1", «F2"); у step/rework, по нему доработка склеивается со своим шагом
    string? StepRef = null,
    // Линия («бэк", «фронт"); у step/rework, подпись берётся как есть
    string? Lane = null,
    // Id задач, которые оценивает это ревью/проверка/финал; null — не задано
    List<string>? ReviewOf = null,
    // Структурный список шагов плана (только у role=plan); null — не задано
    List<PlannedStep>? PlanSteps = null,
    // «Ждёт вас»: с какого момента и почему (merge, decision); у эпика
    DateTime? AwaitingHumanSince = null,
    string? AwaitingHumanReason = null,
    // Вердикт (TaskVerification.Verdict): accepted · accepted_with_remarks · returned · withdrawn;
    // null = старая семантика «проверено»
    TaskVerification? Verification = null,
    // Критерии приёмки (измеримые); пустой Id получает «cN». См. AcceptanceRules
    List<AcceptanceCriterion>? Acceptance = null);

public record CreateSubtaskRequest(string Title);

public record UpdateTaskRequest(
    string? Title = null,
    string? Description = null,
    TaskItemStatus? Status = null,
    TaskItemPriority? Priority = null,
    string? DueDate = null,
    string? DueTime = null,
    // null = не менять, отрицательное = убрать напоминание
    int? ReminderMinutes = null,
    TaskItemAssignee? Assignee = null,
    // null = не менять, Type=None = убрать повторение
    TaskRecurrence? Recurrence = null,
    string? LinkedSessionId = null,
    // Персона-исполнитель: null = не менять, "" = убрать
    string? PersonaId = null,
    // Markdown-итог выполнения: null = не менять, "" = очистить
    string? ResultMarkdown = null,
    List<string>? LinkedFiles = null,
    List<UpdateSubtaskRequest>? Subtasks = null,
    List<string>? Labels = null,
    // Порядок карточки на доске (drag внутри/между колонок); null = не менять
    double? Order = null,
    // Колонка доски проекта; null = не менять, "" = сброс на дефолт категории
    string? ColumnId = null,
    // Смена проекта: null = не менять, "" = сделать личной (ProjectId=null), guid = привязать
    string? ProjectId = null,
    // Время жизни чата исполнения: null = не менять, отрицательное = бессрочно, N>=0 = TTL
    int? ExecutionExpiresAfterMinutes = null,
    // Уровень модели исполнителя: null = не менять, "" = сбросить, "strong|medium|weak" = задать
    string? ModelTier = null,
    // Дерево чата-исполнителя (см. TaskItem.WorktreePath): null = не менять, "" = сбросить
    string? WorktreePath = null,
    string? WorktreeBranch = null,
    // Вид карточки: null = не менять, значение = сменить (dефект ⇄ задача)
    TaskKind? Kind = null,
    // Шаги воспроизведения дефекта: null = не менять, объект = задать целиком
    // (частичного обновления нет — как у Labels/Subtasks)
    DefectRepro? Repro = null,
    // Вердикт проверки: null = не менять. Клиент шлёт только Notes — VerifiedAt/PersonaId
    // подставляет контроллер из сессии вызова (X-Caller-Session-Id) перед вызовом TaskManager
    TaskVerification? Verification = null,
    // Исход дефекта: null = не менять. Единственное значение этой волны — ClosedWithoutCheck
    // (внутренние пути закрытия без отдельной проверки)
    DefectOutcome? Outcome = null,
    // Запустивший исполнителя (адресат доклада): null = не менять, "" = сбросить
    string? ExecutorLaunchedBySessionId = null,
    string? ExecutorLaunchedByPersonaId = null,
    // ─── Фича (режим «Исполнение фичи») ─── null = не менять, "" = сбросить (строки)
    // Id задачи-эпика фичи; валидация — как в Create (TaskManager.ValidateFeatureFields)
    string? FeatureId = null,
    // Роль задачи в фиче (TaskRoles); null = не менять, "" = сбросить
    string? Role = null,
    // Код шага из плана; null = не менять, "" = сбросить
    string? StepRef = null,
    // Линия; null = не менять, "" = сбросить
    string? Lane = null,
    // Id задач, которые оценивает это ревью/проверка/финал; null = не менять, список = задать целиком
    List<string>? ReviewOf = null,
    // Структурный список шагов плана (только у role=plan); null = не менять, список = задать целиком
    List<PlannedStep>? PlanSteps = null,
    // «Ждёт вас»: null = не менять, значение = задать
    DateTime? AwaitingHumanSince = null,
    string? AwaitingHumanReason = null,
    // Критерии приёмки: null = не менять, список = задать целиком (пустой — снять все).
    // Исполнитель-чат своей задачи критерии не меняет (TaskManager.Update, callerIsExecutor)
    List<AcceptanceCriterion>? Acceptance = null,
    // Свидетельства: null = не менять, список = добавить/заменить по criterionId
    List<AcceptanceEvidence>? Evidence = null);

public record UpdateSubtaskRequest(string Id, string Title, bool IsDone);
