using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Composition.Notifications;

// Реализация ITaskNotificationDispatcher (Core) поверх NotificationService (Main).
// Тонкий форвардер: вертикаль Tasks через шов шлёт напоминания и события
// исполнителя, ничего не зная о NotificationStore/Hub/Push/PersonaManager/
// ProjectManager, которые сам NotificationService держит для денормализации
// и доставки. Обоснование отдельного шва — в ITaskNotificationDispatcher.cs.
public sealed class TaskNotificationDispatcherAdapter(NotificationService notifications)
    : ITaskNotificationDispatcher
{
    public Task SendNotificationMessageAsync(string userId, NotificationMessage msg, bool sendPush = false) =>
        notifications.SendNotificationMessageAsync(userId, msg, sendPush);

    public Task SendExecutionEventAsync(string userId, string sessionId,
        string title, string body, string? projectId, string? taskId,
        string type, string tag, string url) =>
        notifications.SendExecutionEventAsync(userId, sessionId, title, body,
            projectId, taskId, type, tag, url);
}