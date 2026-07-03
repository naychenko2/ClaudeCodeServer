using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/projects")]
public class ProjectsController(ProjectManager projects, SessionManager sessions, AppSettingsService appSettings, TaskManager tasks) : ControllerBase
{
    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    private object WithCount(Project p)
    {
        var basePath = appSettings.Get().DefaultProjectsPath;
        var relativePath = string.IsNullOrEmpty(basePath) ? p.RootPath : Path.GetRelativePath(basePath, p.RootPath);
        return new { p.Id, p.Name, p.RootPath, RelativePath = relativePath, p.CreatedAt, p.UpdatedAt, p.GroupId, p.SystemPrompt, p.ShowHiddenFiles, p.PermissionRules, BuiltInSystemPrompt = ProjectManager.BuiltInSystemPrompt, SessionCount = sessions.CountByProject(p.Id) };
    }

    [HttpGet("builtin-prompt")]
    public IActionResult GetBuiltinPrompt() => Ok(new { content = ProjectManager.BuiltInSystemPrompt });

    [HttpGet]
    public IActionResult GetAll() => Ok(projects.GetByOwner(UserId).Select(WithCount));

    [HttpGet("{id}")]
    public IActionResult GetById(string id)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        return Ok(WithCount(p));
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
