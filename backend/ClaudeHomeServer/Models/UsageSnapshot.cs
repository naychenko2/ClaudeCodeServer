namespace ClaudeHomeServer.Models;

// Снимок использования окна лимита подписки в момент времени (из rate_limit_event).
// Utilization — доля 0..1; LimitType — five_hour/seven_day/weekly; ResetsAt — ISO-время сброса.
// SubscriptionKey — какая подписка сгенерировала снимок ("claude" — основная, ключ из пула — дополнительная).
public record UsageSnapshot(
    DateTime Timestamp,
    string LimitType,
    double? Utilization,
    string? Status,
    bool IsUsingOverage,
    string? ResetsAt,
    string? OverageStatus = null,
    string? OverageResetsAt = null,
    string SubscriptionKey = "claude");

// Информация о тарифе подписки (из ~/.claude/.credentials.json)
public record PlanInfo(string? SubscriptionType, string? RateLimitTier, string Label);

// Ответ /api/usage: история снимков + тариф + per-subscription utilisation.
// RotationThreshold — порог утилизации 5h-окна, выше которого аккаунт выведен из ротации
// новых чатов (заполняется только при наличии дополнительных подписок).
public record UsageResponse(IReadOnlyList<UsageSnapshot> Snapshots, PlanInfo? Plan,
    Dictionary<string, SubscriptionUsage>? Subscriptions = null, double? RotationThreshold = null);

// Utilisation одной подписки: снимки + опциональное имя + статус роутинга.
// InRotation — берёт ли пул этот аккаунт для новых чатов; Utilization — эффективная
// утилизация 5h-окна (истёкшее окно/нет данных = 0), по которой считается InRotation.
public record SubscriptionUsage(IReadOnlyList<UsageSnapshot> Snapshots, string? Name = null,
    bool InRotation = true, double Utilization = 0);
