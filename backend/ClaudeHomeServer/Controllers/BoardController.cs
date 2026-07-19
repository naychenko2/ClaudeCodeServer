using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Диспетчерская доска агентов: live-статусы исполнителей (Claude + персоны).
[ApiController]
[Authorize]
[Route("api/board")]
public class BoardController(BoardService board, SessionManager sessions) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Сессия текущего пользователя (владелец резолвится в SessionManager)
    private Session? OwnedSession(string sessionId) => sessions.GetOwned(sessionId, UserId);

    /// <summary>
    /// GET /api/board/agents — доска агентов: все задачи с исполнителем Claude/персона,
    /// классифицированные по колонкам (queue/working/waiting/done).
    /// </summary>
    [HttpGet("agents")]
    public IActionResult GetAgents()
    {
        var items = board.GetBoard(UserId);
        return Ok(new { items });
    }

    /// <summary>
    /// POST /api/board/agents/{sessionId}/interrupt — прервать выполнение агента.
    /// </summary>
    [HttpPost("agents/{sessionId}/interrupt")]
    public IActionResult InterruptAgent(string sessionId)
    {
        if (OwnedSession(sessionId) is null) return NotFound();
        sessions.Interrupt(sessionId);
        return NoContent();
    }

    /// <summary>
    /// POST /api/board/agents/{sessionId}/permission/{requestId}/allow — разрешить запрос.
    /// </summary>
    [HttpPost("agents/{sessionId}/permission/{requestId}/allow")]
    public IActionResult AllowPermission(string sessionId, string requestId)
    {
        if (OwnedSession(sessionId) is null) return NotFound();
        // Без updatedInput — эхо тела permission ломало ответы (//af
        sessions.RespondPermission(sessionId, requestId, "allow");
        return NoContent();
    }

    /// <summary>
    /// POST /api/board/agents/{sessionId}/permission/{requestId}/deny — запретить запрос.
    /// </summary>
    [HttpPost("agents/{sessionId}/permission/{requestId}/deny")]
    public IActionResult DenyPermission(string sessionId, string requestId)
    {
        if (OwnedSession(sessionId) is null) return NotFound();
        sessions.RespondPermission(sessionId, requestId, "deny");
        return NoContent();
    }
}
