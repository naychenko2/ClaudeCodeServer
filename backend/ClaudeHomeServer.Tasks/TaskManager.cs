using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using ClaudeHomeServer.Models;

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

    // Единственный путь в Done (UI/MCP/планировщик — всё через Update). Подписчик —
    // TaskExecutionService.TryDeliverCompletionAsync (join сигналов R/D, см. CompletionDelivered).
    public event Action<TaskItem>? TaskCompleted;

    public TaskManager(IConfiguration config, IProjectEventLogService? events = null,
        ITaskNotificationDispatcher? notif = null, IPersonaLookup? personas = null)
    {
        _events = events;
        _notif = notif;
        _personas = personas;
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
            DelegationDepth = ComputeDelegationDepth(req.SourceSessionId),
            Order = NextOrder(ownerId),
            // Создаём задачу сразу готовой (редко) — фиксируем момент завершения
            CompletedAt = req.Status == TaskItemStatus.Done ? DateTime.UtcNow : null,
            Kind = req.Kind ?? TaskKind.Task,
            Repro = req.Repro,
        };
        // Серия регулярной задачи начинается с её первого экземпляра
        if (task.Recurrence is not null) task.SeriesId = task.Id;
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
        _tasks[task.Id] = task;
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

    // Глубина цепочки делегирования новой задачи: если её создали из чата, который сам
    // сейчас исполняет родительскую задачу (её LinkedSessionId == sourceSessionId) —
    // глубина родителя + 1; иначе (обычный чат, UI, API) — 0.
    private int ComputeDelegationDepth(string? sourceSessionId) =>
        !string.IsNullOrEmpty(sourceSessionId) && GetBySession(sourceSessionId) is { } parent
            ? parent.DelegationDepth + 1
            : 0;

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
        bool isAgentCall = false)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null) return null;
        var statusBefore = task.Status;

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

        // Завершение/переоткрытие: фиксируем дату+время перехода в Done.
        // Первый переход в Done → CompletedAt = сейчас; уход из Done → сброс.
        if (task.Status == TaskItemStatus.Done && !wasDone) task.CompletedAt = DateTime.UtcNow;
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
        task.ReminderSentAt = atUtc;
        ScheduleSave();
        return task;
    }

    // Запуск Claude-исполнителя: связка с сессией + перевод в работу
    public TaskItem? MarkClaudeStarted(string id, string sessionId, DateTime atUtc)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null) return null;
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
        if (task.Status == TaskItemStatus.Todo) task.Status = TaskItemStatus.InProgress;
        task.UpdatedAt = DateTime.UtcNow;
        ScheduleSave();
        return task;
    }

    // Исполнитель ждёт устройство локального проекта (ADR-016, план §5): момент постановки в
    // ожидание фиксируется ПЕРВЫМ разом — от него считается потолок, повторные попытки его не
    // сдвигают; причина обновляется (офлайн → «обновите агента»). Статус не трогаем: задача
    // остаётся в todo, запуска не было.
    public TaskItem? MarkDeviceWait(string id, DateTime atUtc, string? reason)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null) return null;
        task.DeviceWaitSince ??= atUtc;
        task.DeviceWaitReason = reason;
        task.UpdatedAt = DateTime.UtcNow;
        ScheduleSave();
        return task;
    }

    // Ожидание устройства снято без запуска (истёк потолок)
    public TaskItem? ClearDeviceWait(string id)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null) return null;
        task.DeviceWaitSince = null;
        task.DeviceWaitReason = null;
        task.UpdatedAt = DateTime.UtcNow;
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
        task.ClaudeResult = result;
        task.UpdatedAt = DateTime.UtcNow;
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
        task.ExecutorStoppedAt = atUtc;
        task.ExecutorStopReason = reason;
        task.UpdatedAt = DateTime.UtcNow;
        ScheduleSave();
        return task;
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
        task.DroppedByHumanAt = atUtc;
        task.UpdatedAt = DateTime.UtcNow;
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
        task.ExecutorNudgedAt = atUtc;
        ScheduleSave();
        return task;
    }

    public TaskItem? MarkExecutorStaleAlerted(string id, DateTime atUtc)
    {
        var task = _tasks.GetValueOrDefault(id);
        if (task is null) return null;
        task.ExecutorStaleAlertedAt = atUtc;
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
        lock (_saveLock)
        {
            var task = _tasks.GetValueOrDefault(id);
            if (task is null || task.CompletionDelivered) return false;
            task.CompletionDelivered = true;
            claimed = true;
        }
        ScheduleSave();
        return claimed;
    }

    public bool Delete(string id)
    {
        if (!_tasks.TryRemove(id, out var task)) return false;
        ScheduleSave();
        LogTask(task, ProjectEventTypes.TaskDeleted, $"Удалена задача «{task.Title}»");
        return true;
    }

    // Зачистка при удалении проекта
    public IReadOnlyCollection<string> DeleteByProject(string projectId)
    {
        var ids = _tasks.Values.Where(t => t.ProjectId == projectId).Select(t => t.Id).ToList();
        foreach (var id in ids)
            _tasks.TryRemove(id, out _);
        if (ids.Count > 0) ScheduleSave();
        return ids;
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
            for (var i = 0; i < unset.Count; i++)
                unset[i].Order = baseOrder + (i + 1) * 1000;
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
    DefectRepro? Repro = null);

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
    DefectOutcome? Outcome = null);

public record UpdateSubtaskRequest(string Id, string Title, bool IsDone);
