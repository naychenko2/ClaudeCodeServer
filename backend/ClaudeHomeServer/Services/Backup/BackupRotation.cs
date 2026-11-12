namespace ClaudeHomeServer.Services.Backup;

// Какие архивы удалять при ротации. Чистая логика над списком — тестируется без диска.
public static class BackupRotation
{
    // Только последние снимки, без недельных и месячных корзин: архив весит единицы ГБ
    // (5 ГБ к 08.11.2026), и прежний GFS 7+4+3 держал в облаке до ~70 ГБ.
    public const int KeepLast = 3;

    public record Candidate(string FileName, DateTime CreatedAt);

    /// <summary>
    /// Отобрать архивы на удаление: остаются не более <see cref="KeepLast"/> файлов —
    /// по одному (самому свежему) архиву на каждый из последних <see cref="KeepLast"/> дней.
    /// Дневная корзина обязательна: выкатки и ручные снимки дают по несколько архивов в сутки
    /// (6 на 24.10.2026, по 8 ГБ), и счёт по последним файлам оставил бы три снимка одного дня,
    /// вытеснив из окна всю историю глубже вчерашнего.
    /// На вход подаются ТОЛЬКО свои архивы (чужой instanceId и нечитаемый sidecar
    /// отфильтровываются вызывающим — в общей облачной папке лежат архивы разных инстансов).
    /// </summary>
    public static List<string> SelectForDeletion(IEnumerable<Candidate> candidates)
    {
        // Свежие сверху: при равных «корзинах» оставляем самый новый архив периода
        var ordered = candidates.OrderByDescending(c => c.CreatedAt).ToList();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        KeepFirstPerBucket(ordered, keep, KeepLast, DayKey);

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

    private static string DayKey(DateTime dt) => dt.ToString("yyyy-MM-dd");
}
