using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/projects/{projectId}/sessions")]
public class SessionsController(SessionManager sessions) : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll(string projectId) => Ok(sessions.GetByProject(projectId));

    [HttpPost]
    public async Task<IActionResult> Create(string projectId, [FromBody] CreateSessionRequest req)
    {
        try
        {
            var mode = Enum.TryParse<ClaudeMode>(req.Mode, true, out var m) ? m : ClaudeMode.Auto;
            var session = await sessions.CreateAsync(projectId, mode, req.ResumeSessionId, req.Name, req.Model);
            return CreatedAtAction(nameof(GetAll), new { projectId }, session);
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    [HttpPut("{sessionId}")]
    public IActionResult Update(string projectId, string sessionId, [FromBody] UpdateSessionRequest req)
    {
        var session = sessions.GetById(sessionId);
        if (session == null || session.ProjectId != projectId) return NotFound();
        var updated = sessions.Update(sessionId, req.Name, req.Model);
        return updated == null ? NotFound() : Ok(updated);
    }

    [HttpGet("{sessionId}/history")]
    public async Task<IActionResult> GetHistory(string projectId, string sessionId)
    {
        var session = sessions.GetById(sessionId);
        if (session == null || session.ProjectId != projectId) return NotFound();
        var history = await sessions.GetHistoryAsync(sessionId);
        return Ok(history);
    }

    [HttpDelete("{sessionId}")]
    public async Task<IActionResult> Delete(string projectId, string sessionId)
    {
        await sessions.DeleteAsync(sessionId);
        return NoContent();
    }
}

public record CreateSessionRequest(string Mode = "auto", string? ResumeSessionId = null, string? Name = null, string? Model = null);

public record UpdateSessionRequest(string? Name = null, string? Model = null);
