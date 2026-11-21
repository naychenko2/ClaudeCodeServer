using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Кнопка «Мне неприятно» (KM-B6d, ADR-041 «Контракт», ручка №9). Владелец — только из JWT;
/// ребёнок опознаётся по стору через IChildModePolicy. Сам сигнал и рассылка — ChildStopSignalService;
/// ответ ребёнку не несёт ничего, кроме подтверждения: ни списка получателей, ни текста уведомления.
/// Порядок отказов: 403 not_child → 404 chat_not_found (чужой, не детский, несуществующий —
/// одно тело) → 429 too_frequent (повтор в окне схлопнулся в уже отправленный сигнал).
/// </summary>
[ApiController]
[Authorize]
[Route("api/child")]
public class ChildStopSignalController(
    IChildModePolicy policy,
    SessionManager sessions,
    ChildStopSignalService signals,
    ILogger<ChildStopSignalController> logger) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [ChildAllowed]
    [HttpPost("stop-signal")]
    public async Task<IActionResult> Raise([FromBody] ChildStopSignalRequest? req)
    {
        if (policy.For(UserId) is null)
            return Error(403, "not_child", "Эта ручка только для детей.");

        var sessionId = string.IsNullOrWhiteSpace(req?.SessionId) ? null : req!.SessionId;
        if (sessionId is not null)
        {
            // Чужой, не детский и несуществующий — одно тело 404 (существование не раскрываем).
            // Урок Б9b — чат проекта-предмета, поэтому ProjectId здесь не признак «не детский»:
            // граница одна — ChildSpace (тот же, что у Voice в ChildChatsController и у Б9d).
            var chat = sessions.GetOwnedVisible(sessionId, UserId);
            if (chat is null || chat.ChildSpace is null)
                return Error(404, "chat_not_found", "Чат не найден.");
        }

        try
        {
            var outcome = await signals.RaiseAsync(UserId, sessionId, StopSignalSource.Child);
            return outcome == StopSignalOutcome.Collapsed
                ? Error(429, "too_frequent", "Взрослым уже сообщили — подожди минуту.")
                : Accepted(new { accepted = true });
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Детский режим: стоп-сигнал не поднят (owner {Owner})", UserId);
            return Error(503, "policy_unavailable", "Не получилось сообщить — позови взрослого рядом.");
        }
    }

    private static JsonResult Error(int status, string code, string message) =>
        new(new { code, message }) { StatusCode = status };
}

// Тело POST /api/child/stop-signal (ADR-041, ручка №9): sessionId необязателен.
public record ChildStopSignalRequest(string? SessionId = null);
