using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition.Notifications;

// Реализация INotificationSender (Core) поверх NotificationService (Main).
// Тонкий форвардер для DeployReportService: доложить об итоге выкатки
// владельцу через существущий механизм уведомлений.
public sealed class DeployNotificationSenderAdapter(NotificationService notifications)
    : INotificationSender
{
    public Task<string> SendAsync(string userId, CreateNotificationRequest req, bool sendPush = false) =>
        notifications.SendAsync(userId, req, sendPush);
}
