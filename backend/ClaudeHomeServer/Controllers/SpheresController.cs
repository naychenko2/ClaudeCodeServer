using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/spheres")]
public class SpheresController(
    SphereManager spheres,
    ProjectManager projects,
    PersonaManager personas,
    TaskManager tasks,
    SphereMemoryService sphereMemory,
    TeamMemoryService teamMemory,
    IDeviceExecChannel? deviceExec = null) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    /// <summary>Сводка сферы: проекты с матрицей возможностей, команда, память, открытые задачи.</summary>
    [HttpGet("{id}/overview")]
    public IActionResult Overview(string id)
    {
        var sphere = spheres.GetOwned(id, UserId);
        if (sphere is null) return NotFound();

        var sphereProjects = spheres.ProjectsOf(UserId, id)
            .Select(pid => projects.GetById(pid))
            .OfType<Project>()
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var projectDtos = sphereProjects.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            capabilities = ProjectCapabilities.For(p,
                ProjectCapabilities.IsDeviceBound(p) ? deviceExec?.GetStatus(UserId, p.DeviceId!) : null),
        }).ToList();

        var team = personas.GetByOwner(UserId)
            .Where(p => PersonaZone.IsSphereTeam(p, id))
            .Select(p => new { id = p.Id, name = p.Name, handle = p.Handle, role = p.Role })
            .ToList();

        var openTasks = sphereProjects
            .SelectMany(p => tasks.GetByProject(p.Id))
            .Where(t => t.Status != TaskItemStatus.Done)
            .OrderByDescending(t => t.Priority).ThenBy(t => t.DueDate ?? "9999")
            .Select(t => new
            {
                id = t.Id,
                title = t.Title,
                projectId = t.ProjectId,
                status = t.Status,
                priority = t.Priority,
                dueDate = t.DueDate,
            })
            .ToList();

        return Ok(new
        {
            sphere = new { id = sphere.Id, name = sphere.Name, color = sphere.Color, icon = sphere.Icon, charter = sphere.Charter },
            projects = projectDtos,
            team,
            memory = new { count = sphereMemory.Count(UserId, id) },
            openTasks,
        });
    }

    // --- Память сферы. Запись человеком из UI: PersonaZone.CanWriteSphereMemory(null, …) = true ---

    /// <summary>Полка сферы + полки проектов сферы (чтобы на странице было что переносить).</summary>
    [HttpGet("{id}/memory")]
    public IActionResult MemoryList(string id)
    {
        if (spheres.GetOwned(id, UserId) is null) return NotFound();
        var projectShelves = spheres.ProjectsOf(UserId, id)
            .Select(pid => projects.GetById(pid))
            .OfType<Project>()
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new SphereMemoryProjectShelf(p.Id, p.Name,
                [.. teamMemory.List(UserId, p.Id).Select(SphereMemoryEntryDto.From)]))
            .ToList();
        return Ok(new SphereMemoryResponse(
            [.. sphereMemory.List(UserId, id).Select(SphereMemoryEntryDto.From)],
            projectShelves,
            sphereMemory.MaxEntries));
    }

    /// <summary>Записать в память сферы (человек).</summary>
    [HttpPost("{id}/memory")]
    public IActionResult MemoryAdd(string id, [FromBody] SphereMemoryAddRequest req)
    {
        if (spheres.GetOwned(id, UserId) is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest(new { error = "Пустой текст" });
        if (TeamMemoryService.LengthViolation(req.Text, 0) is { } tooLong) return BadRequest(new { error = tooLong });
        var type = Enum.TryParse<TeamMemoryType>(req.Type, true, out var t) ? t : TeamMemoryType.Fact;
        return Ok(SphereMemoryEntryDto.From(sphereMemory.Add(UserId, id, req.Text, type)));
    }

    [HttpDelete("{id}/memory/{entryId}")]
    public IActionResult MemoryRemove(string id, string entryId)
    {
        if (spheres.GetOwned(id, UserId) is null) return NotFound();
        return sphereMemory.Remove(UserId, id, entryId) ? NoContent() : NotFound();
    }

    /// <summary>Поднять запись с полки проекта сферы на полку сферы (перемещение; тот же код, что у MCP adopt).</summary>
    [HttpPost("{id}/memory/adopt")]
    public IActionResult MemoryAdopt(string id, [FromBody] SphereMemoryAdoptRequest req)
    {
        if (spheres.GetOwned(id, UserId) is null) return NotFound();
        if (string.IsNullOrEmpty(req.ProjectId) || !spheres.ProjectsOf(UserId, id).Contains(req.ProjectId))
            return BadRequest(new { error = "Проект не входит в сферу" });
        var adopted = sphereMemory.Adopt(UserId, id, teamMemory, req.ProjectId, req.EntryId ?? "");
        return adopted is null ? NotFound(new { error = "Запись не найдена в памяти проекта" })
            : Ok(SphereMemoryEntryDto.From(adopted));
    }
}

public record SphereMemoryAddRequest(string Text, string? Type);
public record SphereMemoryAdoptRequest(string ProjectId, string EntryId);

public record SphereMemoryPromotionDto(string ProjectId, string EntryId, DateTime At);

public record SphereMemoryEntryDto(string Id, string ProjectId, string Text, string Type, double Salience,
    string Source, DateTime CreatedAt, SphereMemoryPromotionDto? PromotedFrom)
{
    public static SphereMemoryEntryDto From(TeamMemoryEntry e) => new(e.Id, e.ProjectId, e.Text,
        e.Type.ToString().ToLowerInvariant(), e.Salience, e.Source.ToString().ToLowerInvariant(), e.CreatedAt,
        e.PromotedFrom is { } p ? new SphereMemoryPromotionDto(p.ProjectId, p.EntryId, p.At) : null);
}

public record SphereMemoryProjectShelf(string ProjectId, string ProjectName, List<SphereMemoryEntryDto> Entries);

public record SphereMemoryResponse(List<SphereMemoryEntryDto> Sphere, List<SphereMemoryProjectShelf> Projects, int MaxEntries);
