using ClaudeCodeServer.Models;
using ClaudeCodeServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeCodeServer.Controllers;

[ApiController]
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
            var session = await sessions.CreateAsync(projectId, mode, req.ResumeSessionId);
            session.Name = req.Name;
            return CreatedAtAction(nameof(GetAll), new { projectId }, session);
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    [HttpDelete("{sessionId}")]
    public async Task<IActionResult> Delete(string projectId, string sessionId)
    {
        await sessions.DeleteAsync(sessionId);
        return NoContent();
    }
}

public record CreateSessionRequest(string Mode = "auto", string? ResumeSessionId = null, string? Name = null);
