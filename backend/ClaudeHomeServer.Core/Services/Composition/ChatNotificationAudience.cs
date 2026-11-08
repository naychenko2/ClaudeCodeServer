using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

/// <summary>
/// Адресат уведомления по чату (in-app тост + web push) — пара к
/// <see cref="SessionBroadcasterExtensions.ToChatAudience"/>: приватный чат
/// (<c>PrivateToUserId</c>) — ТОЛЬКО его автор, общий чат проекта и чат вне проекта —
/// прежний адресат (<see cref="ISessionDirectory.ResolveOwnerId"/>: владелец проекта /
/// владелец чата). Без этой точки эскалация штаба, просроченная очередь устройства и
/// ответ автоматизации уходили владельцу проекта с именем и id чужого приватного чата
/// (дефект e127cef0, правило И.2).
/// Пустой <c>PrivateToUserId</c> — «не видит никто» (fail-closed, как
/// <see cref="Access.SessionAccess.CanSee"/>): адресата нет, null — вызывающий
/// пропускает отправку. Приватность читается В МОМЕНТ вызова, не кэшируется.
/// </summary>
public static class ChatNotificationAudience
{
    public static string? OwnerOf(ISessionDirectory sessions, Session session)
    {
        if (session.PrivateToUserId is { } author)
            return string.IsNullOrEmpty(author) ? null : author;
        return sessions.ResolveOwnerId(session);
    }
}
