namespace ClaudeHomeServer.Services.Backup;

// Какие архивы удалять при ротации. Чистая логика над списком — тестируется без диска.
public static class BackupRotation
{
    public const int KeepDaily = 7;
    public const int KeepWeekly = 4;
    public const int KeepMonthly = 3;

    public record Candidate(string FileName, DateTime CreatedAt);

    /// <summary>
    /// Отобрать архивы на удаление: последние 7 дневных, 4 недельных и 3 месячных остаются.
    /// На вход подаются ТОЛЬКО свои архивы (чужой instanceId и нечитаемый sidecar
    /// отфильтровываются вызывающим — в общей облачной папке лежат архивы разных инстансов).
    /// </summary>
    public static List<string> SelectForDeletion(IEnumerable<Candidate> candidates)
    {
        // Свежие сверху: при равных «корзинах» оставляем самый новый архив периода
        var ordered = candidates.OrderByDescending(c => c.CreatedAt).ToList();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in ordered.Take(KeepDaily)) keep.Add(item.FileName);

        KeepFirstPerBucket(ordered, keep, KeepWeekly, WeekKey);
        KeepFirstPerBucket(ordered, keep, KeepMonthly, MonthKey);

        return ordered.Where(c => !keep.Contains(c.FileName)).Select(c => c.FileName).ToList();
    }

    private static void KeepFirstPerBucket(
        List<Candidate> ordered, HashSet<string> keep, int bucketCount, Func<DateTime, string> keyOf)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in ordered)
        {
            if (seen.Count >= bucketCount) break;
            var key = keyOf(item.CreatedAt);
            if (!seen.Add(key)) continue;
            keep.Add(item.FileName);
        }
    }

    private static string WeekKey(DateTime dt)
    {
        var week = System.Globalization.ISOWeek.GetWeekOfYear(dt);
        var year = System.Globalization.ISOWeek.GetYear(dt);
        return $"{year}-W{week:00}";
    }

    private static string MonthKey(DateTime dt) => $"{dt.Year}-{dt.Month:00}";
}
