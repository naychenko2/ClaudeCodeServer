namespace ClaudeHomeServer.Services.Composition;

// Шов алертов подписки. SubscriptionWindowMismatchGuard (Llm) сообщает алерты
// владельцу при расхождении 5h-окна между probe и oauth каналами — аномалия
// обычно означает, что setup-токен сгенерирован под чужим аккаунтом (инцидент
// 20–23.08.2026). Отдельный шов (а не прямой вызов NotificationService) — тот же
// приём, что IKnowledgeNotificationDispatcher: сам NotificationService тянет
// стор, hub и push, а тестировать надо логику доставки админам, а не общий
// нотификатор.
//
// NotifyAdminsAsync — узкая функция: разрешение списка админов, дефолтные поля
// (kind=alert, url=#/spend, tag/source=«Подписки») и best-effort рассылка с
// подавлением отказа одному админу — зашиты в реализации.
public interface ISubscriptionAlertNotifier
{
    Task NotifyAdminsAsync(string title, string body);
}
