namespace ClaudeHomeServer.Services.AudioEditor.Jobs;

// Рабочая папка задач модуля «Звук»: data/audio-editor/{ownerId}/{jobId}/… (ADR-021 §2, как у
// картинок). Кеш на 7 дней весом до гигабайт, поэтому в бэкап не едет (BackupPaths по
// AudioEditorPaths.WorkspaceDirName). Чистку зовёт исполнитель задач при каждом запуске, как у картинок.
public sealed class AudioEditWorkspace(string root)
{
    public const string DirName = AudioEditorPaths.WorkspaceDirName;
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    public string Root { get; } = root;

    public static AudioEditWorkspace FromConfig(IConfiguration config)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        return new AudioEditWorkspace(Path.Combine(Path.GetDirectoryName(dataPath)!, DirName));
    }

    // Задачи, на которые ссылаются версии нитей владельца: живут столько же, сколько нить, а не TTL.
    // Ставит регистрация модуля поверх AudioThreadStore; null — удерживать нечего
    public Func<string, IReadOnlySet<string>>? RetainedJobs { get; set; }

    // Папка задачи; создаётся вызывающим при записи
    public string JobDir(string ownerId, string jobId) =>
        Path.Combine(Root, Safe(ownerId), Safe(jobId));

    // Чистка задач старше TTL, кроме удержанных нитями; ошибки файловой системы не мешают запуску
    public void Sweep(DateTime nowUtc)
    {
        if (!Directory.Exists(Root)) return;
        try
        {
            foreach (var owner in Directory.EnumerateDirectories(Root))
            {
                IReadOnlySet<string>? keep = null;
                foreach (var job in Directory.EnumerateDirectories(owner))
                {
                    if (nowUtc - Directory.GetLastWriteTimeUtc(job) <= Ttl) continue;
                    keep ??= RetainedJobs?.Invoke(Path.GetFileName(owner)) ?? new HashSet<string>();
                    if (!keep.Contains(Path.GetFileName(job))) Directory.Delete(job, recursive: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // id владельца и задачи — только из своих рук (Guid и claim), но всё равно без разделителей
    private static string Safe(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment.Contains("..")
            || segment.IndexOfAny(['/', '\\', ':']) >= 0 || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Недопустимый идентификатор", nameof(segment));
        return segment;
    }
}
