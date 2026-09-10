using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition.Llm;

// Реализация ISubscriptionAlertNotifier (Core) поверх NotificationService (Main).
// Доставка через NotificationService всем администраторам (подписки настраивает
// админ в конфиге — «владелец» это он). Kind "alert" (категория «Алерты») + push:
// инцидент чужого токена жил трое суток и нашёлся только ручным сравнением каналов.
//
// Создана ради выноса Llm в отдельный .csproj — SubscriptionWindowMismatchGuard
// (Llm) больше не имеет прямого доступа к NotificationService.
//
// Импорт Core/Models/AppNotification.cs нужен для CreateNotificationRequest —
// это в Core, как и сам шов. IUserStore — тоже Core-шов, существующий (Этап 5).
public sealed class SubscriptionAlertNotifier(
    NotificationService notifications,
    IUserStore users) : ISubscriptionAlertNotifier
{
    public async Task NotifyAdminsAsync(string title, string body)
    {
        try
        {
            var admins = users.GetAll().Where(u => u.Role == "admin").ToList();
            foreach (var admin in admins)
            {
                try
                {
                    await notifications.SendAsync(admin.Id, new CreateNotificationRequest
                    {
                        Kind = "alert",
                        Type = "subscription_window_mismatch",
                        Title = title,
                        Body = body,
                        // Экран «Расходы», вкладка квот — там видны снимки обоих каналов
                        Url = "#/spend",
                        Tag = "Подписки",
                        Source = "Подписки",
                    }, sendPush: true);
                }
                catch { /* отказ одному админу не должен обрывать рассылку остальным */ }
            }
        }
        catch { /* доставка best-effort — не ронять опрос, из-за которого она вызвана */ }
    }
}
