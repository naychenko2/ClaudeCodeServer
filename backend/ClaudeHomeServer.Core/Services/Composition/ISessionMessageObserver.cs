using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Composition;

// Шов для вертикалей, которым нужно слушать события хода Claude
// (SessionManager.OnSessionMessage). Прямой доступ к SessionManager
// из вынесенной вертикали запрещён: SessionManager живёт в Main.
//
// Этап 5 (сцепка Memory + Dossiers): сигнатура расширена до
// `Func<Session, ServerMessage, Task>` — Memory autolearn и Dossier capture
// используют поля Session (PersonaId, ProjectId, WorktreePath, Participants),
// не только ProjectId. Knowledge (ProjectKnowledgeTurnSync) был единственным
// потребителем, ему нужен лишь ProjectId; адаптация — взять s.ProjectId из
// Session в лямбде.
public interface ISessionMessageObserver
{
    // Подписаться на события хода; обработчик получает полную Session и ServerMessage.
    // Если handler не зарегистрирован — тихий no-op.
    void Attach(Func<Session, ServerMessage, Task> handler);

    // Отписаться.
    void Detach(Func<Session, ServerMessage, Task> handler);
}
