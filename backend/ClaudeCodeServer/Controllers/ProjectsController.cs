using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeCodeServer.Models;
using ClaudeCodeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeCodeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/projects")]
public class ProjectsController(ProjectManager projects, SessionManager sessions) : ControllerBase
{
    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    private object WithCount(Project p) => new
    {
        p.Id, p.Name, p.RootPath, p.CreatedAt, p.UpdatedAt,
        SessionCount = sessions.CountByProject(p.Id),
    };

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
            var p = projects.Create(req.Name, req.RootPath, UserId, username, req.CreateDirectory);
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
            var updated = projects.Update(id, req.Name, req.RootPath);
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
        return NoContent();
    }
}

public record CreateProjectRequest(string Name, string? RootPath, bool CreateDirectory = false);
public record UpdateProjectRequest(string? Name, string? RootPath);
