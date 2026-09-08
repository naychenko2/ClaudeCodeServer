using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Composition;

// Реализация ISessionMessageObserver (Core) поверх SessionManager.
// Тонкая обёртка: подписывается на SessionManager.OnSessionMessage,
// извлекает ProjectId из Session.Info.ProjectId для интерфейса.
// Создана ради выноса Knowledge в отдельный .csproj: после выноса
// ProjectKnowledgeTurnSync больше не имеет прямого доступа к SessionManager.
public sealed class SessionMessageObserver(SessionManager sessions) : ISessionMessageObserver
{
    public void Attach(Func<string?, ServerMessage, Task> handler) =>
        sessions.OnSessionMessage += (s, m) => handler(s.ProjectId, m);

    public void Detach(Func<string?, ServerMessage, Task> handler) =>
        sessions.OnSessionMessage -= (s, m) => handler(s.ProjectId, m);
}
