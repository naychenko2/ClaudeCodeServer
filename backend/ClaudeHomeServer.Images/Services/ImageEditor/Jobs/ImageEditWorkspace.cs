using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Versioning;

namespace ClaudeHomeServer.Services.Images.Editing;

// Рабочая папка сеансов правки: data/image-editor/{ownerId}/{jobId}/v{n}.{ext} (ADR-017,
// разделы 5 и 7). Это кеш на 7 дней, а весить он может гигабайты, поэтому в бэкап не едет
// (BackupPaths). Реестр задач в памяти теряется на рестарте, а уже скачанные варианты
// остаются здесь до чистки.
//
// Шаги истории (ADR-018 §9) — там же: {ownerId}/{editId}/steps/{stepId}.{ext} плюс
// {stepId}.json с метаданными. editId — лента правки: её задаёт первый шаг, потомки живут
// в той же папке. TTL тот же: запись шага обновляет время папки ленты, чистка её не снесёт.
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

    public const string StepsDirName = "steps";

    private static readonly System.Text.Json.JsonSerializerOptions StepJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    public void SaveStep(string ownerId, ImageEditStep step, byte[] bytes)
    {
        var editDir = JobDir(ownerId, step.EditId);
        var dir = Path.Combine(editDir, StepsDirName);
        Directory.CreateDirectory(dir);
        var id = Safe(step.StepId);
        var ext = ImageFormatSniffer.DetectExtension(bytes) ?? ".png";
        File.WriteAllBytes(Path.Combine(dir, id + ext), bytes);
        // Метаданные последними: шаг без картинки не должен найтись
        File.WriteAllText(Path.Combine(dir, id + ".json"), System.Text.Json.JsonSerializer.Serialize(step, StepJson));
        try { Directory.SetLastWriteTimeUtc(editDir, DateTime.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Шаг владельца по id; null — нет, истёк или id не похож на наш
    public (ImageEditStep Step, EditedImage Image)? OpenStep(string ownerId, string stepId)
    {
        if (!IsStepId(stepId)) return null;
        var ownerDir = Path.Combine(Root, Safe(ownerId));
        if (!Directory.Exists(ownerDir)) return null;
        try
        {
            foreach (var edit in Directory.EnumerateDirectories(ownerDir))
            {
                var meta = Path.Combine(edit, StepsDirName, stepId + ".json");
                if (!File.Exists(meta)) continue;
                var step = System.Text.Json.JsonSerializer.Deserialize<ImageEditStep>(File.ReadAllText(meta), StepJson);
                var file = Directory.EnumerateFiles(Path.Combine(edit, StepsDirName), stepId + ".*")
                    .FirstOrDefault(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
                if (step is null || file is null) return null;
                var bytes = File.ReadAllBytes(file);
                return (step, new EditedImage(bytes, ContentTypeOf(ImageFormatSniffer.DetectExtension(bytes))));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        return null;
    }

    public static string NewStepId() => Guid.NewGuid().ToString("N");

    // id шага — только наш Guid "N": он же имя файла, поэтому без белого списка никак
    public static bool IsStepId(string? id) =>
        id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

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

// Шаг истории правки. Kind — ImageEditStepKinds.*; Parent — предыдущий шаг ленты (null — первый
// шаг от файла проекта или вариант задачи без базового шага); SourcePath — файл проекта, с
// которого началась лента; JobId/Variant — у применённого варианта
public sealed record ImageEditStep(
    string StepId,
    string EditId,
    string ProjectId,
    string? Parent,
    string Kind,
    int Width,
    int Height,
    long Bytes,
    DateTime CreatedAt,
    string? SourcePath = null,
    string? JobId = null,
    int? Variant = null);

public static class ImageEditStepKinds
{
    public const string Transform = "transform";
    public const string Variant = "variant";
}
