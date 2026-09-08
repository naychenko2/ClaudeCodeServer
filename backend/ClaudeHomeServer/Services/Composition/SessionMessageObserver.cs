using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Composition;

// Реализация ISessionMessageObserver (Core) поверх SessionManager.
// Тонкая обёртка: подписывается на SessionManager.OnSessionMessage,
// извлекает ProjectId из Session.Info.ProjectId для интерфейса.
// Создана ради выноса Knowledge в отдельный .csproj: после выноса
// ProjectKnowledgeTurnSync больше не имеет прямого доступа к SessionManager.
//
// Кэш обёрток обязателен: иначе Detach не нашёл бы свой делегат в мультикасте
// (сравнение идёт по Target+Method, у каждого вызова `s => handler(...)` Target
// свой собственный), и обработчик оставался бы подписанным навсегда.
public sealed class SessionMessageObserver(SessionManager sessions) : ISessionMessageObserver
{
    private readonly ConcurrentDictionary<Func<string?, ServerMessage, Task>, Func<Session, ServerMessage, Task>> _wrappers = new();
    private readonly object _attachLock = new();

    public void Attach(Func<string?, ServerMessage, Task> handler)
    {
        // Лок сериализует пару «получить/создать обёртку + подписать» — иначе под
        // гонкой GetOrAdd мог бы сделать лишнюю обёртку, а подписка одной из
        // них утекла бы мимо словаря (Detach снимет не ту).
        lock (_attachLock)
        {
            if (_wrappers.ContainsKey(handler)) return;
            Func<Session, ServerMessage, Task> wrapper = (s, m) => handler(s.ProjectId, m);
            _wrappers[handler] = wrapper;
            sessions.OnSessionMessage += wrapper;
        }
    }

    public void Detach(Func<string?, ServerMessage, Task> handler)
    {
        if (_wrappers.TryRemove(handler, out var wrapper))
        {
            sessions.OnSessionMessage -= wrapper;
        }
    }
}
