namespace ClaudeHomeServer.Services.Composition.Notifications;

using ClaudeHomeServer.Models;

// Реализация IKnowledgeNotificationDispatcher (Core) поверх NotificationService/NotificationStore.
// Тонкая обёртка: делегирует в NotificationStore.GetLastCreatedAtByTypeAsync и
// NotificationService.SendAsync с sendPush:false. Создана ради выноса KnowledgeAlertNotifier
// из Main в отдельный .csproj — KnowledgeAlertNotifier больше не имеет прямого доступа
// к NotificationService/NotificationStore.
public sealed class KnowledgeNotificationDispatcher(
    NotificationService notifications,
    NotificationStore store) : IKnowledgeNotificationDispatcher
{
    public async Task<DateTimeOffset?> LastNotifiedAtAsync(string userId, string notifType, CancellationToken ct = default)
    {
        var last = await store.GetLastCreatedAtByTypeAsync(userId, notifType);
        return last is null ? null : new DateTimeOffset(last.Value, TimeSpan.Zero);
    }

    public async Task NotifyAsync(string userId, string title, string body, string url, string tag, string source, CancellationToken ct = default)
    {
        await notifications.SendAsync(userId, new CreateNotificationRequest
        {
            Kind = "alert",
            Type = "knowledge_index_error",
            Title = title,
            Body = body,
            Url = url,
            Tag = tag,
            Source = source,
        }, sendPush: false);
    }
}
