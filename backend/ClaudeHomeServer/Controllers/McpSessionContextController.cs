using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Состав контекста ТЕКУЩЕГО чата для MCP-тула <c>context_list</c> (фича chat-context).
///
/// Сессию берём не из параметра, а из заголовка <c>X-Caller-Session-Id</c>, который ставит
/// общий api() каждого MCP-сервера: тул адресован собственному чату модели, и параметр
/// позволил бы ей спросить состав чужого. Владелец сверяется с sub сервисного JWT —
/// сессия другого пользователя даёт 403, а не пустой список (пустой был бы неотличим
/// от «в контексте ничего нет»).
/// </summary>
[ApiController]
[Authorize]
[Route("api/mcp")]
public class McpSessionContextController(SessionManager sessions, SessionContextResolver resolver)
    : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet("session-context")]
    public IActionResult Get()
    {
        if (Request.Headers[DenyOnDelegatedTurnAttribute.CallerHeader].FirstOrDefault()
            is not { Length: > 0 } sessionId)
            return BadRequest(new { error = "Не определена сессия вызова (X-Caller-Session-Id)" });

        var session = sessions.GetById(sessionId);
        if (session == null) return NotFound(new { error = "Чат не найден" });
        // Владельца проектной сессии резолвит проект (Session.OwnerId у неё пуст) — берём
        // единую точку SessionManager.GetOwned, а не сравниваем поле вручную
        if (sessions.GetOwned(sessionId, UserId) is null)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Контекст чужого чата недоступен" });

        return Ok(new
        {
            sessionId = session.Id,
            // Контекст живёт в проекте сессии: адреса file/task разворачиваются внутри него,
            // отдельного projectId у записи нет — тул подставляет этот в files_read/tasks_get.
            projectId = session.ProjectId,
            entries = resolver.Resolve(session, UserId),
        });
    }
}
