using System.Globalization;

namespace ClaudeHomeServer.Services.ChatContext;

// Цена «Чем» человеческими единицами, как у фронта: «бесплатно», «$0.04 / шт.», «2 кр. / шт.».
// Общая для видов вертикалей: единицы поставщика (usd, credits, rub, free) и «за что» — одна таблица
public static class ContextPriceText
{
    public static string Format(double amount, string unit, string per)
    {
        if (amount == 0 || unit.Equals("free", StringComparison.OrdinalIgnoreCase)) return "бесплатно";
        var number = amount.ToString("0.##", CultureInfo.InvariantCulture);
        var money = unit.ToLowerInvariant() switch
        {
            "usd" => $"${number}",
            "credits" => $"{number} кр.",
            "rub" => $"{number} ₽",
            _ => $"{number} {unit}",
        };
        return $"{money} / {PerName(per)}";
    }

    private static string PerName(string per) => per.Trim().ToLowerInvariant() switch
    {
        "image" or "images" => "шт.",
        "megapixel" or "megapixels" => "Мпикс.",
        "sec" or "second" or "seconds" => "с",
        "min" or "minute" or "minutes" => "мин",
        "run" or "generation" or "generations" or "request" or "requests" => "запуск",
        "chars" or "char" or "character" or "characters" => "симв.",
        var other => other,
    };
}
