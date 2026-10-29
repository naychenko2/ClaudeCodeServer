using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Отказ REST-входам хода (право RunTurn, S1-7): чат виден, а вести его ход вызывающему нельзя —
// 403 той же формы, что у [ProjectScope] (существование ему и так известно); чата не видно — 404.
// null — можно, дальше работает обычная логика эндпоинта.
internal static class TurnRightExtensions
{
    public static IActionResult? DenyTurn(this SessionManager sessions, string sessionId, string userId)
    {
        if (sessions.GetWithRight(sessionId, userId, ProjectRight.RunTurn) is not null) return null;
        return sessions.GetAccessible(sessionId, userId) is null
            ? new NotFoundResult()
            : new ObjectResult(new { error = "Нет права вести ход", code = ProjectScopeAttribute.ErrorCode })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
    }
}
