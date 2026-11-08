using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Отказ REST-входам хода. Четыре уровня (S1-7, S2a-10, QA-P1), каждый со своим машинным кодом —
// фронт по нему различает, чего именно ему нельзя (participantGate.isNotChatAuthorError):
//  • DenyTurn — право RunTurn: отправить сообщение в чат проекта (владелец и редактор);
//    403 project_scope — «нет права вести ход»;
//  • DenyTurnAuthor — «владелец или автор текущего хода»: перебой идущего хода (REST-ручка
//    pending/preempt). Прочему участнику — 403 not_chat_author (S2a-10b); право писать здесь не
//    спрашивается: читатель тоже не автор хода, и отвечает ему тот же отказ. «Стоп» и ответы на
//    вопросы агента в хабе держит тем же IsOwnerOrTurnAuthor SessionHub.OwnerOrTurnAuthor, и
//    отказывает тем же кодом: SignalR-текст «not_chat_author: …» (S2a-10c) — клиент видит только
//    строку error, поэтому код живёт в ней префиксом, а не в data;
//  • DenyOwnerTurn — только владелец (Configure): режим прав, цикл, штаб, смена модели и
//    провайдера, продолжение. Участнику — 403 owner_settings (S2a-10b): один гейт на все
//    настройки чата, отдельного «на модель» нет.
//  • DenyContentWrite — право WriteContent: переименование и архив чата общего проекта
//    (QA-P1, решение Андрея по продуктовому вопросу 2 плана v2: редактор и выше, читатель —
//    ничего). О нём же, а не о Write: ProjectAccessService.Resolve не даёт Write участнику
//    («запись откроют S2b/S2c», ProjectAccessService.cs:24), а WriteContent — право, которое
//    открывается участнику поимённо (ProjectAccess.cs:43-47, двери S2c-8).
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

    /// <summary>
    /// Право записи контента чата (QA-P1): переименование и архив чата общего проекта открыты
    /// редактору и выше; читатель — 403 project_scope («нет права на запись», тот же код и текст,
    /// что у отказа атрибута [ProjectScope(Right = WriteContent)]: ProjectScopeAttribute.cs:91-96),
    /// посторонний и чужой приватный чат — 404. Решение о праве идёт ПОСЛЕ проверки видимости:
    /// GetWithRight→ResolveSession сначала сверяет CanSee (ProjectAccessService.cs:38), а отказ
    /// Deny различает видимого без права (403) и невидимому (404) — тот же порядок, что у DenyTurn.
    /// </summary>
    public static IActionResult? DenyContentWrite(this SessionManager sessions, string sessionId, string userId) =>
        Deny(sessions, sessionId, userId, ProjectRight.WriteContent, NoWrite);

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

    private static ObjectResult NoWrite() =>
        new(new { error = "Нет права на запись", code = ProjectScopeAttribute.ErrorCode })
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
}
