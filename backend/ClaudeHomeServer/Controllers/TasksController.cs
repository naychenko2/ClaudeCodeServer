using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Hubs;
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
    IHubContext<SessionHub> hub) : ControllerBase
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

        // Колонка доски → статус выводим из её категории
        var cat = BoardColumnHelper.Category(project, req.ColumnId);
        if (cat is not null) req = req with { Status = cat };

        // Персона-исполнитель: своя и в правильном проекте
        if (!string.IsNullOrEmpty(req.PersonaId)
            && TaskPersonaValidator.Error(personas, UserId, req.PersonaId, projectId) is { } personaError)
            return BadRequest(new { error = personaError });

        var task = tasks.Create(projectId, UserId, req);
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
    PersonaManager personas, TaskExecutionService executor, NoteTaskSyncService noteSync) : ControllerBase
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
            var description = await ai.GenerateDescriptionAsync(req.Title.Trim(), OwnProjectId(req.ProjectId), ct);
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
                req.Title.Trim(), req.Description ?? "", OwnProjectId(req.ProjectId), ct);
            return Ok(new { subtasks });
        }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // Личная задача — без привязки к проекту
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTaskRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "Название задачи не может быть пустым" });

        // Колонка доски → статус из категории (у личных — только дефолтные колонки)
        var cat = BoardColumnHelper.Category(null, req.ColumnId);
        if (cat is not null) req = req with { Status = cat };

        // Персона-исполнитель: своя; проектная персона личную задачу не берёт
        if (!string.IsNullOrEmpty(req.PersonaId)
            && TaskPersonaValidator.Error(personas, UserId, req.PersonaId, taskProjectId: null) is { } personaError)
            return BadRequest(new { error = personaError });

        var task = tasks.Create(null, UserId, req);
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
        var cat = BoardColumnHelper.Category(
            targetProjectId is null ? null : projects.GetById(targetProjectId), req.ColumnId);
        if (cat is not null) req = req with { Status = cat };

        // Персона-исполнитель: "" = убрать (валидировать нечего), непустая — проверяем.
        // Валидация по целевому проекту: проектная персона прежнего проекта в новом недействительна.
        if (!string.IsNullOrEmpty(req.PersonaId)
            && TaskPersonaValidator.Error(personas, UserId, req.PersonaId, targetProjectId) is { } personaError)
            return BadRequest(new { error = personaError });

        var wasDone = task.Status == TaskItemStatus.Done;
        var updated = tasks.Update(taskId, req)!;
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

// Валидация персоны-исполнителя задачи: персона существует и принадлежит владельцу,
// проектная персона допустима только у задач её проекта. null — ошибок нет.
public static class TaskPersonaValidator
{
    public static string? Error(PersonaManager personas, string userId, string personaId, string? taskProjectId)
    {
        var persona = personas.Get(personaId, userId);
        if (persona is null) return "Персона не найдена или недоступна";
        if (persona.Scope == PersonaScope.Project && persona.ProjectId != taskProjectId)
            return "Проектная персона может выполнять только задачи своего проекта";
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
}

public static class TaskHubExtensions
{
    // Уведомление всех устройств пользователя об изменении задачи
    public static Task BroadcastTaskChangedAsync(
        this IHubContext<SessionHub> hub, string userId, string action, TaskItem task) =>
        hub.Clients.Group("user_" + userId)
            .SendAsync("message", new TaskChangedMessage(action, task));
}
