using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Composition;

// Реализация ISessionMessageObserver (Core) поверх SessionManager.
// Тонкая обёртка: подписывается на SessionManager.OnSessionMessage и пробрасывает
// (Session, ServerMessage) обработчику шва.
//
// Этап 5 (сцепка Memory + Dossiers): сигнатура шва расширена до
// `Func<Session, ServerMessage, Task>` — потребителям (Memory autolearn,
// Dossier capture) нужны поля Session (PersonaId/ProjectId/WorktreePath/
// Participants). Раньше знал только ProjectId; единственный потребитель
// той формы (Knowledge) адаптирован.
//
// Кэш обёрток обязателен: иначе Detach не нашёл бы свой делегат в мультикасте
// (сравнение идёт по Target+Method, у каждого вызова `s => handler(...)` Target
// свой собственный), и обработчик оставался бы подписанным навсегда.
public sealed class SessionMessageObserver(SessionManager sessions) : ISessionMessageObserver
{
    private readonly ConcurrentDictionary<Func<Session, ServerMessage, Task>, Func<Session, ServerMessage, Task>> _wrappers = new();
    private readonly object _attachLock = new();

    public void Attach(Func<Session, ServerMessage, Task> handler)
    {
        // Лок сериализует пару «получить/создать обёртку + подписать» — иначе под
        // гонкой GetOrAdd мог бы сделать лишнюю обёртку, а подписка одной из
        // них утекла бы мимо словаря (Detach снимет не ту).
        lock (_attachLock)
        {
            if (_wrappers.ContainsKey(handler)) return;
            _wrappers[handler] = handler;
            sessions.OnSessionMessage += handler;
        }
    }

    public void Detach(Func<Session, ServerMessage, Task> handler)
    {
        if (_wrappers.TryRemove(handler, out var wrapper))
        {
            sessions.OnSessionMessage -= wrapper;
        }
    }
}
