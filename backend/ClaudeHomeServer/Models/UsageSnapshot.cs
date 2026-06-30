namespace ClaudeHomeServer.Models;

// Снимок использования окна лимита подписки в момент времени (из rate_limit_event).
// Utilization — доля 0..1; LimitType — five_hour/seven_day/weekly; ResetsAt — ISO-время сброса.
public record UsageSnapshot(
    DateTime Timestamp,
    string LimitType,
    double? Utilization,
    string? Status,
    bool IsUsingOverage,
    string? ResetsAt,
    string? OverageStatus = null,
    string? OverageResetsAt = null);

// Информация о тарифе подписки (из ~/.claude/.credentials.json)
public record PlanInfo(string? SubscriptionType, string? RateLimitTier, string Label);

// Ответ /api/usage: история снимков + тариф
public record UsageResponse(IReadOnlyList<UsageSnapshot> Snapshots, PlanInfo? Plan);
