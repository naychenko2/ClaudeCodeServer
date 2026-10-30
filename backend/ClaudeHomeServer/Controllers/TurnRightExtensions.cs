using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Отказ REST-входам хода. Три уровня (S1-7, S2a-10), каждый со своим машинным кодом — фронт по
// нему различает, чего именно ему нельзя (participantGate.isNotChatAuthorError):
//  • DenyTurn — право RunTurn: отправить сообщение в чат проекта (владелец и редактор);
//    403 project_scope — «нет права вести ход»;
//  • DenyTurnAuthor — «владелец или автор текущего хода»: перебой идущего хода (REST-ручка
//    pending/preempt). Прочему участнику — 403 not_chat_author (S2a-10b); право писать здесь не
//    спрашивается: читатель тоже не автор хода, и отвечает ему тот же отказ. «Стоп» и ответы на
//    вопросы агента в хабе держит тем же IsOwnerOrTurnAuthor SessionHub.OwnerOrTurnAuthor;
//    текст отказа хаба — общий «Доступ запрещён», машинного кода в SignalR-контракте нет;
//  • DenyOwnerTurn — только владелец (Configure): режим прав, цикл, штаб, смена модели и
//    провайдера, продолжение. Участнику — 403 owner_settings (S2a-10b): один гейт на все
//    настройки чата, отдельного «на модель» нет.
// Чата не видно — 404 (существование не раскрываем). null — можно, дальше работает обычная
// логика эндпоинта.
internal static class TurnRightExtensions
{
    public static IActionResult? DenyTurn(this SessionManager sessions, string sessionId, string userId) =>
        Deny(sessions, sessionId, userId, ProjectRight.RunTurn, Forbidden);

    public static IActionResult? DenyOwnerTurn(this SessionManager sessions, string sessionId, string userId) =>
        Deny(sessions, sessionId, userId, ProjectRight.Configure, OwnerSettingsGate.Denied);

    /// <summary>
    /// Владелец или автор идущего хода (S2a-10b). Авторство читается из реестра актора под тем же
    /// PendingLock, под которым пишется подача хода (SessionManager.NoteTurnActor / GetTurnActor) —
    /// проверка не видит «половинного» актора и не решает по устаревшему. Неизвестный актор автором
    /// не считается (fail-closed), чужой ход никто, кроме его автора и владельца, не останавливает.
    /// </summary>
    public static IActionResult? DenyTurnAuthor(this SessionManager sessions, string sessionId, string userId)
    {
        if (sessions.GetAccessible(sessionId, userId) is null) return new NotFoundResult();
        return sessions.IsOwnerOrTurnAuthor(sessionId, userId) ? null : ChatAuthorGate.Denied();
    }

    private static IActionResult? Deny(SessionManager sessions, string sessionId, string userId,
        ProjectRight right, Func<ObjectResult> denied)
    {
        if (sessions.GetWithRight(sessionId, userId, right) is not null) return null;
        return sessions.GetAccessible(sessionId, userId) is null ? new NotFoundResult() : denied();
    }

    private static ObjectResult Forbidden() =>
        new(new { error = "Нет права вести ход", code = ProjectScopeAttribute.ErrorCode })
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
}
