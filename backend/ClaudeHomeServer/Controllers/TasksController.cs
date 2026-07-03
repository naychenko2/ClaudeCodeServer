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
    TaskManager tasks, ProjectManager projects, IHubContext<SessionHub> hub) : ControllerBase
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
        if (OwnProject(projectId) is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "Название задачи не может быть пустым" });

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
    TaskManager tasks, IHubContext<SessionHub> hub) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Личная задача — без привязки к проекту
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTaskRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "Название задачи не может быть пустым" });

        var task = tasks.Create(null, UserId, req);
        await hub.BroadcastTaskChangedAsync(UserId, "created", task);
        return Ok(task);
    }

    // Все задачи пользователя (календарь, MCP): диапазон по сроку, поиск и фильтры.
    // personal=true — только личные (вне проекта); projectId — только задачи проекта.
    [HttpGet]
    public IActionResult GetAll(
        [FromQuery] string? from = null, [FromQuery] string? to = null,
        [FromQuery] string? q = null, [FromQuery] string? status = null,
        [FromQuery] string? priority = null, [FromQuery] string? assignee = null,
        [FromQuery] string? projectId = null, [FromQuery] bool personal = false)
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

        var updated = tasks.Update(taskId, req)!;
        await hub.BroadcastTaskChangedAsync(UserId, "updated", updated);
        return Ok(updated);
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

public static class TaskHubExtensions
{
    // Уведомление всех устройств пользователя об изменении задачи
    public static Task BroadcastTaskChangedAsync(
        this IHubContext<SessionHub> hub, string userId, string action, TaskItem task) =>
        hub.Clients.Group("user_" + userId)
            .SendAsync("message", new TaskChangedMessage(action, task));
}
