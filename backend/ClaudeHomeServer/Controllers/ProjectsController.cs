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

[ApiController]
[Authorize]
[Route("api/projects")]
public class ProjectsController(ProjectManager projects, SessionManager sessions, AppSettingsService appSettings, WorkspaceKnowledgeStore wkStore, TaskManager tasks, ProjectEventLogService events, TeamMemoryService teamMemory, IHubContext<SessionHub> hub) : ControllerBase
{
    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    private Task BroadcastTeamMemory(string action, string projectId, string? entryId = null) =>
        hub.Clients.Group("user_" + UserId).SendAsync("message", new TeamMemoryChangedMessage(action, projectId, entryId));

    private object WithCount(Project p)
    {
        var basePath = appSettings.Get().DefaultProjectsPath;
        var relativePath = string.IsNullOrEmpty(basePath) ? p.RootPath : Path.GetRelativePath(basePath, p.RootPath);
        return new { p.Id, p.Name, p.RootPath, RelativePath = relativePath, p.CreatedAt, p.UpdatedAt, p.GroupId, p.SystemPrompt, p.ShowHiddenFiles, p.PermissionRules, p.BoardColumns, BuiltInSystemPrompt = ProjectManager.BuiltInSystemPrompt, SessionCount = sessions.CountByProject(p.Id) };
    }

    [HttpGet("builtin-prompt")]
    public IActionResult GetBuiltinPrompt() => Ok(new { content = ProjectManager.BuiltInSystemPrompt });

    // Эффективный системный промпт проекта — ровно те части, что уходят в --append-system-prompt
    // (без промпта агента: он добавляется per-session для агент-чатов)
    [HttpGet("{id}/effective-prompt")]
    public IActionResult GetEffectivePrompt(string id)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        var wk = wkStore.GetByPath(p.RootPath);
        var parts = ProjectManager.GetSystemPromptParts(
            p.SystemPrompt, wk?.DifyDatasetId != null, wk?.DocumentTags);
        return Ok(new { parts });
    }

    [HttpGet]
    public IActionResult GetAll() => Ok(projects.GetByOwner(UserId).Select(WithCount));

    [HttpGet("{id}")]
    public IActionResult GetById(string id)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        return Ok(WithCount(p));
    }

    // Лента событий проекта (активность команды): ходы, задачи, память, база, заметки, состав.
    // Фильтры опциональны (since/type/actor/limit). Источник для командного центра (①-L1).
    [HttpGet("{id}/events")]
    public IActionResult GetEvents(string id,
        [FromQuery] DateTime? since, [FromQuery] string? type,
        [FromQuery] string? actor, [FromQuery] int? limit)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        return Ok(events.Query(id, UserId, since, type, actor, limit ?? 100));
    }

    // === Память команды проекта (③-3.4) === — общие факты/договорённости, которые recall'ят
    // все персоны команды проекта наравне с личной памятью.

    [HttpGet("{id}/team-memory")]
    public IActionResult TeamMemory(string id)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        return Ok(teamMemory.List(UserId, id));
    }

    [HttpPost("{id}/team-memory")]
    public async Task<IActionResult> AddTeamMemory(string id, [FromBody] TeamMemoryRequest req)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest(new { error = "Пустой текст" });
        var entry = teamMemory.Add(UserId, id, req.Text);
        await BroadcastTeamMemory("added", id, entry.Id);
        return Ok(entry);
    }

    [HttpPut("{id}/team-memory/{entryId}")]
    public async Task<IActionResult> UpdateTeamMemory(string id, string entryId, [FromBody] TeamMemoryRequest req)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest(new { error = "Пустой текст" });
        var entry = teamMemory.Update(UserId, id, entryId, req.Text);
        if (entry is null) return NotFound();
        await BroadcastTeamMemory("updated", id, entryId);
        return Ok(entry);
    }

    [HttpDelete("{id}/team-memory/{entryId}")]
    public async Task<IActionResult> RemoveTeamMemory(string id, string entryId)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        if (!teamMemory.Remove(UserId, id, entryId)) return NotFound();
        await BroadcastTeamMemory("removed", id, entryId);
        return NoContent();
    }

    [HttpPost]
    public IActionResult Create([FromBody] CreateProjectRequest req)
    {
        try
        {
            var username = User.FindFirstValue(ClaimTypes.Name) ?? UserId;
            var p = projects.Create(req.Name, req.RootPath, UserId, username, req.CreateDirectory, req.GroupId);
            return CreatedAtAction(nameof(GetById), new { id = p.Id }, WithCount(p));
        }
        catch (DirectoryNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] UpdateProjectRequest req)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        try
        {
            var updated = projects.Update(id, req.Name, req.RootPath, req.SystemPrompt, req.ShowHiddenFiles, req.PermissionRules, req.GroupId);
            return Ok(WithCount(updated));
        }
        catch (DirectoryNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Кастомные колонки Kanban-доски проекта (пустой список → дефолтные 3)
    [HttpPut("{id}/board-columns")]
    public IActionResult UpdateBoardColumns(string id, [FromBody] UpdateBoardColumnsRequest req)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        var updated = projects.UpdateBoardColumns(id, req.Columns);
        return Ok(WithCount(updated));
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        projects.Delete(id);
        tasks.DeleteByProject(id);
        return NoContent();
    }
}

public record CreateProjectRequest(string Name, string? RootPath, bool CreateDirectory = false, string? GroupId = null);
public record UpdateProjectRequest(string? Name, string? RootPath, string? SystemPrompt, bool? ShowHiddenFiles, List<PermissionRule>? PermissionRules = null, string? GroupId = null);
public record UpdateBoardColumnsRequest(List<BoardColumn>? Columns);
public record TeamMemoryRequest(string Text);
