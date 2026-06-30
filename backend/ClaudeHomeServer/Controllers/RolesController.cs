using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/projects/{projectId}/roles")]
public class RolesController(RoleManager roles, ProjectManager projects, RoleMemoryService roleMemory,
    RoleGeneratorService generator) : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll(string projectId)
    {
        if (projects.GetById(projectId) is null) return NotFound();
        return Ok(roles.GetByProject(projectId));
    }

    // Диалог-интервью для генерации роли: возвращает следующий вопрос либо готовый черновик.
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

    [HttpPost]
    public IActionResult Create(string projectId, [FromBody] CreateRoleRequest req)
    {
        if (projects.GetById(projectId) is null) return NotFound();
        var role = roles.Create(projectId, req.Name, req.Title, req.Avatar, req.Color,
            req.Persona, req.AgentNames, req.SystemPrompt, req.Model, req.Effort);
        return CreatedAtAction(nameof(GetAll), new { projectId }, role);
    }

    [HttpPut("{roleId}")]
    public IActionResult Update(string projectId, string roleId, [FromBody] UpdateRoleRequest req)
    {
        var role = roles.GetById(roleId);
        if (role is null || role.ProjectId != projectId) return NotFound();
        var updated = roles.Update(roleId, req.Name, req.Title, req.Avatar, req.Color,
            req.Persona, req.AgentNames, req.SystemPrompt, req.Model, req.Effort);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{roleId}")]
    public IActionResult Delete(string projectId, string roleId)
    {
        var role = roles.GetById(roleId);
        if (role is null || role.ProjectId != projectId) return NotFound();
        roles.Delete(roleId);
        roleMemory.Delete(roleId);   // память роли больше не нужна
        return NoContent();
    }

    // Память роли — просмотр и ручная правка из UI
    [HttpGet("{roleId}/memory")]
    public IActionResult GetMemory(string projectId, string roleId)
    {
        var role = roles.GetById(roleId);
        if (role is null || role.ProjectId != projectId) return NotFound();
        return Ok(new { content = roleMemory.Read(roleId) });
    }

    [HttpPut("{roleId}/memory")]
    public IActionResult SaveMemory(string projectId, string roleId, [FromBody] RoleMemoryRequest req)
    {
        var role = roles.GetById(roleId);
        if (role is null || role.ProjectId != projectId) return NotFound();
        roleMemory.Overwrite(roleId, req.Content ?? "");
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
