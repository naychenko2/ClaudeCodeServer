using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Узкий шов NotificationService для выноса Deploy (Этап 5, волна 2).
// DeployReportService шлёт уведомление об итоге выкатки (ADR-010): новый
// инстанс сообщает владельцу, что деплой прошёл/упал. Один метод — отправка
// сформированного CreateNotificationRequest с опциональным web-push.
public interface INotificationSender
{
    Task<string> SendAsync(string userId, CreateNotificationRequest req, bool sendPush = false);
}
