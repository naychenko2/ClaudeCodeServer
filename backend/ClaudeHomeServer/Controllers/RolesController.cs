using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Глобальный пул ролей («Команда» в верхнем хабе): CRUD ролей, найм-интервью без проекта,
// память внепроектного контекста текущего пользователя.
[ApiController]
[Authorize]
[Route("api/roles")]
public class GlobalRolesController(RoleManager roles, RoleMemoryService roleMemory,
    RoleGeneratorService generator, SkillsService skills, ProjectManager projects) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet]
    public IActionResult GetAll() => Ok(roles.GetAll());

    // Агенты для глобального редактора роли — только глобальные (~/.claude/agents)
    [HttpGet("agents")]
    public IActionResult GetAgents() => Ok(skills.GetProjectAgents(AppContext.BaseDirectory));

    // Найм-интервью без проекта: агенты — только глобальные (~/.claude/agents)
    [HttpPost("interview")]
    public async Task<IActionResult> Interview([FromBody] InterviewRequest req)
    {
        var history = (req.Messages ?? [])
            .Select(m => new InterviewMessage(m.Role, m.Content))
            .ToList();
        try
        {
            var result = await generator.InterviewAsync(projectRootPath: null, history);
            return Ok(result);
        }
        catch (TimeoutException ex) { return StatusCode(504, new { error = ex.Message }); }
        catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
    }

    // Глобальный найм: роль создаётся в пуле, ни к какому проекту не прикомандирована
    [HttpPost]
    public IActionResult Create([FromBody] CreateRoleRequest req)
    {
        var role = roles.Create(null, req.Name, req.Title, req.Avatar, req.Color,
            req.Persona, req.AgentNames, req.SystemPrompt, req.Model, req.Effort);
        return CreatedAtAction(nameof(GetAll), null, role);
    }

    [HttpPut("{roleId}")]
    public IActionResult Update(string roleId, [FromBody] UpdateRoleRequest req)
    {
        var updated = roles.Update(roleId, req.Name, req.Title, req.Avatar, req.Color,
            req.Persona, req.AgentNames, req.SystemPrompt, req.Model, req.Effort);
        return updated is null ? NotFound() : Ok(updated);
    }

    // Полное удаление из пула: роль + ВСЯ её память по всем контекстам
    [HttpDelete("{roleId}")]
    public IActionResult Delete(string roleId)
    {
        if (!roles.Delete(roleId)) return NotFound();
        roleMemory.DeleteRole(roleId);
        return NoContent();
    }

    // Обзор памяти роли по контекстам: проекты + внепроектные чаты текущего пользователя.
    // Для списка сотрудников — «что сотрудник помнит».
    [HttpGet("{roleId}/memory/overview")]
    public IActionResult MemoryOverview(string roleId)
    {
        if (roles.GetById(roleId) is null) return NotFound();
        var items = roleMemory.ListContexts(roleId, UserId)
            .Select(c => new
            {
                c.Context,
                Title = c.Context.StartsWith("chats-")
                    ? "Чаты"
                    : "Проект: " + (projects.GetById(c.Context)?.Name ?? "(удалён)"),
                c.Facts,
            });
        return Ok(items);
    }

    // Память внепроектных чатов текущего пользователя с ролью
    [HttpGet("{roleId}/memory")]
    public IActionResult GetMemory(string roleId)
    {
        if (roles.GetById(roleId) is null) return NotFound();
        return Ok(new { content = roleMemory.Read(roleId, $"chats-{UserId}") });
    }

    [HttpPut("{roleId}/memory")]
    public IActionResult SaveMemory(string roleId, [FromBody] RoleMemoryRequest req)
    {
        if (roles.GetById(roleId) is null) return NotFound();
        roleMemory.Overwrite(roleId, $"chats-{UserId}", req.Content ?? "");
        return NoContent();
    }
}

