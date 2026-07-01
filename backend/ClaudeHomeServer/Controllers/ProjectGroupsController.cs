using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/project-groups")]
public class ProjectGroupsController(ProjectGroupManager groups, ProjectManager projects) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet]
    public IActionResult GetAll() => Ok(groups.GetByOwner(UserId));

    [HttpPost]
    public IActionResult Create([FromBody] CreateGroupRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Укажите название группы" });
        var g = groups.Create(req.Name.Trim(), req.Color ?? "", UserId);
        return Ok(g);
    }

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] UpdateGroupRequest req)
    {
        var g = groups.GetById(id);
        if (g is null || g.OwnerId != UserId) return NotFound();
        var updated = groups.Update(id, req.Name?.Trim(), req.Color);
        return Ok(updated);
    }

    [HttpPost("reorder")]
    public IActionResult Reorder([FromBody] ReorderGroupsRequest req)
        => Ok(groups.Reorder(UserId, req.OrderedIds ?? []));

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        var g = groups.GetById(id);
        if (g is null || g.OwnerId != UserId) return NotFound();
        groups.Delete(id);
        // Проекты удалённой группы возвращаются в список «без группы»
        projects.ClearGroup(id);
        return NoContent();
    }
}

public record CreateGroupRequest(string Name, string? Color);
public record UpdateGroupRequest(string? Name, string? Color);
public record ReorderGroupsRequest(List<string>? OrderedIds);
