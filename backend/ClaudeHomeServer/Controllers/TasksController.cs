using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Controllers;

// Задачи внутри проекта
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/tasks")]
public class ProjectTasksController(
    TaskManager tasks, ProjectManager projects, PersonaManager personas,
    IHubContext<SessionHub> hub, PersonaBindingsService bindings) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    private Project? OwnProject(string projectId)
    {
        var project = projects.GetById(projectId);
        return project?.OwnerId == UserId ? project : null;
    }

    [HttpGet]
    public IActionResult GetAll(string projectId)
    {
        if (OwnProject(projectId) is null) return NotFound();
        return Ok(tasks.GetByProject(projectId));
    }

    [HttpPost]
    public async Task<IActionResult> Create(string projectId, [FromBody] CreateTaskRequest req)
    {
        var project = OwnProject(projectId);
        if (project is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "Название задачи не может быть пустым" });
        if (!ModelTiers.IsValidWireValue(req.ModelTier))
            return BadRequest(new { error = ModelTiers.WireError });

        // Колонка доски → статус выводим из её категории
        var cat = BoardColumnHelper.Category(project, req.ColumnId);
        if (cat is not null) req = req with { Status = cat };
        var targetIsReview = BoardColumnHelper.IsReview(project, req.ColumnId);
        // Полный объект колонки — для гейта DefectRules.EnsureNotClosedAtCreate:
        // дефект в Todo с columnId="done" не должен пройти мимо правила только потому,
        // что клиент не привёл Status в соответствие с категорией колонки.
        var targetColumn = project?.BoardColumns?.FirstOrDefault(c => c.Id == req.ColumnId);

        // Персона-исполнитель: своя и в правильном проекте (или есть ProjectTasks-привязка)
        if (!string.IsNullOrEmpty(req.PersonaId))
        {
            var p = personas.Get(req.PersonaId, UserId);
            var scopes = p is not null ? bindings.BuildExternalTaskScopes(UserId, p) : [];
            if (TaskPersonaValidator.Error(personas, UserId, req.PersonaId, projectId, scopes) is { } personaError)
                return BadRequest(new { error = personaError });
        }

        // Персона-постановщик (происхождение): та же валидация — своя и в правильном проекте
        if (!string.IsNullOrEmpty(req.CreatedByPersonaId))
        {
            var p = personas.Get(req.CreatedByPersonaId, UserId);
            var scopes = p is not null ? bindings.BuildExternalTaskScopes(UserId, p) : [];
            if (TaskPersonaValidator.Error(personas, UserId, req.CreatedByPersonaId, projectId, scopes) is { } creatorError)
                return BadRequest(new { error = creatorError });
        }

        TaskItem task;
        try
        {
            task = tasks.Create(projectId, UserId, req, targetIsReview, targetColumn);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        await hub.BroadcastTaskChangedAsync(UserId, "created", task);
        return Ok(task);
    }
}

