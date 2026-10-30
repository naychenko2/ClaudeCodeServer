using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Отказ REST-входам хода. Три уровня (S1-7, S2a-10):
//  • DenyTurn — право RunTurn: отправить сообщение в чат проекта (владелец и редактор);
//  • DenyTurnAuthor — RunTurn плюс «владелец или автор текущего хода»: остановить/перебить ход;
//  • DenyOwnerTurn — только владелец (Configure): режим прав, цикл, штаб, смена провайдера, продолжение.
// 403 той же формы, что у [ProjectScope] (существование вызывающему и так известно); чата не видно — 404.
// null — можно, дальше работает обычная логика эндпоинта.
internal static class TurnRightExtensions
{
    public static IActionResult? DenyTurn(this SessionManager sessions, string sessionId, string userId) =>
        Deny(sessions, sessionId, userId, ProjectRight.RunTurn);

    public static IActionResult? DenyOwnerTurn(this SessionManager sessions, string sessionId, string userId) =>
        Deny(sessions, sessionId, userId, ProjectRight.Configure);

    /// <summary>Владелец или автор идущего хода; участник, не запускавший ход, — 403.</summary>
    public static IActionResult? DenyTurnAuthor(this SessionManager sessions, string sessionId, string userId)
    {
        if (Deny(sessions, sessionId, userId, ProjectRight.RunTurn) is { } denied) return denied;
        return sessions.IsOwnerOrTurnAuthor(sessionId, userId) ? null : Forbidden();
    }

    private static IActionResult? Deny(SessionManager sessions, string sessionId, string userId, ProjectRight right)
    {
        if (sessions.GetWithRight(sessionId, userId, right) is not null) return null;
        return sessions.GetAccessible(sessionId, userId) is null ? new NotFoundResult() : Forbidden();
    }

    private static ObjectResult Forbidden() =>
        new(new { error = "Нет права вести ход", code = ProjectScopeAttribute.ErrorCode })
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
}
