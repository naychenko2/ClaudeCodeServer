using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.ChatContext;

// Рассылка chat_context_changed — только сигнал с ревизией (решение CONSULT-CONTEXT-SUBJECT, вариант 3):
// у аудитории чата (ToChatAudience) одного субъекта нет, поэтому карточек в рассылке нет — клиент
// перечитывает их по REST под своим субъектом. Сессии нет — слать нечего, фронт догонит GET контекста.
public sealed class ChatContextBroadcaster(
    ISessionDirectory sessions,
    ISessionBroadcaster broadcaster,
    ILogger<ChatContextBroadcaster> log) : IChatContextNotifier
{
    public void Changed(string ownerId, string sessionId, ChatContextState state)
    {
        _ = SendAsync(sessionId, state);
    }

    private async Task SendAsync(string sessionId, ChatContextState state)
    {
        try
        {
            if (sessions.GetById(sessionId) is not { } session) return;
            await broadcaster.ToChatAudience(session, new ChatContextChangedMessage(state.Revision) { SessionId = sessionId });
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Контекст чата {SessionId} не разослан", sessionId);
        }
    }
}
