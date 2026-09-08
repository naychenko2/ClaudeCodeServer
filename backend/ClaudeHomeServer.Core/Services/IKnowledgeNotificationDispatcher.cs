namespace ClaudeHomeServer.Services.Composition;

// Шов нотификатора для KnowledgeAlertNotifier. Сам NotificationService
// тянет стор, hub и push, а KnowledgeAlertNotifier должен проверять
// дедуп «не чаще раза в сутки» (LastNotifiedAtAsync) — это не часть
// общей нотификации. Поэтому узкий шов с двумя методами.
public interface IKnowledgeNotificationDispatcher
{
    Task<DateTimeOffset?> LastNotifiedAtAsync(string userId, string notifType, CancellationToken ct = default);
    Task NotifyAsync(string userId, string title, string body, string url, string tag, string source, CancellationToken ct = default);
}
