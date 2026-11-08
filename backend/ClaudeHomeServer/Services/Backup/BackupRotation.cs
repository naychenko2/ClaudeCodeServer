namespace ClaudeHomeServer.Services.Backup;

// Какие архивы удалять при ротации. Чистая логика над списком — тестируется без диска.
public static class BackupRotation
{
    // Только последние снимки, без недельных и месячных корзин: архив весит единицы ГБ
    // (5 ГБ к 08.11.2026), и прежний GFS 7+4+3 держал в облаке до ~70 ГБ.
    public const int KeepLast = 3;

    public record Candidate(string FileName, DateTime CreatedAt);

    /// <summary>
    /// Отобрать архивы на удаление: остаются последние <see cref="KeepLast"/> по времени создания.
    /// На вход подаются ТОЛЬКО свои архивы (чужой instanceId и нечитаемый sidecar
    /// отфильтровываются вызывающим — в общей облачной папке лежат архивы разных инстансов).
    /// </summary>
    public static List<string> SelectForDeletion(IEnumerable<Candidate> candidates) =>
        candidates
            .OrderByDescending(c => c.CreatedAt)
            .Skip(KeepLast)
            .Select(c => c.FileName)
            .ToList();
}
