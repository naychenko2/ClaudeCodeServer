using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Задачи владельца (tasks_*) поверх HTTP-транспорта — третий переехавший с node сервер
/// (ADR-012, фаза 2 волна 2). Раньше это был mcp/tasks-server: тонкий JSON-RPC-фасад, который
/// ходил в наш же бэкенд сервисным JWT. Здесь фасад повёрнут напрямую к сервисам (TaskManager,
/// TaskExecutionService, TaskAiService) — HTTP-хоп через собственный Kestrel не нужен.
///
/// Маршрут — <c>POST /mcp/tasks/{sessionId}</c>: хвост несёт СЕССИЮ-ВЫЗЫВАТЕЛЬ, по которой
/// тулсет резолвит проект чата, персону и её кросс-проектные привязки (то, что на stdio ехало
/// env TASKS_PROJECT_ID/TASKS_SELF_PERSONA_ID/TASKS_EXTRA_*). Параметры в ПУТИ, а не в теле:
/// конфиг хода — наш код, тело контролирует модель. Сессия из хвоста обязана принадлежать
/// владельцу токена (GetOwned) — чужая это отказ, а не пустой список.
///
/// ИНВАРИАНТ состава (IMcpToolset): tools/list зависит только от сессии-вызывателя (проект
/// чата, персона, её привязки) — всё это свойства СЕССИИ, от свойств хода состав не зависит.
/// tasks_run_executor подключён всегда; анти-рекурсия (запуск исполнителя с делегированного
/// хода и с реакционного авто-хода постановщика) проверяется на вызов — DelegatedTurnGate,
/// тем же гейтом, что и [DenyOnDelegatedTurn] на TasksController.Execute (MVC-атрибут на
/// McpTransportController не применяется: тулсет зовёт сервисы через DI, минуя конвейер
/// фильтров). Гейт идёт по сессии из ХВОСТА — она уже изолирована по владельцу токена,
/// тогда как заголовок X-Caller-Session-Id клиент мог бы и не прислать.
///
/// Сторож парности со stdio-веткой отката — TasksToolsetParityTests (index.js заморожен).
/// </summary>
public sealed class TasksToolset(
    TaskManager tasks,
    ProjectManager projects,
    PersonaManager personas,
    TaskExecutionService executor,
    TaskAiService ai,
    NoteTaskSyncService noteSync,
    PersonaBindingsService bindings,
    SessionManager sessions,
    IHubContext<SessionHub> hub) : IMcpParameterizedToolset
{
    // Имя сервера = первый сегмент маршрута POST /mcp/tasks/{sessionId}. Константа —
    // единственная точка правды для URL конфига хода (ClaudeSession)
    public const string ServerName = "tasks";

    // Дефолтные колонки доски: у проекта без кастомных и у личных задач. Category — enum,
    // глобальный camelCase-конвертер отдаёт его как "todo"/"inProgress"/"done" (как stdio)
    private static readonly (string Id, string Name, TaskItemStatus Category)[] DefaultColumns =
    [
        ("todo", "К выполнению", TaskItemStatus.Todo),
        ("inProgress", "В работе", TaskItemStatus.InProgress),
        ("done", "Готово", TaskItemStatus.Done),
    ];

    // Ответы — как у stdio-ветки (JSON.stringify): camelCase, кириллица без \u-экранирования
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Name => ServerName;
    public string Version => "1.0.0";

    // У параметризованного тулсета состава без хвоста не существует: контроллер на
    // /mcp/tasks без хвоста отвечает 404 до диспетчера
    public IReadOnlyList<McpToolSchema> Tools => [];

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) =>
        TryResolve(context, out var session, out _, out _)
            ? ToolsFor(session.ProjectId is not null)
            : [];

    // Состав фиксированный (13 инструментов всегда, включая tasks_run_executor — инвариант
    // стабильности состава), различаются только описания контекста: проект чата или личные дела
    private static IReadOnlyList<McpToolSchema> ToolsFor(bool inProject) => inProject ? ProjectChatTools : PersonalTools;

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        // Хвост не разобрался или указывает на чужую/чужого-владельца сессию — отказ
        // текстом, а не пустой список: право на сессию проверяется на КАЖДЫЙ вызов
        if (!TryResolve(context, out var session, out var persona, out var routeError))
            return Deny(routeError);

        // Живой контекст вместо env stdio-ветки: проект чата, персона-постановщик и её
        // кросс-проектные привязки считаются по САМОЙ сессии на каждый вызов — смена
        // спикера/привязок подхватывается без пересоздания адаптера
        var projectId = session.ProjectId;
        var selfPersonaId = session.PersonaId;
        var extraScopes = bindings.BuildExternalTaskScopes(context.OwnerId, persona);
        var allowed = AllowedProjectIds(projectId, extraScopes);
        var extraReadOnly = extraScopes.Where(s => s.ReadOnly).Select(s => s.ProjectId)
            .ToHashSet(StringComparer.Ordinal);

        switch (tool)
        {
            case "tasks_list_projects":
            {
                // Как stdio-ветка: текущий проект + привязки; недоступные — молча пропуск
                var entries = new List<(string Id, bool ReadOnly, bool Current)>();
                if (projectId is not null) entries.Add((projectId, false, true));
                foreach (var scope in extraScopes.Where(s => s.ProjectId != projectId))
                    entries.Add((scope.ProjectId, scope.ReadOnly, false));
                var result = new List<object>();
                foreach (var e in entries)
                {
                    var proj = projects.GetById(e.Id);
                    if (proj is null || proj.OwnerId != context.OwnerId) continue;
                    result.Add(new { id = e.Id, name = proj.Name, readOnly = e.ReadOnly, current = e.Current });
                }
                return Json(result);
            }

            case "tasks_list":
            {
                var all = tasks.GetByOwner(context.OwnerId).AsEnumerable();
                // Фильтры — повтор REST GetAll (строковое сравнение корректно для ISO-дат)
                all = ApplyFilters(all, arguments);
                // projectId явно указан — точечный запрос по одному (доступному) проекту
                var explicitProject = StringArg(arguments, "projectId");
                if (explicitProject.Length > 0)
                {
                    var denied = ReadDenied(projectId, allowed, explicitProject);
                    if (denied is not null) return Deny(denied);
                    return Json(all.Where(t => t.ProjectId == explicitProject).Select(Brief).ToList());
                }
                if (StringArg(arguments, "scope") == "all")
                    return Json(all.Where(t => InScope(projectId, allowed, t)).Select(Brief).ToList());
                // scope=context (дефолт): проект чата, либо личные задачи владельца
                var data = projectId is not null
                    ? all.Where(t => t.ProjectId == projectId)
                    : all.Where(t => t.ProjectId is null);
                return Json(data.Select(Brief).ToList());
            }

            case "tasks_search":
            {
                var q = StringArg(arguments, "query");
                var all = tasks.GetByOwner(context.OwnerId).AsEnumerable();
                if (q.Length > 0)
                    all = all.Where(t =>
                        t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || t.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || t.Labels.Any(l => l.Contains(q, StringComparison.OrdinalIgnoreCase)));
                return Json(all.Where(t => InScope(projectId, allowed, t)).Select(Brief).ToList());
            }

            case "tasks_get":
            {
                if (GetAccessible(context.OwnerId, projectId, allowed, StringArg(arguments, "id")) is not { } task)
                    return Deny($"Задача {StringArg(arguments, "id")} не найдена или недоступна в этом контексте.");
                return Json(task);
            }

            case "tasks_board_columns":
            {
                if (projectId is null)
                    return Json(new
                    {
                        note = "Личные задачи используют дефолтные колонки.",
                        columns = DefaultColumns.Select(c => new { id = c.Id, name = c.Name, category = c.Category }),
                    });
                var proj = projects.GetById(projectId);
                // Category — enum: глобальный camelCase-конвертер отдаёт "inProgress" как у stdio-ветки
                var cols = proj?.BoardColumns is { Count: > 0 } custom
                    ? custom.Select(c => (Id: c.Id, Name: c.Name, Category: c.Category)).ToList()
                    : [.. DefaultColumns];
                return Json(cols.Select(c => new { id = c.Id, name = c.Name, category = c.Category }));
            }

            case "tasks_create":
            {
                var title = StringArg(arguments, "title");
                if (string.IsNullOrWhiteSpace(title)) return Deny("Название задачи не может быть пустым");
                var modelTier = StringArg(arguments, "modelTier");
                if (modelTier.Length > 0 && !ModelTiers.IsValidWireValue(modelTier))
                    return Deny(ModelTiers.WireError);

                // Целевой проект: по умолчанию текущий; явный projectId, отличный от
                // текущего, требует полной (не readonly) привязки
                var targetProjectId = OptionalArg(arguments, "projectId") ?? projectId;
                if (targetProjectId is not null && targetProjectId != projectId)
                {
                    var denied = WriteDenied(extraReadOnly, targetProjectId);
                    if (denied is not null) return Deny(denied);
                }

                var project = targetProjectId is null
                    ? null
                    : projects.GetById(targetProjectId) is { OwnerId: var owner } p && owner == context.OwnerId ? p : null;
                if (targetProjectId is not null && project is null)
                    return Deny("Проект не найден или недоступен");

                var columnId = StringArg(arguments, "columnId");
                var cat = BoardColumnHelper.Category(project, columnId);
                var personaId = StringArg(arguments, "personaId");
                if (personaId.Length > 0
                    && TaskPersonaValidator.Error(personas, context.OwnerId, personaId, targetProjectId, extraScopes) is { } personaError)
                    return Deny(personaError);
                if (selfPersonaId is not null
                    && TaskPersonaValidator.Error(personas, context.OwnerId, selfPersonaId, targetProjectId, extraScopes) is { } creatorError)
                    return Deny(creatorError);

                var req = new CreateTaskRequest(
                    Title: title,
                    Description: OptionalArg(arguments, "description"),
                    ColumnId: columnId.Length > 0 ? columnId : null,
                    Status: cat,
                    Priority: ParseEnum<TaskItemPriority>(arguments, "priority"),
                    DueDate: OptionalArg(arguments, "dueDate"),
                    DueTime: OptionalArg(arguments, "dueTime"),
                    ReminderMinutes: NullableIntArg(arguments, "reminderMinutes"),
                    Assignee: ParseEnum<TaskItemAssignee>(arguments, "assignee"),
                    Recurrence: RecurrenceArg(arguments),
                    PersonaId: personaId.Length > 0 ? personaId : null,
                    Subtasks: SubtasksArg(arguments),
                    Labels: LabelsArg(arguments),
                    ExecutionExpiresAfterMinutes: NullableIntArg(arguments, "executionExpiresAfterMinutes"),
                    ModelTier: modelTier.Length > 0 ? modelTier : null,
                    // Происхождение задачи из окружения чата: персона-постановщик и чат-источник
                    CreatedByPersonaId: selfPersonaId,
                    SourceSessionId: session.Id,
                    WorktreePath: OptionalArg(arguments, "worktreePath"),
                    WorktreeBranch: OptionalArg(arguments, "worktreeBranch"));
                var created = tasks.Create(targetProjectId, context.OwnerId, req);
                await hub.BroadcastTaskChangedAsync(context.OwnerId, "created", created);
                return Json(created);
            }

            case "tasks_update":
            {
                var id = StringArg(arguments, "id");
                if (tasks.GetById(id) is not { OwnerId: var taskOwner } existing || taskOwner != context.OwnerId)
                    return Deny($"Задача {id} не найдена.");
                var denied = TaskDenied(projectId, allowed, existing);
                if (denied is not null) return Deny(denied);

                // Целевой проект для валидации колонки/персоны: текущий, либо новый из projectId
                string? targetProjectId = existing.ProjectId;
                var newProjectId = arguments.ContainsKey("projectId") ? StringArg(arguments, "projectId") : null;
                if (newProjectId is not null)
                {
                    targetProjectId = newProjectId.Length == 0 ? null : newProjectId;
                    if (targetProjectId is not null
                        && projects.GetById(targetProjectId)?.OwnerId != context.OwnerId)
                        return Deny("Проект не найден или недоступен");
                    if (targetProjectId is not null && targetProjectId != projectId)
                    {
                        var writeDenied = WriteDenied(extraReadOnly, targetProjectId);
                        if (writeDenied is not null) return Deny(writeDenied);
                    }
                }
                var modelTier = StringArg(arguments, "modelTier");
                if (modelTier.Length > 0 && !ModelTiers.IsValidWireValue(modelTier))
                    return Deny(ModelTiers.WireError);

                var columnId = arguments.ContainsKey("columnId") ? StringArg(arguments, "columnId") : null;
                var cat = columnId is null
                    ? null
                    : BoardColumnHelper.Category(
                        targetProjectId is null ? null : projects.GetById(targetProjectId), columnId);
                var personaId = StringArg(arguments, "personaId");
                if (personaId.Length > 0
                    && TaskPersonaValidator.Error(personas, context.OwnerId, personaId, targetProjectId, extraScopes) is { } personaError)
                    return Deny(personaError);

                var wasDone = existing.Status == TaskItemStatus.Done;
                var req = new UpdateTaskRequest(
                    Title: OptionalArg(arguments, "title"),
                    Description: OptionalArg(arguments, "description"),
                    Status: cat ?? ParseEnum<TaskItemStatus>(arguments, "status"),
                    Priority: ParseEnum<TaskItemPriority>(arguments, "priority"),
                    DueDate: arguments.ContainsKey("dueDate") ? StringArg(arguments, "dueDate") : null,
                    DueTime: arguments.ContainsKey("dueTime") ? StringArg(arguments, "dueTime") : null,
                    ReminderMinutes: NullableIntArg(arguments, "reminderMinutes"),
                    Assignee: ParseEnum<TaskItemAssignee>(arguments, "assignee"),
                    Recurrence: arguments.ContainsKey("recurrence") ? RecurrenceArg(arguments) : null,
                    PersonaId: arguments.ContainsKey("personaId") ? personaId : null,
                    ResultMarkdown: arguments.ContainsKey("resultMarkdown") ? StringArg(arguments, "resultMarkdown") : null,
                    LinkedFiles: arguments.ContainsKey("linkedFiles") ? LabelsArg(arguments, "linkedFiles") : null,
                    Subtasks: null, // подзадачи — отдельными инструментами (add/toggle), как у stdio
                    Labels: arguments.ContainsKey("labels") ? LabelsArg(arguments) : null,
                    ColumnId: columnId,
                    ProjectId: newProjectId,
                    ExecutionExpiresAfterMinutes: NullableIntArg(arguments, "executionExpiresAfterMinutes"),
                    ModelTier: arguments.ContainsKey("modelTier") ? modelTier : null,
                    WorktreePath: arguments.ContainsKey("worktreePath") ? StringArg(arguments, "worktreePath") : null,
                    WorktreeBranch: arguments.ContainsKey("worktreeBranch") ? StringArg(arguments, "worktreeBranch") : null);
                var updated = tasks.Update(id, req);
                if (updated is null) return Deny($"Задача {id} не найдена.");
                await hub.BroadcastTaskChangedAsync(context.OwnerId, "updated", updated);

                // Завершение регулярной задачи → следующий экземпляр серии (тот же путь, что REST)
                if (!wasDone && updated.Status == TaskItemStatus.Done && updated.Recurrence is not null)
                {
                    var next = tasks.SpawnNextOccurrence(updated);
                    if (next is not null)
                        await hub.BroadcastTaskChangedAsync(context.OwnerId, "created", next);
                }
                // Обратная запись в заметку-источник: смена done-состояния ставит/снимает галочку
                if (wasDone != (updated.Status == TaskItemStatus.Done))
                    await noteSync.SyncTaskToNoteAsync(context.OwnerId, updated);
                return Json(updated);
            }

            case "tasks_complete":
            {
                var id = StringArg(arguments, "id");
                if (GetAccessible(context.OwnerId, projectId, allowed, id) is not { } task)
                    return Deny($"Задача {id} не найдена или недоступна в этом контексте.");
                var wasDone = task.Status == TaskItemStatus.Done;
                var updated = tasks.Update(id, new UpdateTaskRequest(
                    Status: TaskItemStatus.Done,
                    ResultMarkdown: arguments.ContainsKey("resultMarkdown") ? StringArg(arguments, "resultMarkdown") : null,
                    LinkedFiles: arguments.ContainsKey("linkedFiles") ? LabelsArg(arguments, "linkedFiles") : null));
                if (updated is null) return Deny($"Задача {id} не найдена.");
                await hub.BroadcastTaskChangedAsync(context.OwnerId, "updated", updated);
                if (!wasDone && updated.Recurrence is not null
                    && tasks.SpawnNextOccurrence(updated) is { } next)
                    await hub.BroadcastTaskChangedAsync(context.OwnerId, "created", next);
                if (wasDone != (updated.Status == TaskItemStatus.Done))
                    await noteSync.SyncTaskToNoteAsync(context.OwnerId, updated);
                return Json(updated);
            }

            case "tasks_run_executor":
            {
                // Анти-рекурсия — тот же гейт, что [DenyOnDelegatedTurn] на TasksController.
                // Execute: fail-closed без вызывателя, запрет на делегированном и реакционном
                // ходу, квоты team-implement/work-loop вместо запрета. Сессия — из хвоста
                // (уже изолирована по владельцу), заголовку от клиента доверять нельзя
                var gate = DelegatedTurnGate.Decide(
                    sessions, context.OwnerId, session.Id,
                    "Запуск задачи на исполнение",
                    alsoWhenExecutorSuppressed: true,
                    allowInTeamImplement: true, allowInWorkLoop: true,
                    failOpenWhenUnknown: false);
                if (!gate.Allowed) return Deny(gate.DenyText!);

                var taskId = StringArg(arguments, "taskId");
                if (tasks.GetById(taskId) is not { OwnerId: var owner } task || owner != context.OwnerId)
                {
                    DelegatedTurnGate.Refund(sessions, session.Id, context.OwnerId, gate);
                    return Deny($"Задача {taskId} не найдена.");
                }
                try
                {
                    var executed = await executor.ExecuteAsync(task, auto: false);
                    return Json(new
                    {
                        id = executed.Id,
                        title = executed.Title,
                        status = executed.Status,
                        executorSessionId = executed.LinkedSessionId,
                        note = "Исполнитель запущен и работает в фоне — прогресс виден в связанной сессии и статусе задачи.",
                    });
                }
                catch (InvalidOperationException ex)
                {
                    // 400-семантика контроллера: запуск не состоялся — квоту вернуть
                    DelegatedTurnGate.Refund(sessions, session.Id, context.OwnerId, gate);
                    return Deny(ex.Message);
                }
                catch
                {
                    DelegatedTurnGate.Refund(sessions, session.Id, context.OwnerId, gate);
                    throw;
                }
            }

            case "tasks_delete":
            {
                var id = StringArg(arguments, "id");
                if (GetAccessible(context.OwnerId, projectId, allowed, id) is not { } task)
                    return Deny($"Задача {id} не найдена или недоступна в этом контексте.");
                tasks.Delete(id);
                await hub.BroadcastTaskChangedAsync(context.OwnerId, "deleted", task);
                return Text($"Задача {id} удалена.");
            }

            case "tasks_add_subtask":
            {
                // Подзадачи обновляются списком целиком: читаем, добавляем, сохраняем
                var taskId = StringArg(arguments, "taskId");
                if (GetAccessible(context.OwnerId, projectId, allowed, taskId) is not { } task)
                    return Deny($"Задача {taskId} не найдена или недоступна в этом контексте.");
                var subtasks = task.Subtasks
                    .Select(s => new UpdateSubtaskRequest(s.Id, s.Title, s.IsDone)).ToList();
                subtasks.Add(new UpdateSubtaskRequest("", StringArg(arguments, "title"), false));
                var updated = tasks.Update(taskId, new UpdateTaskRequest(Subtasks: subtasks));
                if (updated is null) return Deny($"Задача {taskId} не найдена.");
                await hub.BroadcastTaskChangedAsync(context.OwnerId, "updated", updated);
                return Json(updated);
            }

            case "tasks_toggle_subtask":
            {
                var taskId = StringArg(arguments, "taskId");
                if (GetAccessible(context.OwnerId, projectId, allowed, taskId) is not { } task)
                    return Deny($"Задача {taskId} не найдена или недоступна в этом контексте.");
                var subtaskId = StringArg(arguments, "subtaskId");
                var subtaskTitle = StringArg(arguments, "subtaskTitle");
                var isDone = arguments["isDone"] is JsonValue b && b.TryGetValue<bool>(out var v) && v;
                bool Match(TaskSubtask s) =>
                    (subtaskId.Length > 0 && s.Id == subtaskId)
                    || (subtaskTitle.Length > 0 && s.Title == subtaskTitle);
                if (!task.Subtasks.Any(Match))
                    return Deny("Подзадача не найдена — проверьте subtaskId/subtaskTitle через tasks_get");
                var subtasks = task.Subtasks
                    .Select(s => Match(s) ? new UpdateSubtaskRequest(s.Id, s.Title, isDone) : new UpdateSubtaskRequest(s.Id, s.Title, s.IsDone))
                    .ToList();
                var updated = tasks.Update(taskId, new UpdateTaskRequest(Subtasks: subtasks));
                if (updated is null) return Deny($"Задача {taskId} не найдена.");
                await hub.BroadcastTaskChangedAsync(context.OwnerId, "updated", updated);
                return Json(updated);
            }

            case "tasks_find_duplicate":
            {
                var title = StringArg(arguments, "title");
                if (string.IsNullOrWhiteSpace(title)) return Deny("Нужно название задачи");
                var projectIdForAi = projectId is not null
                    && projects.GetById(projectId)?.OwnerId == context.OwnerId ? projectId : null;
                // Ключевые слова заголовка (≥4 букв) для дешёвого предотбора кандидатов —
                // тот же предфильтр, что REST-эндпоинт
                var words = System.Text.RegularExpressions.Regex
                    .Matches(title.ToLowerInvariant(), @"\p{L}{4,}")
                    .Select(m => m.Value).ToHashSet();
                var candidates = tasks.GetByOwner(context.OwnerId)
                    .Where(t => t.ProjectId == projectIdForAi && !string.IsNullOrWhiteSpace(t.Title))
                    .Where(t => words.Count == 0 || words.Any(w => t.Title.ToLowerInvariant().Contains(w)))
                    .Take(20).Select(t => (t.Id, t.Title)).ToList();
                try
                {
                    var r = await ai.FindDuplicateAsync(context.OwnerId, title.Trim(),
                        OptionalArg(arguments, "description"), candidates, ct);
                    return Json(new { duplicateId = r.Id, reason = r.Reason });
                }
                catch (InvalidOperationException ex) { return Deny(ex.Message); }
            }

            default:
                throw new ArgumentException($"Неизвестный инструмент: {tool}", nameof(tool));
        }
    }

    // --- Маршрут: /mcp/tasks/{sessionId} ---

    /// <summary>Хвост маршрута для конфига хода: единая точка с TryParseRoute.</summary>
    internal static string RouteTail(string sessionId) => sessionId;

    /// <summary>URL эндпоинта в конфиге хода: базовый адрес + маршрут тулсета с хвостом.</summary>
    public static string EndpointFor(string apiUrl, string sessionId) =>
        McpHttpTransport.EndpointFor(apiUrl, ServerName) + "/" + RouteTail(sessionId);

    // Один сегмент — id сессии; форма как у resumeSessionId-белого списка (хвост строим мы,
    // но проверяем форму всё равно — он приезжает из URL)
    private static bool TryParseRoute(string? route, out string sessionId)
    {
        sessionId = "";
        if (route is null || route.Split('/').Length != 1) return false;
        if (route.Length is < 1 or > 128 || !route.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return false;
        sessionId = route;
        return true;
    }

    /// <summary>
    /// Резолв хвоста в сессию ВЛАДЕЛЬЦА токена + её живые права. Чужая или несуществующая
    /// сессия — отказ; персона чата без права на tasks-сервер (живая формула
    /// SessionManager.TasksMcpEnabled) — отказ: права проверяются на каждый вызов, а не
    /// только при построении контекста адаптера.
    /// </summary>
    private bool TryResolve(McpToolCallContext context,
        out Session session, out Persona? persona, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        session = null!;
        persona = null;
        if (!TryParseRoute(context.RouteTail, out var sessionId))
        {
            error = "Некорректный маршрут сервера задач — вызов отклонён.";
            return false;
        }
        var owned = sessions.GetOwned(sessionId, context.OwnerId);
        if (owned is null)
        {
            error = "Чат-вызыватель не найден или принадлежит другому владельцу — доступ к задачам закрыт.";
            return false;
        }
        session = owned;
        persona = session.PersonaId is { } pid ? personas.Get(pid, context.OwnerId) : null;
        if (!sessions.TasksMcpEnabled(context.OwnerId, session, persona))
        {
            error = "Сервер задач недоступен этой персоне (привязка tool:tasks выключена). "
                + "Попроси пользователя включить её.";
            return false;
        }
        error = null;
        return true;
    }

    // --- Скоупы проектов (живой эквивалент TASKS_EXTRA_* stdio-ветки) ---

    private static IReadOnlyCollection<string> AllowedProjectIds(string? projectId,
        IReadOnlyList<(string ProjectId, bool ReadOnly)> extraScopes)
    {
        var set = extraScopes.Select(s => s.ProjectId).ToHashSet(StringComparer.Ordinal);
        if (projectId is not null) set.Add(projectId);
        return set;
    }

    // Проект доступен для ЧТЕНИЯ (текущий или любая привязка, включая readonly). Личный
    // контекст (проекта нет) — сужения нет вовсе: владение перепроверяет каждый вызов
    private static string? ReadDenied(string? projectId, IReadOnlyCollection<string> allowed, string target)
    {
        if (projectId is null || target == projectId) return null;
        return allowed.Contains(target)
            ? null
            : $"Нет доступа к проекту {target} — нужна привязка ProjectTasks персоне (см. tasks_list_projects).";
    }

    // Проект доступен для ЗАПИСИ (текущий или полная — не readonly — привязка)
    private static string? WriteDenied(IReadOnlySet<string> extraReadOnly, string target) =>
        extraReadOnly.Contains(target)
            ? $"Доступ к проекту {target} только для чтения (привязка ProjectTasks с readonly) — создавать/менять задачи нельзя."
            : null;

    // Задача видна в контексте: без проекта чата — весь воркспейс владельца; личные задачи
    // (без проекта) видны из любого чата; проектные — только из доступных проектов
    private static bool InScope(string? projectId, IReadOnlyCollection<string> allowed, TaskItem t)
    {
        if (projectId is null) return true;
        if (t.ProjectId is null) return true;
        return allowed.Contains(t.ProjectId);
    }

    // Задача по id: владение + видимость в контексте (закрывает доступ к чужим проектам по id)
    private TaskItem? GetAccessible(string ownerId, string? projectId, IReadOnlyCollection<string> allowed, string id)
    {
        var task = tasks.GetById(id);
        if (task is null || task.OwnerId != ownerId) return null;
        if (projectId is not null && task.ProjectId is not null && !allowed.Contains(task.ProjectId)) return null;
        return task;
    }

    private static string? TaskDenied(string? projectId, IReadOnlyCollection<string> allowed, TaskItem task)
    {
        if (projectId is null || task.ProjectId is null) return null;
        return allowed.Contains(task.ProjectId)
            ? null
            : $"Задача {task.Id} недоступна в этом контексте — она принадлежит другому проекту.";
    }

    private static IEnumerable<TaskItem> ApplyFilters(IEnumerable<TaskItem> source, JsonObject arguments)
    {
        var from = StringArg(arguments, "from");
        if (from.Length > 0)
            source = source.Where(t => t.DueDate is not null && string.Compare(t.DueDate, from, StringComparison.Ordinal) >= 0);
        var to = StringArg(arguments, "to");
        if (to.Length > 0)
            source = source.Where(t => t.DueDate is not null && string.Compare(t.DueDate, to, StringComparison.Ordinal) <= 0);
        if (ParseEnum<TaskItemStatus>(arguments, "status") is { } s)
            source = source.Where(t => t.Status == s);
        if (ParseEnum<TaskItemPriority>(arguments, "priority") is { } p)
            source = source.Where(t => t.Priority == p);
        if (ParseEnum<TaskItemAssignee>(arguments, "assignee") is { } a)
            source = source.Where(t => t.Assignee == a);
        return source;
    }

    // Компактное представление задачи для списков — как у stdio-ветки
    private static object Brief(TaskItem t) => new
    {
        t.Id, t.Title, t.Status, t.Priority,
        t.DueDate, t.DueTime, t.ReminderMinutes,
        // Правило повторения показываем, только если оно активно
        recurrence = t.Recurrence is { Type: not TaskRecurrenceType.None } r ? r : null,
        t.Assignee,
        t.PersonaId, t.ProjectId, t.ColumnId, t.CompletedAt,
        t.Labels,
        subtasks = $"{t.Subtasks.Count(s => s.IsDone)}/{t.Subtasks.Count}",
    };

    // --- Ответы и аргументы ---

    private static McpToolCallResult Text(string text) => new(text);

    private static McpToolCallResult Json<T>(T value) =>
        new(JsonSerializer.Serialize(value, JsonOpts));

    private static McpToolCallResult Deny(string text) => new(text, IsError: true);

    private static string StringArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static string? OptionalArg(JsonObject arguments, string name)
    {
        var value = StringArg(arguments, name);
        return value.Length == 0 ? null : value;
    }

    private static T? ParseEnum<T>(JsonObject arguments, string name) where T : struct, Enum
    {
        var raw = StringArg(arguments, name);
        return raw.Length > 0 && Enum.TryParse<T>(raw, true, out var parsed) ? parsed : null;
    }

    private static int? NullableIntArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static List<string>? LabelsArg(JsonObject arguments, string name = "labels") =>
        arguments[name] is JsonArray arr && arr.Count > 0
            ? arr.Where(t => t is JsonValue v && v.TryGetValue<string>(out _))
                .Select(t => t!.GetValue<string>()).ToList()
            : null;

    private static List<CreateSubtaskRequest>? SubtasksArg(JsonObject arguments) =>
        arguments["subtasks"] is JsonArray arr && arr.Count > 0
            ? arr.Select(t => new CreateSubtaskRequest(t?.GetValue<string>() ?? "")).ToList()
            : null;

    private static TaskRecurrence? RecurrenceArg(JsonObject arguments) =>
        arguments["recurrence"] is not JsonObject rec ? null :
        Enum.TryParse<TaskRecurrenceType>(StringArg(rec, "type"), true, out var type)
            ? new TaskRecurrence
            {
                Type = type,
                Interval = NullableIntArg(rec, "interval") ?? 1,
                Weekdays = rec["weekdays"] is JsonArray days && days.Count > 0
                    ? days.Where(d => d is JsonValue dv && dv.TryGetValue<int>(out _))
                        .Select(d => d!.GetValue<int>()).ToList()
                    : null,
                Until = OptionalArg(rec, "until"),
            }
            : null;

    // --- Схемы инструментов: копия mcp/tasks-server/index.js (источник контракта — здесь,
    // index.js заморожен; сторож парности — TasksToolsetParityTests). Описания зависят от
    // контекста чата (CONTEXT_NOTE) — как у stdio-ветки, проект чата в рамках сессии постоянен.
    // internal для сторожа: обе ветки живые (рубильник Mcp:HttpTransport), правка обязана
    // ехать парой ---

    // Lazy, а не прямые инициализаторы: общие JsonObject-поля схем объявлены ниже списков,
    // и прямой инициализатор прочитал бы их null (порядок статической инициализации)
    private static readonly System.Lazy<IReadOnlyList<McpToolSchema>> _projectChat =
        new(() => [.. Schemas(inProject: true)]);
    private static readonly System.Lazy<IReadOnlyList<McpToolSchema>> _personal =
        new(() => [.. Schemas(inProject: false)]);

    internal static IReadOnlyList<McpToolSchema> ProjectChatTools => _projectChat.Value;
    internal static IReadOnlyList<McpToolSchema> PersonalTools => _personal.Value;

    private static IEnumerable<McpToolSchema> Schemas(bool inProject)
    {
        var context = inProject
            ? "Контекст — текущий проект."
            : "Контекст — личные задачи пользователя (вне проектов).";

        yield return Tool("tasks_list_projects",
            "Проекты, чьи задачи доступны в этом ходу (текущий плюс кросс-проектные привязки): " +
            "id, имя, readOnly. По ним выбирай projectId для tasks_create/tasks_list.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });

        yield return Tool("tasks_list",
            $"Список задач. {context} scope=all — все задачи пользователя по всем проектам и личные.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["status"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "todo", "inProgress", "done" },
                        ["description"] = "Фильтр по статусу",
                    },
                    ["priority"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "urgent", "high", "medium", "low" },
                        ["description"] = "Фильтр по приоритету",
                    },
                    ["assignee"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "me", "claude" },
                        ["description"] = "Фильтр по исполнителю (me — пользователь, claude — Claude)",
                    },
                    ["from"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Срок от даты включительно, YYYY-MM-DD (задачи без срока не попадают)",
                    },
                    ["to"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Срок до даты включительно, YYYY-MM-DD (задачи без срока не попадают)",
                    },
                    ["scope"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "context", "all" },
                        ["description"] = "context (дефолт) — текущий проект/личные; all — все задачи пользователя",
                    },
                    ["projectId"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Явный проект — переопределяет scope, только его задачи",
                    },
                },
            });

        yield return Tool("tasks_search",
            "Поиск задач по названию, описанию и меткам — по всем задачам пользователя (все проекты + личные).",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "query" },
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Строка поиска" },
                },
            });

        yield return Tool("tasks_get",
            "Полная карточка задачи по id: описание (markdown), подзадачи, метки, срок.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "id" },
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "ID задачи" },
                },
            });

        yield return Tool("tasks_create",
            $"Создать задачу. {context}",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "title" },
                ["properties"] = new JsonObject
                {
                    ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Название задачи" },
                    ["description"] = new JsonObject { ["type"] = "string", ["description"] = "Описание (markdown)" },
                    ["priority"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "urgent", "high", "medium", "low" },
                        ["description"] = "Приоритет (по умолчанию medium)",
                    },
                    ["dueDate"] = new JsonObject { ["type"] = "string", ["description"] = "Срок YYYY-MM-DD" },
                    ["dueTime"] = new JsonObject { ["type"] = "string", ["description"] = "Время HH:MM" },
                    ["reminderMinutes"] = ReminderMinutesSchema(),
                    ["recurrence"] = RecurrenceSchema(),
                    ["assignee"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "me", "claude" },
                        ["description"] = "Исполнитель",
                    },
                    ["personaId"] = PersonaIdSchema(),
                    ["modelTier"] = ModelTierSchema(),
                    ["worktreePath"] = WorktreePathSchema(),
                    ["worktreeBranch"] = WorktreeBranchSchema(),
                    ["subtasks"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Названия подзадач",
                    },
                    ["labels"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Метки",
                    },
                    ["columnId"] = ColumnIdSchema(),
                    ["executionExpiresAfterMinutes"] = ExecutionTtlSchema(),
                    ["projectId"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Проект задачи, если не текущий (см. tasks_list_projects; нужен полный доступ)",
                    },
                },
            });

        yield return Tool("tasks_update",
            "Обновить поля задачи: поля как при создании, передавай только изменяемые. " +
            "Очистка значений и сброс повторения — см. инструкции сервера tasks.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "id" },
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "ID задачи" },
                    ["title"] = new JsonObject { ["type"] = "string" },
                    ["description"] = new JsonObject
                    {
                        ["type"] = "string", ["description"] = "Описание (markdown), заменяет целиком",
                    },
                    ["status"] = new JsonObject
                    {
                        ["type"] = "string", ["enum"] = new JsonArray { "todo", "inProgress", "done" },
                    },
                    ["priority"] = new JsonObject
                    {
                        ["type"] = "string", ["enum"] = new JsonArray { "urgent", "high", "medium", "low" },
                    },
                    ["dueDate"] = new JsonObject
                    {
                        ["type"] = "string", ["description"] = "YYYY-MM-DD или \"\" чтобы убрать срок",
                    },
                    ["dueTime"] = new JsonObject
                    {
                        ["type"] = "string", ["description"] = "HH:MM или \"\" чтобы убрать время",
                    },
                    ["reminderMinutes"] = ReminderMinutesSchema(),
                    ["recurrence"] = RecurrenceSchema(),
                    ["assignee"] = new JsonObject
                    {
                        ["type"] = "string", ["enum"] = new JsonArray { "me", "claude" },
                    },
                    ["personaId"] = PersonaIdSchema(),
                    ["modelTier"] = ModelTierSchema(),
                    ["worktreePath"] = WorktreePathSchema(),
                    ["worktreeBranch"] = WorktreeBranchSchema(),
                    ["resultMarkdown"] = ResultMarkdownSchema(),
                    ["linkedFiles"] = LinkedFilesSchema(),
                    ["labels"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Метки (заменяют список целиком)",
                    },
                    ["columnId"] = ColumnIdSchema(),
                    ["executionExpiresAfterMinutes"] = ExecutionTtlSchema(),
                    ["projectId"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Перенести в другой проект (см. tasks_list_projects) или \"\" — сделать личной",
                    },
                },
            });

        yield return Tool("tasks_board_columns",
            inProject
                ? "Колонки Kanban-доски текущего проекта: id, name, category (todo/inProgress/done). Нужен, чтобы задать columnId в tasks_create/tasks_update."
                : "Колонки Kanban-доски (личные задачи используют дефолтные): id, name, category (todo/inProgress/done). Нужен, чтобы задать columnId в tasks_create/tasks_update.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });

        yield return Tool("tasks_complete",
            "Пометить задачу выполненной (status → done) и сразу прикрепить итог: " +
            "resultMarkdown (что сделано) и linkedFiles (итоговые файлы). Это ТОЛЬКО смена статуса — " +
            "исполнителя запускает tasks_run_executor.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "id" },
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "ID задачи" },
                    ["resultMarkdown"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Короткий итог сделанного (markdown)",
                    },
                    ["linkedFiles"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Итоговые файлы проекта (пути от корня, через /)",
                    },
                },
            });

        yield return Tool("tasks_delete",
            "Удалить задачу безвозвратно.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "id" },
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "ID задачи" },
                },
            });

        yield return Tool("tasks_add_subtask",
            "Добавить подзадачу к задаче.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "taskId", "title" },
                ["properties"] = new JsonObject
                {
                    ["taskId"] = new JsonObject { ["type"] = "string", ["description"] = "ID родительской задачи" },
                    ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Название подзадачи" },
                },
            });

        yield return Tool("tasks_toggle_subtask",
            "Отметить подзадачу выполненной или снять отметку. Подзадачу можно указать по id или точному названию.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "taskId", "isDone" },
                ["properties"] = new JsonObject
                {
                    ["taskId"] = new JsonObject { ["type"] = "string", ["description"] = "ID задачи" },
                    ["subtaskId"] = new JsonObject { ["type"] = "string", ["description"] = "ID подзадачи" },
                    ["subtaskTitle"] = new JsonObject
                    {
                        ["type"] = "string", ["description"] = "Точное название подзадачи (если id неизвестен)",
                    },
                    ["isDone"] = new JsonObject
                    {
                        ["type"] = "boolean", ["description"] = "true — выполнена",
                    },
                },
            });

        yield return Tool("tasks_find_duplicate",
            "Проверить, дублирует ли новая задача одну из существующих задач владельца (предотбор по ключевым словам + " +
            "модель). Возвращает {duplicateId, reason} или duplicateId=null. Полезно перед tasks_create, чтобы не плодить дубли.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "title" },
                ["properties"] = new JsonObject
                {
                    ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Название новой задачи" },
                    ["description"] = new JsonObject { ["type"] = "string", ["description"] = "Описание (опционально)" },
                },
            });

        yield return Tool("tasks_run_executor",
            "Запустить Claude-исполнителя задачи: отдельная сессия в проекте задачи " +
            "(личная — чат вне проекта), работает в фоне и сама ведёт статус через tasks_*. " +
            "Возвращает задачу с id сессии-исполнителя. " +
            "НЕ отмечает задачу выполненной — для смены статуса на done используй tasks_complete.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "taskId" },
                ["properties"] = new JsonObject
                {
                    ["taskId"] = new JsonObject { ["type"] = "string", ["description"] = "ID задачи" },
                },
            });
    }

    private static JsonObject ReminderMinutesSchema() => new()
    {
        ["type"] = "integer",
        ["description"] = "Напоминание: за сколько минут до срока уведомить (0 = в момент срока). Требует dueDate.",
    };

    private static JsonObject RecurrenceSchema() => new()
    {
        ["type"] = "object",
        ["description"] = "Повторение задачи, требует dueDate (см. инструкции сервера tasks)",
        ["required"] = new JsonArray { "type" },
        ["properties"] = new JsonObject
        {
            ["type"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray { "none", "daily", "weekly", "monthly", "yearly" },
            },
            ["interval"] = new JsonObject
            {
                ["type"] = "integer", ["minimum"] = 1,
                ["description"] = "Каждые N периодов (дефолт 1)",
            },
            ["weekdays"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 7 },
                ["description"] = "Только для weekly: ISO-дни (1=Пн … 7=Вс)",
            },
            ["until"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Последняя дата серии YYYY-MM-DD; опустить — бессрочно",
            },
        },
    };

    private static JsonObject PersonaIdSchema() => new()
    {
        ["type"] = "string",
        ["description"] = "ID персоны-исполнителя (см. personas_list); assignee выставится сам, \"\" — снять",
    };

    private static JsonObject ModelTierSchema() => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray { "strong", "medium", "weak" },
        ["description"] = "Уровень модели исполнения (см. инструкции сервера tasks); сомневаешься — не указывай",
    };

    private static JsonObject WorktreePathSchema() => new()
    {
        ["type"] = "string",
        ["description"] = "Абсолютный путь СУЩЕСТВУЮЩЕГО git worktree проекта (см. инструкции сервера tasks); \"\" — убрать",
    };

    private static JsonObject WorktreeBranchSchema() => new()
    {
        ["type"] = "string",
        ["description"] = "Ветка этого worktree (метка в git-баре чата); пусто — из самого дерева",
    };

    private static JsonObject ExecutionTtlSchema() => new()
    {
        ["type"] = "integer",
        ["description"] = "TTL чата исполнения в минутах от последней активности (дефолт 1440)",
    };

    private static JsonObject ResultMarkdownSchema() => new()
    {
        ["type"] = "string",
        ["description"] = "Markdown-описание итога выполнения (заменяет целиком). \"\" — очистить.",
    };

    private static JsonObject LinkedFilesSchema() => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject { ["type"] = "string" },
        ["description"] = "Пути файлов проекта (от корня проекта, через /). Заменяют список целиком.",
    };

    private static JsonObject ColumnIdSchema() => new()
    {
        ["type"] = "string",
        ["description"] = "ID колонки доски проекта (см. tasks_board_columns); статус выставится по её категории",
    };

    private static McpToolSchema Tool(string name, string description, JsonObject schema) =>
        new(name, description, schema);
}
