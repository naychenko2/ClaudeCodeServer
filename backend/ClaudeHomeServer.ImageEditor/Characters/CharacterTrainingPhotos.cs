using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.ImageEditor.Versioning;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Services.ImageEditor;

// Обучающие фото персонажа для LoRA (ADR-026, раздел 5): data/lora-training/<owner>/<project>/<slug>/
// <sha16>.<ext> — вне проекта и вне его git. Имя на диске — первые 16 hex SHA-256 содержимого,
// имён из запроса здесь нет вовсе; путь собирается только через SafePath.Join от корня data.
public sealed partial class CharacterTrainingPhotos(IConfiguration config) : IProjectTrainingPhotoCleaner
{
    public const int MinPhotos = 20, MaxPhotos = 50, MinShortSide = 512;
    public const int MaxPhotoMb = 8;
    public const string Folder = ImageEditorPaths.TrainingDirName;

    [GeneratedRegex(@"^[0-9a-f]{16}\.(jpg|png|webp)\z")]
    private static partial Regex NamePattern();

    private readonly object _lock = new();

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    public IReadOnlyList<TrainingPhoto> List(string ownerId, string projectId, string slug)
    {
        var dir = Dir(ownerId, projectId, slug);
        if (dir is null || !Directory.Exists(dir)) return [];
        var result = new List<TrainingPhoto>();
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (!IsValidName(name) || IsLink(file)) continue;
            var (w, h) = ImageDimensions.Read(ReadHeader(file)) ?? (0, 0);
            result.Add(new TrainingPhoto(name, new FileInfo(file).Length, w, h));
        }
        return [.. result.OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    // Размеры берём по первым байтам: PNG/WebP — в первых 32, JPEG — SOF в пределах заголовков
    private const int HeaderBytes = 64 * 1024;

    private static byte[] ReadHeader(string path)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[(int)Math.Min(fs.Length, HeaderBytes)];
        fs.ReadExactly(buf);
        return buf;
    }

    // Удаляет все фото персонажа; нет каталога — не ошибка
    public void Purge(string ownerId, string projectId, string slug)
    {
        var dir = Dir(ownerId, projectId, slug);
        DeleteDir(dir);
    }

    // Удаляет все фото проекта владельца (каскад удаления проекта); нет каталога — не ошибка
    public void PurgeProject(string ownerId, string projectId)
    {
        var dir = ProjectDir(ownerId, projectId);
        DeleteDir(dir);
    }

    private void DeleteDir(string? dir)
    {
        if (dir is null) return;
        lock (_lock)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    public TrainingPhotosAddResult Add(string ownerId, string projectId, string slug, IReadOnlyList<PhotoUpload> uploads)
    {
        var added = new List<TrainingPhoto>();
        var rejected = new List<TrainingPhotoRejection>();
        var dir = Dir(ownerId, projectId, slug);
        if (dir is null)
        {
            // Каталог не собрался: slug, владелец или проект некорректны — отказ «персонаж», а не «формат»
            rejected.AddRange(uploads.Select(u => new TrainingPhotoRejection(u.FileName, "character")));
            return new TrainingPhotosAddResult(added, rejected);
        }

        lock (_lock)
        {
            Directory.CreateDirectory(dir);
            var count = Directory.EnumerateFiles(dir).Count(f => IsValidName(Path.GetFileName(f)));
            foreach (var upload in uploads)
            {
                var bytes = upload.Bytes;
                var ext = ImageFormatSniffer.DetectExtension(bytes);
                if (ext is not (".jpg" or ".png" or ".webp") || ImageDimensions.Read(bytes) is not { } size)
                {
                    rejected.Add(new TrainingPhotoRejection(upload.FileName, "format"));
                    continue;
                }
                if (bytes.LongLength > MaxPhotoMb * 1024L * 1024L)
                {
                    rejected.Add(new TrainingPhotoRejection(upload.FileName, "too_big"));
                    continue;
                }
                if (Math.Min(size.Width, size.Height) < MinShortSide)
                {
                    rejected.Add(new TrainingPhotoRejection(upload.FileName, "too_small"));
                    continue;
                }
                var name = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16] + ext;
                var path = SafePath.Join(dir, name);
                if (File.Exists(path))
                {
                    rejected.Add(new TrainingPhotoRejection(upload.FileName, "duplicate"));
                    continue;
                }
                if (count >= MaxPhotos)
                {
                    rejected.Add(new TrainingPhotoRejection(upload.FileName, "limit"));
                    continue;
                }
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, path, overwrite: true);
                count++;
                added.Add(new TrainingPhoto(name, bytes.LongLength, size.Width, size.Height));
            }
        }
        return new TrainingPhotosAddResult(added, rejected);
    }

    public bool Remove(string ownerId, string projectId, string slug, string name)
    {
        var path = PathOf(ownerId, projectId, slug, name);
        if (path is null) return false;
        lock (_lock) File.Delete(path);
        return true;
    }

    public string? PathOf(string ownerId, string projectId, string slug, string name)
    {
        if (!IsValidName(name)) return null;
        var dir = Dir(ownerId, projectId, slug);
        if (dir is null) return null;
        var path = SafePath.Join(dir, name);
        return File.Exists(path) && !IsLink(path) ? path : null;
    }

    // Имена — хеши содержимого, поэтому порядок загрузки на результат не влияет
    public static string PhotosHash(IEnumerable<string> names)
    {
        var joined = string.Join('\n', names.OrderBy(n => n, StringComparer.Ordinal));
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    // Не Guid владельца/проекта или кривой slug — null (fail-closed), а не исключение
    private string? Dir(string ownerId, string projectId, string slug)
    {
        if (!CharacterStore.IsValidSlug(slug)) return null;
        return ProjectDir(ownerId, projectId) is { } projectDir ? SafePath.Join(projectDir, slug) : null;
    }

    private string? ProjectDir(string ownerId, string projectId)
    {
        string owner, project;
        try { (owner, project) = (LoraNamespace.OwnerKey(ownerId), LoraNamespace.OwnerKey(projectId)); }
        catch (ArgumentException) { return null; }
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        var dataDir = Path.GetDirectoryName(dataPath)!;
        return SafePath.Join(dataDir, Path.Combine(Folder, owner, project));
    }

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;
}

public sealed record PhotoUpload(string FileName, byte[] Bytes);
public sealed record TrainingPhoto(string Name, long Bytes, int Width, int Height);
public sealed record TrainingPhotosAddResult(IReadOnlyList<TrainingPhoto> Added, IReadOnlyList<TrainingPhotoRejection> Rejected);
public sealed record TrainingPhotoRejection(string FileName, string Reason);
