namespace ClaudeHomeServer.Services;

// Шов Hub-рассылки для вертикали Knowledge. Тонкий адаптер в Main
// конструирует KnowledgeChangedMessage и шлёт в группу user_{userId}.
// IHubContext<SessionHub> живёт в Main, Core не знает ни о Hub, ни о типе
// сообщения.
public interface IKnowledgeHubNotifier
{
    // Рассылает «база знаний изменилась»: пользователь userId, действие action
    // (created/deleted/updated/doc_changed), id датасета.
    Task BroadcastKnowledgeChangedAsync(string userId, string action, string? datasetId);
}
