using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Memory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/project-groups")]
public class ProjectGroupsController(SphereManager groups, ProjectManager projects, PersonaManager personas,
    SphereMemoryService sphereMemory) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet]
    public IActionResult GetAll() => Ok(groups.GetByOwner(UserId));

    [HttpPost]
    public IActionResult Create([FromBody] CreateGroupRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Укажите название группы" });
        if (req.Charter is { Length: > Sphere.CharterMaxLength })
            return BadRequest(new { error = $"Хартия не длиннее {Sphere.CharterMaxLength} символов" });
        // Значок — по белому списку lucide, как в Update; пусто — без значка
        var icon = req.Icon?.Trim();
        if (!string.IsNullOrEmpty(icon))
        {
            var candidate = Services.ProjectIcons.ProjectIconGlyphService.ValidateGlyph(icon);
            if (candidate is null)
                return BadRequest(new { error = "Негодный значок: нужно имя иконки из набора lucide" });
            icon = candidate.Name;
        }
        var g = groups.Create(req.Name.Trim(), req.Color ?? "", UserId, icon, req.Charter);
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
    public async Task<IActionResult> Delete(string id)
    {
        var g = groups.GetById(id);
        if (g is null || g.OwnerId != UserId) return NotFound();
        // С командой сфера не удаляется: персон сначала переносят или удаляют. Память живёт и
        // умирает вместе со сферой — её забирает удаление, отказа из-за неё нет
        var team = personas.GetByOwner(UserId).Count(p => PersonaZone.IsSphereTeam(p, id));
        if (team > 0)
            return Conflict(new { error = $"Сферу нельзя удалить — в ней персон сферы: {team}", personas = team });
        groups.Delete(id);
        // Проекты удалённой группы возвращаются в список «без группы»
        projects.ClearGroup(id);
        var deletedMemory = await sphereMemory.DeleteAllForSphereAsync(UserId, id);
        return Ok(new DeleteGroupResponse(deletedMemory));
    }
}

/// <summary>Ответ удаления сферы: сколько записей её памяти удалено вместе с ней.</summary>
public record DeleteGroupResponse(int DeletedMemory);

public record CreateGroupRequest(string Name, string? Color, string? Icon = null, string? Charter = null);
public record UpdateGroupRequest(string? Name, string? Color, string? Icon = null, string? Charter = null);
public record ReorderGroupsRequest(List<string>? OrderedIds);
