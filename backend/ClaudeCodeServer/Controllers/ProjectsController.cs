using ClaudeCodeServer.Models;
using ClaudeCodeServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeCodeServer.Controllers;

[ApiController]
[Route("api/projects")]
public class ProjectsController(ProjectManager projects, SessionManager sessions) : ControllerBase
{
    // Проекция проекта с числом его сессий — для карточки проекта (MA13)
    private object WithCount(Models.Project p) => new
    {
        p.Id, p.Name, p.RootPath, p.CreatedAt, p.UpdatedAt,
        SessionCount = sessions.CountByProject(p.Id),
    };

    [HttpGet]
    public IActionResult GetAll() => Ok(projects.GetAll().Select(WithCount));

    [HttpGet("{id}")]
    public IActionResult GetById(string id)
    {
        var p = projects.GetById(id);
        return p is null ? NotFound() : Ok(WithCount(p));
    }

    [HttpPost]
    public IActionResult Create([FromBody] CreateProjectRequest req)
    {
        try
        {
            var p = projects.Create(req.Name, req.RootPath);
            return CreatedAtAction(nameof(GetById), new { id = p.Id }, WithCount(p));
        }
        catch (DirectoryNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] UpdateProjectRequest req)
    {
        try
        {
            var p = projects.Update(id, req.Name, req.RootPath);
            return Ok(WithCount(p));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (DirectoryNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id) =>
        projects.Delete(id) ? NoContent() : NotFound();
}

public record CreateProjectRequest(string Name, string RootPath);
public record UpdateProjectRequest(string? Name, string? RootPath);
