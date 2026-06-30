using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/projects/{projectId}/roles")]
public class RolesController(RoleManager roles, ProjectManager projects) : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll(string projectId)
    {
        if (projects.GetById(projectId) is null) return NotFound();
        return Ok(roles.GetByProject(projectId));
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
        return NoContent();
    }
}

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
