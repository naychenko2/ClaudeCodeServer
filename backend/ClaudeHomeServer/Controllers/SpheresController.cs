using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
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
    IDeviceExecChannel? deviceExec = null) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Записей памяти сферы; хранилище памяти сферы подключат позже, пока всегда 0
    internal static int MemoryCount(string sphereId) => 0;

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
            .Where(p => p.Scope == PersonaScope.Sphere && p.SphereId == id)
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
            memory = new { count = MemoryCount(id) },
            openTasks,
        });
    }
}
