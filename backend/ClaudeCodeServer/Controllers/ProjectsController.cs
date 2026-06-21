using ClaudeCodeServer.Models;
using ClaudeCodeServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeCodeServer.Controllers;

[ApiController]
[Route("api/projects")]
public class ProjectsController(ProjectManager projects) : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll() => Ok(projects.GetAll());

    [HttpGet("{id}")]
    public IActionResult GetById(string id)
    {
        var p = projects.GetById(id);
        return p is null ? NotFound() : Ok(p);
    }

    [HttpPost]
    public IActionResult Create([FromBody] CreateProjectRequest req)
    {
        try
        {
            var p = projects.Create(req.Name, req.RootPath);
            return CreatedAtAction(nameof(GetById), new { id = p.Id }, p);
        }
        catch (DirectoryNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] UpdateProjectRequest req)
    {
        try
        {
            var p = projects.Update(id, req.Name, req.RootPath);
            return Ok(p);
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
