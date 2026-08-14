using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Knowledge;

// Куда реконсайлер сообщает владельцу о непроходящих ошибках индексации.
// Отдельный шов (а не прямой вызов NotificationService) нужен ровно для тестов:
// сам NotificationService тянет стор, хаб и push, а проверять надо дедуп «не чаще
// раза в сутки», а не доставку.
public interface IKnowledgeAlertNotifier
{
    Task NotifyAsync(string userId, string title, string body, CancellationToken ct = default);
}

// Доставка целиком переиспользует NotificationService (запись в колокол + тост по
// SignalR), как это делает AlertPollingService. Без push: индексация — не пожар,
// будить телефон незачем.
public sealed class KnowledgeAlertNotifier(
    NotificationService notifications,
    ILogger<KnowledgeAlertNotifier> log) : IKnowledgeAlertNotifier
{
    public async Task NotifyAsync(string userId, string title, string body, CancellationToken ct = default)
    {
        try
        {
            await notifications.SendAsync(userId, new CreateNotificationRequest
            {
                Kind = "alert",
                Type = "knowledge_index_error",
                Title = title,
                Body = body,
                Url = "#/knowledge",
                // Тег схлопывает повторы в шторке браузера
                Tag = "knowledge-index-error",
                Source = "Знания",
            }, sendPush: false);
        }
        catch (Exception ex)
        {
            // Отказ доставки не должен обрывать обход целей реконсайлером
            log.LogWarning(ex, "Не удалось уведомить {UserId} об ошибках индексации", userId);
        }
    }
}
