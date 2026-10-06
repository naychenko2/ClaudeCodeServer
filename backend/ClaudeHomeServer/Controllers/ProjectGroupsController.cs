using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/project-groups")]
public class ProjectGroupsController(SphereManager groups, ProjectManager projects, PersonaManager personas) : ControllerBase
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
        if (req.Charter is { Length: > Sphere.CharterMaxLength })
            return BadRequest(new { error = $"Хартия не длиннее {Sphere.CharterMaxLength} символов" });
        // Значок валидируется по белому списку lucide, как у проекта (ADR-009); "" — снять
        var icon = req.Icon?.Trim();
        if (!string.IsNullOrEmpty(icon))
        {
            var candidate = Services.ProjectIcons.ProjectIconGlyphService.ValidateGlyph(icon);
            if (candidate is null)
                return BadRequest(new { error = "Негодный значок: нужно имя иконки из набора lucide" });
            icon = candidate.Name;
        }
        var updated = groups.Update(id, req.Name?.Trim(), req.Color, icon, req.Charter);
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
        // Сфера с командой или памятью не удаляется: сначала их переносят или удаляют
        var team = personas.GetByOwner(UserId).Count(p => p.Scope == PersonaScope.Sphere && p.SphereId == id);
        var memory = SpheresController.MemoryCount(id);
        if (team > 0 || memory > 0)
        {
            var parts = new List<string>();
            if (team > 0) parts.Add($"персон сферы: {team}");
            if (memory > 0) parts.Add($"записей памяти: {memory}");
            return Conflict(new { error = $"Сферу нельзя удалить — в ней {string.Join(", ", parts)}", personas = team, memory });
        }
        groups.Delete(id);
        // Проекты удалённой группы возвращаются в список «без группы»
        projects.ClearGroup(id);
        return NoContent();
    }
}

public record CreateGroupRequest(string Name, string? Color);
public record UpdateGroupRequest(string? Name, string? Color, string? Icon = null, string? Charter = null);
public record ReorderGroupsRequest(List<string>? OrderedIds);