// Задачи пользователя без привязки к конкретному проекту: календарь + операции по id
[ApiController]
[Authorize]
[Route("api/tasks")]
public class TasksController(
    TaskManager tasks, IHubContext<SessionHub> hub, TaskAiService ai, ProjectManager projects,
    PersonaManager personas, TaskExecutionService executor, NoteTaskSyncService noteSync,
    PersonaBindingsService bindings, SessionManager sessions) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Проект генерации: только свой; чужой/несуществующий → личный контекст
    private string? OwnProjectId(string? projectId) =>
        projectId is not null && projects.GetById(projectId)?.OwnerId == UserId ? projectId : null;

    // Сгенерировать описание задачи (Claude): по названию + контекст проекта (личная — только название)
    [HttpPost("ai/description")]
    public async Task<IActionResult> GenerateDescription([FromBody] GenerateDescriptionRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "Нужно название задачи" });
        try
        {
            var description = await ai.GenerateDescriptionAsync(UserId, req.Title.Trim(), OwnProjectId(req.ProjectId), ct);
            return Ok(new { description });
        }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // Сгенерировать подзадачи (Claude) по названию и описанию
    [HttpPost("ai/subtasks")]
    public async Task<IActionResult> GenerateSubtasks([FromBody] GenerateSubtasksRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "Нужно название задачи" });
        try
        {
            var subtasks = await ai.GenerateSubtasksAsync(
                UserId, req.Title.Trim(), req.Description ?? "", OwnProjectId(req.ProjectId), ct);
            return Ok(new { subtasks });
        }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // Классификация (локальная модель): приоритет + метки по названию/описанию
    [HttpPost("ai/classify")]
    public async Task<IActionResult> Classify([FromBody] ClassifyTaskRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "Нужно название задачи" });
        var existing = tasks.GetByOwner(UserId).SelectMany(t => t.Labels)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            var r = await ai.ClassifyAsync(UserId, req.Title.Trim(), req.Description, existing, OwnProjectId(req.ProjectId), ct);
            return Ok(new { priority = r.Priority, labels = r.Labels });
        }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // Нормализация заголовка (чистка голосового ввода) → аккуратный title + подсказка срока
    [HttpPost("ai/normalize-title")]
    public async Task<IActionResult> NormalizeTitle([FromBody] NormalizeTitleRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "Нужен текст заголовка" });
        try
        {
            var r = await ai.NormalizeTitleAsync(UserId, req.Title, ct);
            return Ok(new { title = r.Title, dueHint = r.DueHint });
        }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // Поиск дубля среди существующих задач владельца (предфильтр по ключевым словам + модель)
    [HttpPost("ai/find-duplicate")]
    public async Task<IActionResult> FindDuplicate([FromBody] FindDuplicateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "Нужно название задачи" });
        var projectId = OwnProjectId(req.ProjectId);
        // Ключевые слова нового заголовка (≥4 букв) для дешёвого предотбора кандидатов
        var words = System.Text.RegularExpressions.Regex.Matches(req.Title.ToLowerInvariant(), @"\p{L}{4,}")
            .Select(m => m.Value).ToHashSet();
        var candidates = tasks.GetByOwner(UserId)
            .Where(t => t.ProjectId == projectId && !string.IsNullOrWhiteSpace(t.Title))
            .Where(t => words.Count == 0 || words.Any(w => t.Title.ToLowerInvariant().Contains(w)))
            .Take(20).Select(t => (t.Id, t.Title)).ToList();
        try
        {
            var r = await ai.FindDuplicateAsync(UserId, req.Title.Trim(), req.Description, candidates, ct);
            return Ok(new { duplicateId = r.Id, reason = r.Reason });
        }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // Личная задача — без привязки к проекту
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] JsonElement body)
    {
        // projectId в wire-формате CreateTaskRequest отсутствует, но клиент может прислать
        // его в теле — нужно для резолва кастомной колонки (гейт дефекта по категории).
        // Парсим руками, чтобы не расширять публичный record, который живёт в TaskManager.
        string? bodyProjectId = body.TryGetProperty("projectId", out var pid)
            && pid.ValueKind == JsonValueKind.String
            ? pid.GetString()
            : null;

        var req = JsonSerializer.Deserialize<CreateTaskRequest>(body.GetRawText(),
            TaskCreateJson.Options);
        if (req is null)
            return BadRequest(new { error = "Некорректное тело запроса" });

        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "Название задачи не может быть пустым" });
        if (!ModelTiers.IsValidWireValue(req.ModelTier))
            return BadRequest(new { error = ModelTiers.WireError });

        // Проект из тела (если он свой) — для резолва кастомной колонки. Чужой проект
        // игнорируем: личный эндпоинт не должен подменять статус по чужой доске.
        var project = bodyProjectId is not null && projects.GetById(bodyProjectId)?.OwnerId == UserId
            ? projects.GetById(bodyProjectId)
            : null;

        // Колонка доски → статус из категории. У личных — только дефолтные колонки,
        // а также кастомные колонки проекта из тела (если projectId свой).
        var cat = BoardColumnHelper.Category(project, req.ColumnId);
        if (cat is not null) req = req with { Status = cat };
        // Полный объект колонки — для гейта DefectRules.EnsureNotClosedAtCreate: дефект
        // в Todo с columnId="done" не должен пройти мимо правила только потому, что
        // клиент не привёл Status в соответствие с категорией колонки.
        var targetColumn = project?.BoardColumns?.FirstOrDefault(c => c.Id == req.ColumnId);

        // Персона-исполнитель: своя; проектная персона личную задачу не берёт
        if (!string.IsNullOrEmpty(req.PersonaId)
            && TaskPersonaValidator.Error(personas, UserId, req.PersonaId, taskProjectId: null) is { } personaError)
            return BadRequest(new { error = personaError });

        // Персона-постановщик (происхождение): та же валидация, что у исполнителя
        if (!string.IsNullOrEmpty(req.CreatedByPersonaId)
            && TaskPersonaValidator.Error(personas, UserId, req.CreatedByPersonaId, taskProjectId: null) is { } creatorError)
            return BadRequest(new { error = creatorError });

        TaskItem task;
        try
        {
            // Личная задача — проект не сохраняем в TaskItem, но колонку прокидываем
            // через TaskManager.Create для гейта DefectRules (review-гейт у личных
            // задач не действует — пользователь сам решает, что ему делать).
            task = tasks.Create(null, UserId, req, targetIsReview: false, targetColumn);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        await hub.BroadcastTaskChangedAsync(UserId, "created", task);
        return Ok(task);
    }

    // Все задачи пользователя (календарь, MCP): диапазон по сроку, поиск и фильтры.
    // personal=true — только личные (вне проекта); projectId — только задачи проекта;
    // personaId — только задачи, порученные конкретной персоне-исполнителю.
    [HttpGet]
    public IActionResult GetAll(
        [FromQuery] string? from = null, [FromQuery] string? to = null,
        [FromQuery] string? q = null, [FromQuery] string? status = null,
        [FromQuery] string? priority = null, [FromQuery] string? assignee = null,
        [FromQuery] string? projectId = null, [FromQuery] bool personal = false,
        [FromQuery] string? personaId = null)
    {
        var result = tasks.GetByOwner(UserId).AsEnumerable();
        // Строковое сравнение корректно для ISO-дат YYYY-MM-DD
        if (from is not null)
            result = result.Where(t => t.DueDate is not null && string.Compare(t.DueDate, from, StringComparison.Ordinal) >= 0);
        if (to is not null)
            result = result.Where(t => t.DueDate is not null && string.Compare(t.DueDate, to, StringComparison.Ordinal) <= 0);
        if (personal)
            result = result.Where(t => t.ProjectId is null);
        else if (!string.IsNullOrEmpty(projectId))
            result = result.Where(t => t.ProjectId == projectId);
        if (status is not null && Enum.TryParse<TaskItemStatus>(status, true, out var s))
            result = result.Where(t => t.Status == s);
        if (priority is not null && Enum.TryParse<TaskItemPriority>(priority, true, out var p))
            result = result.Where(t => t.Priority == p);
        if (assignee is not null && Enum.TryParse<TaskItemAssignee>(assignee, true, out var a))
            result = result.Where(t => t.Assignee == a);
        if (!string.IsNullOrEmpty(personaId))
            result = result.Where(t => t.PersonaId == personaId);
        if (!string.IsNullOrWhiteSpace(q))
            result = result.Where(t =>
                t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                t.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                t.Labels.Any(l => l.Contains(q, StringComparison.OrdinalIgnoreCase)));
        return Ok(result.ToList());
    }

    [HttpGet("{taskId}")]
    public IActionResult GetById(string taskId)
    {
        var task = tasks.GetById(taskId);
        return task is null || task.OwnerId != UserId ? NotFound() : Ok(task);
    }

    [HttpPut("{taskId}")]
    public async Task<IActionResult> Update(string taskId, [FromBody] UpdateTaskRequest req)
    {
        var task = tasks.GetById(taskId);
        if (task is null || task.OwnerId != UserId) return NotFound();
        if (!ModelTiers.IsValidWireValue(req.ModelTier))
            return BadRequest(new { error = ModelTiers.WireError });

        // Целевой проект для валидации колонки/персоны: текущий, либо новый из req.ProjectId
        // (null в req = не менять; "" = сделать личной; guid = привязать к проекту)
        string? targetProjectId = task.ProjectId;
        if (req.ProjectId is not null)
        {
            targetProjectId = req.ProjectId == "" ? null : req.ProjectId;
            if (targetProjectId is not null && projects.GetById(targetProjectId)?.OwnerId != UserId)
                return BadRequest(new { error = "Проект не найден или недоступен" });
        }

        // Колонка доски → статус выводим из её категории (единый источник для MCP/Claude и доски).
        // Категорию берём по целевому проекту — колонка актуальна для него, а не для прежнего.
        var targetProject = targetProjectId is null ? null : projects.GetById(targetProjectId);
        var cat = BoardColumnHelper.Category(targetProject, req.ColumnId);
        if (cat is not null) req = req with { Status = cat };
        // Дефект: карточка попадает в review-колонку → нужны шаги воспроизведения (гейт — TaskManager/DefectRules)
        var targetIsReview = BoardColumnHelper.IsReview(targetProject, req.ColumnId);

        // Персона-исполнитель: "" = убрать (валидировать нечего), непустая — проверяем.
        // Валидация по целевому проекту: проектная персона прежнего проекта в новом недействительна,
        // если не имеет кросс-проектной ProjectTasks-привязки с полным доступом.
        if (!string.IsNullOrEmpty(req.PersonaId))
        {
            var p = personas.Get(req.PersonaId, UserId);
            var scopes = p is not null ? bindings.BuildExternalTaskScopes(UserId, p) : [];
            if (TaskPersonaValidator.Error(personas, UserId, req.PersonaId, targetProjectId, scopes) is { } personaError)
                return BadRequest(new { error = personaError });
        }

        // Вердикт проверки дефекта: автора и время подставляет сервер из сессии вызова
        // (X-Caller-Session-Id) — клиентские значения игнорируются, это гигиена атрибуции,
        // не защита. null-сессия/персона → проверка человеком (TaskVerification.PersonaId == null)
        if (req.Verification is not null)
        {
            var callerSessionId = Request.Headers[DenyOnDelegatedTurnAttribute.CallerHeader].FirstOrDefault();
            var callerPersonaId = callerSessionId is not null ? sessions.GetById(callerSessionId)?.PersonaId : null;
            req = req with
            {
                Verification = new TaskVerification
                {
                    Notes = req.Verification.Notes,
                    VerifiedAt = DateTime.UtcNow,
                    PersonaId = callerPersonaId,
                },
            };
        }

        var wasDone = task.Status == TaskItemStatus.Done;
        TaskItem updated;
        try
        {
            updated = tasks.Update(taskId, req, targetIsReview)
                ?? throw new InvalidOperationException("Задача не найдена");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        await hub.BroadcastTaskChangedAsync(UserId, "updated", updated);

        // Завершение экземпляра регулярной задачи → следующий экземпляр серии.
        // Покрывает и UI, и MCP (tasks_complete/tasks_update идут через этот PUT)
        if (!wasDone && updated.Status == TaskItemStatus.Done && updated.Recurrence is not null)
        {
            var next = tasks.SpawnNextOccurrence(updated);
            if (next is not null)
                await hub.BroadcastTaskChangedAsync(UserId, "created", next);
        }

        // Обратная запись в заметку-источник: смена done-состояния ставит/снимает галочку
        // (флаг notes-task-sync; no-op если задача не из заметки)
        if (wasDone != (updated.Status == TaskItemStatus.Done))
            await noteSync.SyncTaskToNoteAsync(UserId, updated);

        return Ok(updated);
    }

    // Запустить выполнение задачи Claude-ом (кнопка «Выполнить с Claude»)
    [HttpPost("{taskId}/execute")]
    // Анти-рекурсия: раньше жила в составе инструментов (env TASKS_EXECUTE), из-за чего
    // чередование обычного и делегированного хода перезапускало процесс CLI со всеми MCP.
    // AllowInTeamImplement: у чата-штаба «Командной реализации» запрет заменён квотой —
    // автономный цикл волн иначе невозможен, а лавину держит бюджет итерации (Э4).
    // AllowInWorkLoop: тот же паттерн в обычном чате с включённым циклом «до готово» —
    // запрет хода доклада заменён квотой запусков, иначе агент в цикле не запустит
    // собственные задачи (лавину держит лимит Loop:MaxTaskExecutions).
    [DenyOnDelegatedTurn("Запуск задачи на исполнение",
        AlsoWhenExecutorSuppressed = true, AllowInTeamImplement = true, AllowInWorkLoop = true)]
    public async Task<IActionResult> Execute(string taskId)
    {
        var task = tasks.GetById(taskId);
        if (task is null || task.OwnerId != UserId) return NotFound();

        try
        {
            return Ok(await executor.ExecuteAsync(task, auto: false));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{taskId}")]
    public async Task<IActionResult> Delete(string taskId)
    {
        var task = tasks.GetById(taskId);
        if (task is null || task.OwnerId != UserId) return NotFound();

        tasks.Delete(taskId);
        await hub.BroadcastTaskChangedAsync(UserId, "deleted", task);
        return NoContent();
    }
}

public record GenerateDescriptionRequest(string Title, string? ProjectId = null);
public record GenerateSubtasksRequest(string Title, string? Description = null, string? ProjectId = null);
public record ClassifyTaskRequest(string Title, string? Description = null, string? ProjectId = null);
public record NormalizeTitleRequest(string Title);
public record FindDuplicateRequest(string Title, string? Description = null, string? ProjectId = null);

// Валидация персоны-исполнителя/постановщика задачи: персона существует и принадлежит
// владельцу; проектная персона допустима только у задач своего проекта, если не имеет
// кросс-проектной ProjectTasks-привязки с полным доступом (externalScopes). null — ошибок нет.
public static class TaskPersonaValidator
{
    public static string? Error(PersonaManager personas, string userId, string personaId,
        string? taskProjectId,
        IReadOnlyList<(string ProjectId, bool ReadOnly)>? externalScopes = null)
    {
        var persona = personas.Get(personaId, userId);
        if (persona is null) return "Персона не найдена или недоступна";
        if (persona.Scope == PersonaScope.Project && persona.ProjectId != taskProjectId)
        {
            // Кросс-проектная ProjectTasks-привязка с полным доступом разрешает
            if (externalScopes is not null)
            {
                var scope = externalScopes.FirstOrDefault(s => s.ProjectId == taskProjectId);
                if (scope != default && !scope.ReadOnly)
                    return null;
            }
            return "Проектная персона может выполнять только задачи своего проекта";
        }
        return null;
    }
}

// Резолв категории статуса по id колонки доски
public static class BoardColumnHelper
{
    // Кастомная колонка проекта → её Category; дефолтная (id == имя категории) →
    // распарсенный статус (todo/inProgress/done); иначе null (не менять статус).
    public static TaskItemStatus? Category(Project? project, string? columnId)
    {
        if (string.IsNullOrEmpty(columnId)) return null;
        var custom = project?.BoardColumns?.FirstOrDefault(c => c.Id == columnId);
        if (custom is not null) return custom.Category;
        return Enum.TryParse<TaskItemStatus>(columnId, ignoreCase: true, out var cat) ? cat : null;
    }

    // Признак «карточка попадает в колонку ревью» (BoardColumn.Role == "review") для
    // DefectRules.EnsureReproOnReview. Только кастомные колонки проекта могут иметь Role —
    // дефолтные (todo/inProgress/done) им не бывают.
    public static bool IsReview(Project? project, string? columnId)
    {
        if (string.IsNullOrEmpty(columnId)) return false;
        var custom = project?.BoardColumns?.FirstOrDefault(c => c.Id == columnId);
        return custom?.Role == "review";
    }
}

public static class TaskHubExtensions
{
    // Уведомление всех устройств пользователя об изменении задачи
    public static Task BroadcastTaskChangedAsync(
        this IHubContext<SessionHub> hub, string userId, string action, TaskItem task) =>
        hub.Clients.Group("user_" + userId)
            .SendAsync("message", new TaskChangedMessage(action, task));
}

// Опции десериализации CreateTaskRequest для ручного парсинга тела в TasksController.Create
// (полю projectId нет в record, но клиент шлёт его в JSON — нужно для резолва колонки).
// Те же настройки, что и в Program.cs для глобального MVC: enum-ы в CamelCase,
// регистр имён свойств не важен.
public static class TaskCreateJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
