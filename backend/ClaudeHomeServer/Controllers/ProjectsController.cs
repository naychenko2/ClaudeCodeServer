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
public class ProjectsController(ProjectManager projects, SessionManager sessions, AppSettingsService appSettings, UserStore users, UserHomeResolver homes, WorkspaceKnowledgeStore wkStore, TaskManager tasks, ProjectEventLogService events, TeamMemoryService teamMemory, KnowledgeService knowledge, NotesKnowledgeService notesKb, PersonaManager personas, PersonaMemoryService personaMemory, IHubContext<SessionHub> hub) : ControllerBase
{
    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    private Task BroadcastTeamMemory(string action, string projectId, string? entryId = null) =>
        hub.Clients.Group("user_" + UserId).SendAsync("message", new TeamMemoryChangedMessage(action, projectId, entryId));

    private object WithCount(Project p)
    {
        // Путь показываем относительно домашней папки владельца — с учётом override она может
        // не совпадать с DefaultProjectsPath (иначе получилось бы «..\..\GIT\myproj»)
        var basePath = homes.Resolve(users.GetById(UserId)) ?? appSettings.Get().DefaultProjectsPath;
        var relativePath = string.IsNullOrEmpty(basePath) ? p.RootPath : Path.GetRelativePath(basePath, p.RootPath);
        return new { p.Id, p.Name, p.RootPath, RelativePath = relativePath, p.CreatedAt, p.UpdatedAt, p.GroupId, p.SystemPrompt, p.ShowHiddenFiles, p.ToolsEnabled, p.PermissionRules, p.BoardColumns, BuiltInSystemPrompt = ProjectManager.BuiltInSystemPrompt, SessionCount = sessions.CountByProject(p.Id) };
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
        var entry = teamMemory.Add(UserId, id, req.Text, req.Type ?? TeamMemoryType.Fact);
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

    // Поиск по памяти команды: семантический (при Dify) либо полнотекстовый. Дёргается MCP team_memory_search.
    [HttpGet("{id}/team-memory/search")]
    public async Task<IActionResult> SearchTeamMemory(string id, [FromQuery] string q, [FromQuery] int topK = 8)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        if (string.IsNullOrWhiteSpace(q)) return Ok(Array.Empty<TeamMemoryEntry>());
        return Ok(await teamMemory.SearchAsync(UserId, id, q.Trim(), Math.Clamp(topK, 1, 20)));
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
    public async Task<IActionResult> Update(string id, [FromBody] UpdateProjectRequest req)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        // Update мутирует объект проекта на месте — старые значения снимаем до вызова
        var oldName = p.Name;
        var oldRoot = p.RootPath;
        try
        {
            var updated = projects.Update(id, req.Name, req.RootPath, req.SystemPrompt, req.ShowHiddenFiles, req.PermissionRules, req.GroupId, req.ToolsEnabled);

            // Смена папки проекта: перенести запись знаний под новый ключ — иначе запись сиротеет,
            // для нового пути создаётся дубль-датасет, а mcp dify молча теряет dataset_id
            if (WorkspaceKnowledgeStore.NormalizePath(oldRoot) != WorkspaceKnowledgeStore.NormalizePath(updated.RootPath))
                wkStore.Move(oldRoot, updated.RootPath);

            // Переименование проекта: best-effort освежить имена Dify-датасетов
            // ({user}:{project} и {user}:team:{project}); сбой не ломает работу по id
            if (!string.Equals(oldName, updated.Name, StringComparison.Ordinal))
            {
                var username = User.FindFirstValue(ClaimTypes.Name) ?? UserId;
                var datasetId = wkStore.GetByPath(updated.RootPath)?.DifyDatasetId;
                if (!string.IsNullOrEmpty(datasetId))
                    try { await knowledge.RenameDatasetAsync(datasetId, $"{username}:{updated.Name}"); }
                    catch { /* стухшее имя не критично */ }
                try { await teamMemory.RenameProjectDatasetAsync(UserId, id, username, updated.Name); }
                catch { /* стухшее имя не критично */ }
            }

            return Ok(WithCount(updated));
        }
        catch (DirectoryNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
        // папка вне песочницы либо уже занята другим проектом владельца — это ошибка ввода, не 500
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
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
    public async Task<IActionResult> Delete(string id)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        projects.Delete(id);
        tasks.DeleteByProject(id);
        // Память команды проекта: локальные сторы + Dify-датасет (best-effort — уборка не должна ронять удаление)
        try { await teamMemory.DeleteProjectTeamMemoryAsync(UserId, id); }
        catch { /* удаление проекта не зависит от уборки памяти команды */ }

        // База знаний проекта: Dify-датасет + запись WorkspaceKnowledge. Датасет общий для
        // проектов в одной папке — чистим, только если RootPath больше никем не используется
        if (projects.GetByRootPath(p.RootPath).Count == 0)
        {
            var wk = wkStore.GetByPath(p.RootPath);
            if (wk is not null)
            {
                if (!string.IsNullOrEmpty(wk.DifyDatasetId))
                {
                    try { await knowledge.DeleteDatasetAsync(wk.DifyDatasetId); }
                    catch { /* датасет мог быть удалён в Dify — снимаем только запись */ }
                    await hub.Clients.Group("user_" + UserId)
                        .SendAsync("message", new KnowledgeChangedMessage("deleted", wk.DifyDatasetId));
                }
                wkStore.Delete(p.RootPath);
            }
        }

        // Заметки notes/ проекта выпали из alive-set — вычистить их из «{user}:notes» сразу,
        // не дожидаясь следующей несвязанной правки заметок
        notesKb.QueueSync(UserId);

        // Проектные персоны осиротели вместе с проектом — каскад: память (стор + Dify-датасет),
        // сама персона (файлы сабагента снимет OnPersonaDeleted), событие фронту
        foreach (var persona in personas.GetByOwner(UserId)
                     .Where(x => x.Scope == PersonaScope.Project && x.ProjectId == id).ToList())
        {
            try { await personaMemory.DeletePersonaAsync(persona.Id); }
            catch { /* память персоны — best-effort */ }
            personas.Delete(persona.Id, UserId);
            await hub.Clients.Group("user_" + UserId)
                .SendAsync("message", new PersonasChangedMessage("deleted", persona.Id));
        }

        return NoContent();
    }
}

public record CreateProjectRequest(string Name, string? RootPath, bool CreateDirectory = false, string? GroupId = null);
public record UpdateProjectRequest(string? Name, string? RootPath, string? SystemPrompt, bool? ShowHiddenFiles, bool? ToolsEnabled = null, List<PermissionRule>? PermissionRules = null, string? GroupId = null);
public record UpdateBoardColumnsRequest(List<BoardColumn>? Columns);
public record TeamMemoryRequest(string Text, TeamMemoryType? Type = null);
