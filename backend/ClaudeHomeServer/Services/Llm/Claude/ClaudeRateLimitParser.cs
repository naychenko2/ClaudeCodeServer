using System.Text.Json;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm.Claude;

// Разбор события rate_limit_event из stream-json claude в RateLimitMessage.
// Единый источник правды: используется и живой сессией (ClaudeSession), и стартовым
// прогревом подписок (SubscriptionUsageWarmupService).
public static class ClaudeRateLimitParser
{
    // root — целая строка стрима: { "type": "rate_limit_event", "rate_limit_info": {…} }.
    // false, если нет rate_limit_info или в нём нет ни типа окна, ни utilization.
    public static bool TryParse(JsonElement root, out RateLimitMessage message)
    {
        message = null!;
        if (!root.TryGetProperty("rate_limit_info", out var info)) return false;

        // Форвардим ВСЕ события (включая "allowed"): utilization нужен для непрерывного
        // индикатора использования подписки.
        var status = info.TryGetProperty("status", out var stEl) ? stEl.GetString() : null;

        var utilization = info.TryGetProperty("utilization", out var utEl) && utEl.ValueKind == JsonValueKind.Number
            ? utEl.GetDouble() : (double?)null;
        var isUsingOverage = info.TryGetProperty("isUsingOverage", out var ovEl) && ovEl.ValueKind == JsonValueKind.True;

        var limitType =
            (info.TryGetProperty("rateLimitType", out var lt) ? lt.GetString() : null)
            ?? (info.TryGetProperty("rate_limit_type", out var lt2) ? lt2.GetString() : null)
            ?? "";

        // Нет ни типа окна, ни utilization — нечего показывать
        if (string.IsNullOrEmpty(limitType) && utilization is null) return false;

        // resetsAt может прийти как ISO-строка или unix-время (сек/мс) — нормализуем в ISO
        var resetsAt = NormalizeReset(info, "resetsAt", "resets_at");

        // Overage (перерасход сверх лимита, у тарифа Max): статус + время сброса окна перерасхода
        var overageStatus = info.TryGetProperty("overageStatus", out var osEl) ? osEl.GetString() : null;
        var overageResetsAt = NormalizeReset(info, "overageResetsAt", "overage_resets_at");

        message = new RateLimitMessage(limitType, resetsAt, status, utilization, isUsingOverage, overageStatus, overageResetsAt);
        return true;
    }

    // Нормализует поле времени сброса (ISO-строка или unix сек/мс) в ISO-строку
    private static string? NormalizeReset(JsonElement info, string key1, string key2)
    {
        if (info.TryGetProperty(key1, out var ra) || info.TryGetProperty(key2, out ra))
        {
            if (ra.ValueKind == JsonValueKind.String) return ra.GetString();
            if (ra.ValueKind == JsonValueKind.Number && ra.TryGetInt64(out var n))
                return (n > 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(n)
                    : DateTimeOffset.FromUnixTimeSeconds(n)).ToString("o");
        }
        return null;
    }
}
