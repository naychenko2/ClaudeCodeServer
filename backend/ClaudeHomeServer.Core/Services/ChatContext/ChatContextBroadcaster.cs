using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.ChatContext;

// Рассылка chat_context_changed владельцу (канал тот же, что у image_thread_changed): полный DTO с
// подписями от провайдеров видов. Сессии или проекта нет — слать нечего, фронт догонит GET контекста.
public sealed class ChatContextBroadcaster(
    ContextKindRegistry registry,
    ISessionDirectory sessions,
    IProjectManager projects,
    ISessionBroadcaster broadcaster,
    ILogger<ChatContextBroadcaster> log) : IChatContextNotifier
{
    public void Changed(string ownerId, string sessionId, ChatContextState state)
    {
        _ = SendAsync(ownerId, sessionId, state);
    }

    private async Task SendAsync(string ownerId, string sessionId, ChatContextState state)
    {
        try
        {
            if (sessions.GetById(sessionId) is not { } session) return;
            var project = session.ProjectId is { } pid ? projects.GetById(pid) : null;
            var dto = ChatContextDtoBuilder.Build(registry, new ContextScope(ownerId, session, project), state);
            await broadcaster.ToOwner(ownerId, new ChatContextChangedMessage(dto) { SessionId = sessionId });
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Контекст чата {SessionId} не разослан", sessionId);
        }
    }
}