// Команда проекта: прикомандированные роли, найм с автопривязкой, приглашение из пула,
// открепление (память о проекте сохраняется), память проектного контекста.
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/roles")]
public class RolesController(RoleManager roles, ProjectManager projects, RoleMemoryService roleMemory,
    RoleGeneratorService generator) : ControllerBase
{
    // Роль существует и прикомандирована к проекту
    private Models.Role? AssignedRole(string projectId, string roleId)
    {
        var role = roles.GetById(roleId);
        return role is not null && role.ProjectIds.Contains(projectId) ? role : null;
    }

    [HttpGet]
    public IActionResult GetAll(string projectId)
    {
        if (projects.GetById(projectId) is null) return NotFound();
        return Ok(roles.GetByProject(projectId));
    }

    // Диалог-интервью для найма в проект: возвращает следующий вопрос либо готовый черновик.
    // messages — вся история диалога (stateless: фронт держит её и шлёт целиком).
    [HttpPost("interview")]
    public async Task<IActionResult> Interview(string projectId, [FromBody] InterviewRequest req)
    {
        var project = projects.GetById(projectId);
        if (project is null) return NotFound();
        var history = (req.Messages ?? [])
            .Select(m => new InterviewMessage(m.Role, m.Content))
            .ToList();
        try
        {
            var result = await generator.InterviewAsync(project.RootPath, history);
            return Ok(result);
        }
        catch (TimeoutException ex) { return StatusCode(504, new { error = ex.Message }); }
        catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
    }

    // Найм из проекта: роль создаётся в пуле и сразу прикомандировывается к проекту
    [HttpPost]
    public IActionResult Create(string projectId, [FromBody] CreateRoleRequest req)
    {
        if (projects.GetById(projectId) is null) return NotFound();
        var role = roles.Create(projectId, req.Name, req.Title, req.Avatar, req.Color,
            req.Persona, req.AgentNames, req.SystemPrompt, req.Model, req.Effort);
        return CreatedAtAction(nameof(GetAll), new { projectId }, role);
    }

    // Пригласить существующую роль из пула в команду проекта
    [HttpPost("{roleId}/assign")]
    public IActionResult Assign(string projectId, string roleId)
    {
        if (projects.GetById(projectId) is null) return NotFound();
        var role = roles.GetById(roleId);
        if (role is null) return NotFound();
        roles.Assign(roleId, projectId);
        return Ok(role);
    }

    [HttpPut("{roleId}")]
    public IActionResult Update(string projectId, string roleId, [FromBody] UpdateRoleRequest req)
    {
        if (AssignedRole(projectId, roleId) is null) return NotFound();
        var updated = roles.Update(roleId, req.Name, req.Title, req.Avatar, req.Color,
            req.Persona, req.AgentNames, req.SystemPrompt, req.Model, req.Effort);
        return updated is null ? NotFound() : Ok(updated);
    }

    // Открепить роль от проекта (удаление из «Команды» проекта). Роль остаётся в пуле,
    // её память о проекте сохраняется — при повторном приглашении роль «вспомнит» проект.
    [HttpDelete("{roleId}")]
    public IActionResult Unassign(string projectId, string roleId)
    {
        if (AssignedRole(projectId, roleId) is null) return NotFound();
        roles.Unassign(roleId, projectId);
        return NoContent();
    }

    // Память роли о проекте — просмотр и ручная правка из UI
    [HttpGet("{roleId}/memory")]
    public IActionResult GetMemory(string projectId, string roleId)
    {
        if (AssignedRole(projectId, roleId) is null) return NotFound();
        return Ok(new { content = roleMemory.Read(roleId, projectId) });
    }

    [HttpPut("{roleId}/memory")]
    public IActionResult SaveMemory(string projectId, string roleId, [FromBody] RoleMemoryRequest req)
    {
        if (AssignedRole(projectId, roleId) is null) return NotFound();
        roleMemory.Overwrite(roleId, projectId, req.Content ?? "");
        return NoContent();
    }
}

public record RoleMemoryRequest(string? Content = null);

public record InterviewRequest(List<InterviewMsgDto>? Messages = null);
public record InterviewMsgDto(string Role = "", string Content = "");

public record CreateRoleRequest(
    string Name = "",
    string Title = "",
    string Avatar = "",
    string Color = "",
    string Persona = "",
    List<string>? AgentNames = null,
    string? SystemPrompt = null,
    string? Model = null,
    string? Effort = null);

public record UpdateRoleRequest(
    string? Name = null,
    string? Title = null,
    string? Avatar = null,
    string? Color = null,
    string? Persona = null,
    List<string>? AgentNames = null,
    string? SystemPrompt = null,
    string? Model = null,
    string? Effort = null);
