namespace ClaudeHomeServer.Models;

// Дополнительная подписка Claude (секция конфига "ClaudeSubscriptions": словарь key → конфиг).
// Позволяет подключить второй/третий аккаунт Claude на одном сервере для балансировки
// нагрузки и failover при исчерпании лимита.
public class ClaudeSubscriptionConfig
{
    // Ключ подписки из словаря конфига (напр. "my-second"). Заполняется реестром при загрузке.
    public string Key { get; set; } = "";

    // Человекочитаемое имя (для логов и отладки)
    public string DisplayName { get; set; } = "";

    // OAuth-токен от claude setup-token на аккаунте второй подписки.
    // Получить: запустить `claude setup-token` на машине с браузером, залогиненной в нужный аккаунт.
    public string OAuthToken { get; set; } = "";

    // Альтернатива OAuth: API-ключ (sk-ant-...). Если задан — используется как
    // ANTHROPIC_AUTH_TOKEN в изолированном профиле, без .credentials.json.
    // Приоритет: ApiKey > OAuthToken.
    public string ApiKey { get; set; } = "";

    // Аккаунту доступны Opus-модели. false — план без Opus (например, Pro):
    // пул не отдаёт такому аккаунту чаты с пином opus-тира, иначе CLI падает
    // «There's an issue with the selected model (opus)».
    public bool SupportsOpus { get; set; } = true;

    // Тариф подписки: "pro" | "max" | "max5" | "max20" (варианты записи нормализуются).
    // Пул отдаёт приоритет более высоким тарифам среди доступных аккаунтов; при равенстве
    // тарифа — наименее загруженному. Пусто/не распознано — тариф не задан (низший приоритет).
    public string Tier { get; set; } = "";

    // Провайдер включён при наличии хотя бы одного способа аутентификации
    public bool Enabled => !string.IsNullOrWhiteSpace(OAuthToken) || !string.IsNullOrWhiteSpace(ApiKey);
}

// Нормализация и ранжирование тарифов подписки для приоритизации в пуле.
public static class ClaudeSubscriptionTier
{
    // Ранг тарифа (больше = выше приоритет). 0 — тариф не задан/не распознан.
    public static int Rank(string? tier) => Normalize(tier) switch
    {
        "max20" => 4,
        "max5" => 3,
        "max" => 2,
        "pro" => 1,
        _ => 0,
    };

    // Ярлык тарифа для UI ("Max 20×", "Max 5×", "Max", "Pro"); null — не задан/не распознан.
    public static string? Label(string? tier) => Normalize(tier) switch
    {
        "max20" => "Max 20×",
        "max5" => "Max 5×",
        "max" => "Max",
        "pro" => "Pro",
        _ => null,
    };

    // Свести варианты записи к канону: "Max 20x"/"max_20x"/"max20"/"20x" → "max20",
    // "Max 5x"/"max5" → "max5", "max" → "max", "pro" → "pro".
    private static string Normalize(string? tier)
    {
        if (string.IsNullOrWhiteSpace(tier)) return "";
        var t = new string(tier.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (t.Contains("20")) return "max20";
        if (t.Contains("max") && t.Contains('5')) return "max5";
        if (t.Contains("max")) return "max";
        if (t.Contains("pro")) return "pro";
        return "";
    }
}
