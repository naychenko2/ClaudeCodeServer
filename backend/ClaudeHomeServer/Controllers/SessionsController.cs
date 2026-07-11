using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet]
    public IActionResult GetAll(string projectId) => Ok(sessions.GetByProject(projectId));

    [HttpPost]
    public async Task<IActionResult> Create(string projectId, [FromBody] CreateSessionRequest req)
    {
        try
        {
            var mode = Enum.TryParse<ClaudeMode>(req.Mode, true, out var m) ? m : ClaudeMode.AcceptEdits;
            var session = await sessions.CreateAsync(projectId, mode, req.ResumeSessionId, req.Name, req.Model, req.AgentName, req.Effort);
            return CreatedAtAction(nameof(GetAll), new { projectId }, session);
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("{sessionId}")]
    public IActionResult Update(string projectId, string sessionId, [FromBody] UpdateSessionRequest req)
    {
        var session = sessions.GetById(sessionId);
        if (session == null || session.ProjectId != projectId) return NotFound();
        if (req.ExpiresAfterMinutes is not -1)
        {
            if (req.ExpiresAfterMinutes is <= 0) return BadRequest(new { error = "Срок жизни чата должен быть положительным" });
            sessions.SetExpiry(sessionId, req.ExpiresAfterMinutes);
        }
        try
        {
            var updated = sessions.Update(sessionId, req.Name, req.Model, req.Effort);
            return updated == null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Назначить/сменить/снять собеседника (персону или .md-агента) у проектной сессии — в т.ч. по ходу разговора
    [HttpPost("{sessionId}/persona")]
    public IActionResult SetPersona(string projectId, string sessionId, [FromBody] SetPersonaRequest req)
    {
        var session = sessions.GetById(sessionId);
        if (session == null || session.ProjectId != projectId) return NotFound();
        try
        {
            var updated = sessions.SetPersona(sessionId, UserId, req.PersonaId, req.AgentName);
            return updated == null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
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

public record CreateSessionRequest(string Mode = "acceptEdits", string? ResumeSessionId = null, string? Name = null, string? Model = null, string? AgentName = null, string? Effort = null);

// ExpiresAfterMinutes: -1 (поле не прислано) — не менять; null — сделать сессию постоянной;
// N > 0 — временная, авто-удаление через N минут после последней активности
public record UpdateSessionRequest(string? Name = null, string? Model = null, string? Effort = null, int? ExpiresAfterMinutes = -1);
