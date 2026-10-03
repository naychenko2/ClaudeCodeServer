using System.Globalization;

namespace ClaudeHomeServer.Services.ChatContext;

// Цена «Чем» человеческими единицами, как у фронта: «бесплатно», «$0.04 / шт.», «2 кр. / шт.».
// Общая для видов вертикалей: единицы поставщика (usd, credits, rub, free) и «за что» — одна таблица
public static class ContextPriceText
{
    public static string Format(double amount, string unit, string per)
    {
        if (amount == 0 || unit.Equals("free", StringComparison.OrdinalIgnoreCase)) return "бесплатно";
        var number = Number(amount);
        var money = unit.ToLowerInvariant() switch
        {
            "usd" => $"${number}",
            "credits" => $"{number} кр.",
            "rub" => $"{number} ₽",
            _ => $"{number} {unit}",
        };
        return $"{money} / {PerName(per)}";
    }

    // Три значащие цифры без показателя степени: 0.018 остаётся 0.018, 0.00009 не превращается в 0
    private static string Number(double amount)
    {
        var digits = Math.Clamp(2 - (int)Math.Floor(Math.Log10(Math.Abs(amount))), 0, 12);
        return Math.Round(amount, digits).ToString("0.############", CultureInfo.InvariantCulture);
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
