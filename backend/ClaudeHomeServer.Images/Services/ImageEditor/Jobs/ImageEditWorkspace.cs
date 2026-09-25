using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Versioning;

namespace ClaudeHomeServer.Services.Images.Editing;

// Рабочая папка сеансов правки: data/image-editor/{ownerId}/{jobId}/v{n}.{ext} (ADR-016,
// разделы 5 и 7). Это кеш на 7 дней, а весить он может гигабайты, поэтому в бэкап не едет
// (BackupPaths). Реестр задач в памяти теряется на рестарте, а уже скачанные варианты
// остаются здесь до чистки.
public sealed class ImageEditWorkspace(string root)
{
    public const string DirName = "image-editor";
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    public string Root { get; } = root;

    public static ImageEditWorkspace FromConfig(IConfiguration config)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        return new ImageEditWorkspace(Path.Combine(Path.GetDirectoryName(dataPath)!, DirName));
    }

    public void SaveVariant(string ownerId, string jobId, int variant, EditedImage image)
    {
        var dir = JobDir(ownerId, jobId);
        Directory.CreateDirectory(dir);
        var ext = ImageFormatSniffer.DetectExtension(image.Bytes) ?? ".png";
        File.WriteAllBytes(Path.Combine(dir, $"v{variant}{ext}"), image.Bytes);
    }

    public EditedImage? OpenVariant(string ownerId, string jobId, int variant)
    {
        var dir = JobDir(ownerId, jobId);
        if (!Directory.Exists(dir)) return null;
        var file = Directory.EnumerateFiles(dir, $"v{variant}.*").FirstOrDefault();
        if (file is null) return null;
        var bytes = File.ReadAllBytes(file);
        return new EditedImage(bytes, ContentTypeOf(ImageFormatSniffer.DetectExtension(bytes)));
    }

    // Чистка сеансов старше TTL; ошибки файловой системы не мешают запуску
    public void Sweep(DateTime nowUtc)
    {
        if (!Directory.Exists(Root)) return;
        try
        {
            foreach (var owner in Directory.EnumerateDirectories(Root))
            foreach (var job in Directory.EnumerateDirectories(owner))
                if (nowUtc - Directory.GetLastWriteTimeUtc(job) > Ttl)
                    Directory.Delete(job, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // id владельца и задачи — только из своих рук (Guid и claim), но всё равно без разделителей
    private string JobDir(string ownerId, string jobId) =>
        Path.Combine(Root, Safe(ownerId), Safe(jobId));

    private static string Safe(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment.Contains("..")
            || segment.IndexOfAny(['/', '\\', ':']) >= 0 || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Недопустимый идентификатор", nameof(segment));
        return segment;
    }

    private static string ContentTypeOf(string? ext) => ext switch
    {
        ".jpg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/png",
    };
}
