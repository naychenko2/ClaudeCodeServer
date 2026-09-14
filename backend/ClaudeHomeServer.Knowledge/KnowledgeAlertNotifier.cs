using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Knowledge;

// Куда реконсайлер сообщает владельцу о непроходящих ошибках индексации.
// Отдельный шов (а не прямой вызов NotificationService) нужен ровно для тестов:
// сам NotificationService тянет стор, хаб и push, а проверять надо дедуп «не чаще
// раза в сутки», а не доставку.
public interface IKnowledgeAlertNotifier
{
    Task NotifyAsync(string userId, string title, string body, CancellationToken ct = default);

    // Когда владельцу последний раз уходило это уведомление (UTC) — отметка, пережившая
    // рестарт. Без неё кулдаун жил только в памяти реконсайлера и обнулялся при каждом
    // подъёме процесса: продукт перезапускается watchdog'ом и выкатками по нескольку раз
    // в день, и владелец получал «раз в сутки» несколько раз в сутки.
    Task<DateTimeOffset?> LastNotifiedAtAsync(string userId, CancellationToken ct = default);
}

// Доставка целиком переиспользует NotificationService (запись в колокол + тост по
// SignalR), как это делает AlertPollingService. Без push: индексация — не пожар,
// будить телефон незачем.
public sealed class KnowledgeAlertNotifier(
    IKnowledgeNotificationDispatcher dispatcher,
    ILogger<KnowledgeAlertNotifier> log) : IKnowledgeAlertNotifier
{
    // Подтип уведомления — он же ключ, по которому ищется отметка в сторе
    public const string NotifType = "knowledge_index_error";

    public async Task<DateTimeOffset?> LastNotifiedAtAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            return await dispatcher.LastNotifiedAtAsync(userId, NotifType, ct);
        }
        catch (Exception ex)
        {
            // Нечитаемый стор не должен обрывать обход: считаем, что отметки нет,
            // и полагаемся на кулдаун в памяти
            log.LogWarning(ex, "Не удалось прочитать отметку уведомления для {UserId}", userId);
            return null;
        }
    }

    public async Task NotifyAsync(string userId, string title, string body, CancellationToken ct = default)
    {
        try
        {
            await dispatcher.NotifyAsync(userId, title, body,
                url: "#/knowledge",
                tag: "knowledge-index-error",
                source: "Знания",
                ct: ct);
        }
        catch (Exception ex)
        {
            // Отказ доставки не должен обрывать обход целей реконсайлером
            log.LogWarning(ex, "Не удалось уведомить {UserId} об ошибках индексации", userId);
        }
    }
}
