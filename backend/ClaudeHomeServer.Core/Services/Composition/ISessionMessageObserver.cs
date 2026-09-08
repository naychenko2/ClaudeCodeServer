using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Composition;

// Шов для вертикалей, которым нужно слушать события хода Claude
// (SessionManager.OnSessionMessage). Прямой доступ к SessionManager
// из вынесенной вертикали запрещён: SessionManager живёт в Main.
//
// Используется ProjectKnowledgeTurnSync (Knowledge): нужен ProjectId сессии
// для QueueSync. SessionId и Session.ProjectId — разные вещи: SessionId — id
// сессии в БД, ProjectId — id проекта. Передаём именно ProjectId (nullable),
// чтобы не тащить модель Session в Core.
public interface ISessionMessageObserver
{
    // Подписаться на события хода; обработчик получает ProjectId сессии (nullable)
    // и ServerMessage. Если handler не зарегистрирован — тихий no-op.
    void Attach(Func<string?, ServerMessage, Task> handler);

    // Отписаться.
    void Detach(Func<string?, ServerMessage, Task> handler);
}
