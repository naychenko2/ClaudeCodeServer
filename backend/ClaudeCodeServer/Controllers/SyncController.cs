using ClaudeCodeServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeCodeServer.Controllers;

[ApiController]
[Route("api/projects/{projectId}/sync")]
public class SyncController(SyncService sync, ProjectManager projects) : ControllerBase
{
    [HttpGet]
    public IActionResult Get(string projectId)
    {
        if (projects.GetById(projectId) is null) return NotFound();
        return Ok(sync.GetMarks(projectId));
    }

    [HttpPost]
    public IActionResult Add(string projectId, [FromBody] SyncMarkRequest req)
    {
        if (projects.GetById(projectId) is null) return NotFound();
        sync.Add(projectId, req.Path, req.IsDirectory);
        return Ok();
    }

    // path по умолчанию "" — снятие корневой метки (синхронизация всего проекта)
    [HttpDelete]
    public IActionResult Remove(string projectId, [FromQuery] string path = "")
    {
        sync.Remove(projectId, path);
        return NoContent();
    }
}

public record SyncMarkRequest(string Path, bool IsDirectory = false);
