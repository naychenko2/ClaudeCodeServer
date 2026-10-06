namespace ClaudeHomeServer.Services.VideoEditor.Jobs;

// Рабочая папка задач модуля «Видео»: data/video-editor/{ownerId}/{jobId}/{variant}/clip.mp4 (ADR-022 §2,
// как у звука). Кеш на 7 дней весом до сотен МБ, поэтому в бэкап не едет (BackupPaths по
// VideoEditorPaths.WorkspaceDirName). Чистку зовёт исполнитель задач при каждом запуске. Версии живых
// нитей чистка не трогает (RetainedJobs).
public sealed class VideoEditWorkspace(string root)
{
    public const string DirName = VideoEditorPaths.WorkspaceDirName;
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(7);
    private const string ClipName = "clip";

    public string Root { get; } = root;

    public static VideoEditWorkspace FromConfig(IConfiguration config)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        return new VideoEditWorkspace(Path.Combine(Path.GetDirectoryName(dataPath)!, DirName));
    }

    // Задачи, на которые ссылаются версии нитей владельца: живут столько же, сколько нить, а не TTL.
    // Ставит регистрация модуля поверх VideoThreadStore; null — удерживать нечего
    public Func<string, IReadOnlySet<string>>? RetainedJobs { get; set; }

    public string JobDir(string ownerId, string jobId) => Path.Combine(Root, Safe(ownerId), Safe(jobId));

    // Клип варианта: {jobId}/{variant}/clip.mp4. Расширение от драйвера — с точкой, латиница и цифры, не
    // длиннее 10; иначе .mp4. Возвращает путь файла
    public string SaveClip(string ownerId, string jobId, int variant, byte[] bytes, string? extension)
    {
        var dir = Path.Combine(JobDir(ownerId, jobId), variant.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ClipName + SafeExtension(extension));
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // Файл клипа варианта; нет — null (чистка по TTL или ещё не готов)
    public string? FindClip(string ownerId, string jobId, int variant)
    {
        var dir = Path.Combine(JobDir(ownerId, jobId), variant.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, ClipName + ".*").FirstOrDefault() : null;
    }

    private static string SafeExtension(string? extension) =>
        extension is { Length: > 1 and <= 10 } e && e[0] == '.' && e[1..].All(char.IsAsciiLetterOrDigit)
            ? e.ToLowerInvariant()
            : ".mp4";

    // ── Кадры «С компьютера» личного чата: {ownerId}/frames/{id}.{png|jpg|webp} ────────────────
    // Относительный путь кадра (его хранит FrameRef вида file) — frames/<32 hex>.<ext>; иное имя не принимается,
    // поэтому ".." и чужие папки отсекаются шаблоном, а не чисткой строки
    public const string FramesDirName = "frames";
    private static readonly System.Text.RegularExpressions.Regex FrameRefPattern =
        new(@"^frames/[0-9a-f]{32}\.(png|jpg|webp)\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool IsFrameRef(string? path) => path is not null && FrameRefPattern.IsMatch(path);

    // Человеческое имя исходного файла кладётся рядом: {id}.name (UTF-8) — подпись «кадр-а.png» вместо id
    private const string NameSidecarExtension = ".name";
    private const int MaxFileNameLength = 120;

    public string SaveFrame(string ownerId, byte[] bytes, string extension, string? fileName = null)
    {
        var dir = Path.Combine(Root, Safe(ownerId), FramesDirName);
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid().ToString("N");
        File.WriteAllBytes(Path.Combine(dir, id + extension), bytes);
        if (CleanFileName(fileName) is { } clean)
            File.WriteAllText(Path.Combine(dir, id + NameSidecarExtension), clean);
        return $"{FramesDirName}/{id}{extension}";
    }

    // Только имя без пути и управляющих символов; пусто — null
    public static string? CleanFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var name = new string(Path.GetFileName(raw.Replace('\\', '/')).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) return null;
        return name.Length <= MaxFileNameLength ? name : name[..MaxFileNameLength];
    }

    // Человеческое имя кадра; нет — null
    public string? FindFrameName(string ownerId, string? relative)
    {
        if (!IsFrameRef(relative)) return null;
        var stem = Path.GetFileNameWithoutExtension(relative!["frames/".Length..]);
        var full = Path.Combine(Root, Safe(ownerId), FramesDirName, stem + NameSidecarExtension);
        return File.Exists(full) ? CleanFileName(File.ReadAllText(full)) : null;
    }

    // Полный путь кадра владельца; путь не по шаблону или файла нет — null
    public string? FindFrame(string ownerId, string? relative)
    {
        if (!IsFrameRef(relative)) return null;
        var full = Path.Combine(Root, Safe(ownerId), FramesDirName, relative!["frames/".Length..]);
        return File.Exists(full) ? full : null;
    }

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
                    if (Path.GetFileName(job) == FramesDirName)
                    {
                        SweepFrames(job, nowUtc);
                        continue;
                    }
                    if (nowUtc - Directory.GetLastWriteTimeUtc(job) <= Ttl) continue;
                    keep ??= RetainedJobs?.Invoke(Path.GetFileName(owner)) ?? new HashSet<string>();
                    if (!keep.Contains(Path.GetFileName(job))) Directory.Delete(job, recursive: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Кадр живёт, пока на него может ссылаться сцена: в десять раз дольше клипов
    private static void SweepFrames(string dir, DateTime nowUtc)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
            if (nowUtc - File.GetLastWriteTimeUtc(file) > Ttl * 10) File.Delete(file);
    }

    private static string Safe(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment.Contains("..")
            || segment.IndexOfAny(['/', '\\', ':']) >= 0 || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Недопустимый идентификатор", nameof(segment));
        return segment;
    }
}
