using System.Globalization;

namespace ClaudeHomeServer.Services.Llm;

// Счётчики jiffies строки «cpu» /proc/stat: total — сумма всех полей, idle — idle+iowait
// (поля 4 и 5 формата user nice system idle iowait …). Хранит вызывающий (Ш2c).
public sealed record CpuJiffies
{
    public long Total { get; init; }
    public long Idle { get; init; }
}

// Разбор /proc/uptime и строки «cpu» из /proc/stat по ТЕКСТУ, а не по пути: текст придёт
// и с локального диска, и из среза агента. Чистые функции, без состояния: prev для загрузки
// CPU хранит вызывающий (Ш2c). Недоверенный вход: null/пусто/мусор/отрицательные числа/
// переполнение long → null, без исключений.
public static class ProcStatReader
{
    // "/proc/uptime": "12345.67 999.0" → 12345 (первое число, вниз до целого).
    public static long? ParseUptimeS(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var token = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null) return null;
        var dot = token.IndexOf('.');
        if (dot >= 0) token = token[..dot]; // дробная секундная часть — вниз до целого
        if (token.Length == 0) return null;
        try
        {
            var v = long.Parse(token, NumberStyles.Integer, CultureInfo.InvariantCulture);
            return v >= 0 ? v : null;
        }
        catch
        {
            // Мусор или переполнение long.
            return null;
        }
    }

    // Строка «cpu» /proc/stat: "cpu  237419 103 134093 8935064 4544 0 4352 0 0 0" →
    // total = сумма всех полей, idle = idle+iowait. Строки «cpu0…» и остальное игнорируются;
    // нет строки «cpu», полей меньше 4 (нет idle), мусор/отрицательное/переполнение → null.
    public static CpuJiffies? ParseCpu(string? statText)
    {
        if (string.IsNullOrWhiteSpace(statText)) return null;
        foreach (var rawLine in statText.Split('\n'))
        {
            var parts = rawLine.TrimEnd('\r').Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || parts[0] != "cpu") continue;
            try
            {
                var vals = new long[parts.Length - 1];
                long total = 0;
                for (var i = 0; i < vals.Length; i++)
                {
                    vals[i] = long.Parse(parts[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture);
                    if (vals[i] < 0) return null;
                    total = checked(total + vals[i]);
                }
                // idle — поле 4 (0-й после «cpu»), iowait — поле 5 (в старых ядрах его нет → 0).
                var idle = checked(vals[3] + (vals.Length >= 5 ? vals[4] : 0L));
                return new CpuJiffies { Total = total, Idle = idle };
            }
            catch
            {
                // Мусор или переполнение long.
                return null;
            }
        }
        return null;
    }

    // Загрузка CPU, %: дельта jiffies двух соседних срезов (разрез 6199b268):
    // (Δtotal − Δidle) / Δtotal, целое вниз, 0…100 для реальных снимков (счётчики монотонны).
    // null: нет prev; Δtotal ≤ 0 (за интервал не было тиков или счётчик ушёл назад —
    // перезагрузка узла); переполнение long при умножении.
    public static int? CpuLoadPerc(CpuJiffies? prev, CpuJiffies? cur)
    {
        if (prev is null || cur is null) return null;
        var dTotal = cur.Total - prev.Total;
        if (dTotal <= 0) return null;
        try
        {
            var busy = checked(dTotal - (cur.Idle - prev.Idle));
            return (int)(checked(busy * 100) / dTotal);
        }
        catch
        {
            // Переполнение long при вычитании/умножении.
            return null;
        }
    }
}
