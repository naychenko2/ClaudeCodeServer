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
        var status = ReadStatus(root);

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

        // Почему перерасход выключен (наблюдались out_of_credits и org_level_disabled) —
        // ключевая деталь для разбора инцидентов: «кончились кредиты» и «выключено на
        // уровне организации» лечатся по-разному
        var overageDisabledReason =
            (info.TryGetProperty("overageDisabledReason", out var odrEl) ? odrEl.GetString() : null)
            ?? (info.TryGetProperty("overage_disabled_reason", out var odrEl2) ? odrEl2.GetString() : null);

        message = new RateLimitMessage(limitType, resetsAt, status, utilization, isUsingOverage,
            overageStatus, overageResetsAt, overageDisabledReason);
        return true;
    }

    /// <summary>
    /// Только статус события, без полного разбора. Нужен телеметрии, которая пишется на
    /// КАЖДОЕ событие — включая те, где <see cref="TryParse"/> вернёт false (нет ни типа
    /// окна, ни utilization). null — поля нет.
    /// </summary>
    public static string? ReadStatus(JsonElement root) =>
        root.TryGetProperty("rate_limit_info", out var info)
        && info.TryGetProperty("status", out var st)
        && st.ValueKind == JsonValueKind.String
            ? st.GetString()
            : null;

    // Нормализует поле времени сброса (ISO-строка или unix сек/мс) в ISO-строку
    private static string? NormalizeReset(JsonElement info, string key1, string key2)
    {
        if (info.TryGetProperty(key1, out var ra) || info.TryGetProperty(key2, out ra))
        {
            if (ra.ValueKind == JsonValueKind.String) return ra.GetString();
            if (ra.ValueKind == JsonValueKind.Number && ra.TryGetInt64(out var n))
                return UnixToIso(n);
        }
        return null;
    }

    private static string UnixToIso(long n) =>
        (n > 100_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(n)
            : DateTimeOffset.FromUnixTimeSeconds(n)).ToString("o");

    public const string UnifiedHeaderPrefix = "anthropic-ratelimit-unified-";

    // Окна заголовков → имена окон rate_limit_event: одни и те же сущности, записанные по-разному.
    private static readonly (string Header, string LimitType)[] UnifiedWindows =
        [("5h", "five_hour"), ("7d", "seven_day")];

    /// <summary>
    /// Разбор заголовков <c>anthropic-ratelimit-unified-*</c> ответа Anthropic (их видит шлюз
    /// LLM, ADR-016) в те же <see cref="RateLimitMessage"/>, что даёт rate_limit_event CLI, —
    /// по одному на окно. Дальше оба пути идут в SubscriptionLimitRecorder.
    /// header — чтение заголовка без учёта регистра; нет заголовков — пустой список.
    /// </summary>
    /// Статус окна — из <c>{окно}-status</c>; нет его — общий <c>status</c>, но только для окна
    /// из <c>representative-claim</c>: общий статус говорит про то окно, что решило исход.
    /// Флага «идёт перерасход» в заголовках нет, он выводится: окно выбрано (utilization ≥ 1),
    /// а перерасход разрешён — запросы проходят, исчерпанием это не считается.
    public static IReadOnlyList<RateLimitMessage> FromUnifiedHeaders(Func<string, string?> header)
    {
        string? H(string name) => header(UnifiedHeaderPrefix + name) is { Length: > 0 } v ? v.Trim() : null;

        var unifiedStatus = H("status");
        var claim = H("representative-claim");
        var overageStatus = H("overage-status");
        var overageResetsAt = ParseUnix(H("overage-reset"));
        var overageDisabledReason = H("overage-disabled-reason");
        var overageAllowed = overageStatus is "allowed" or "allowed_warning";

        var result = new List<RateLimitMessage>();
        foreach (var (w, limitType) in UnifiedWindows)
        {
            var utilization = double.TryParse(H($"{w}-utilization"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var u) ? u : (double?)null;
            var status = H($"{w}-status") ?? (string.Equals(claim, limitType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(claim, w, StringComparison.OrdinalIgnoreCase) ? unifiedStatus : null);
            var resetsAt = ParseUnix(H($"{w}-reset"));
            if (utilization is null && status is null && resetsAt is null) continue;
            result.Add(new RateLimitMessage(limitType, resetsAt, status, utilization,
                IsUsingOverage: overageAllowed && utilization >= 1.0,
                overageStatus, overageResetsAt, overageDisabledReason));
        }
        return result;
    }

    // Сброс в заголовках — unix-время (сек/мс); на всякий случай принимаем и ISO-строку.
    private static string? ParseUnix(string? value)
    {
        if (value is null) return null;
        return long.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? UnixToIso(n) : value;
    }
}
