using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Versioning;

namespace ClaudeHomeServer.Services.Images.Editing;

// Персонажи проекта на диске (ADR-017, раздел 10): characters/<slug>/character.json и
// 3–10 фото лица. Владение проектом проверяет контроллер, сюда приходит уже корень.
//
// Граница проекта держится в трёх местах сразу:
// - slug из маршрута проходит только по белому списку [a-z0-9-] — «../x» здесь просто
//   «нет такого персонажа», а не путь;
// - каждый путь собирается через SafePath.Join;
// - папка characters/, папка персонажа и файл фото не могут быть символической ссылкой:
//   SafePath сравнивает строки и ссылку наружу не видит.
// Фото называем сами (face-01.jpg), имя файла из запроса на диск не попадает вовсе.
public static partial class CharacterStore
{
    public const string Folder = "characters";
    public const string ManifestFile = "character.json";
    public const int MinPhotos = 3;
    public const int MaxPhotos = 10;
    public const int MaxPhotoMb = 8;
    // Сколько фото уходит в запрос генерации: primary и два ракурса, остальное место —
    // исходнику и другим образцам
    public const int PhotosPerRequest = 3;
    private const int MaxSlugChars = 48;

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$")]
    private static partial Regex SlugPattern();

    public static bool IsValidSlug(string? slug) => slug is not null && SlugPattern().IsMatch(slug);

    public static string SlugFromName(string name)
    {
        var slug = Slugifier.Slugify(name, maxChars: MaxSlugChars);
        return IsValidSlug(slug) ? slug : "character";
    }

    public static IReadOnlyList<CharacterManifest> List(string root)
    {
        var folder = FolderPath(root);
        if (folder is null || !Directory.Exists(folder)) return [];
        var result = new List<CharacterManifest>();
        foreach (var dir in Directory.EnumerateDirectories(folder))
        {
            var slug = Path.GetFileName(dir);
            if (Get(root, slug) is { } manifest) result.Add(manifest);
        }
        return [.. result.OrderBy(m => m.CreatedAt).ThenBy(m => m.Slug, StringComparer.Ordinal)];
    }

    public static CharacterManifest? Get(string root, string slug)
    {
        var dir = CharacterDir(root, slug);
        if (dir is null) return null;
        var file = Path.Combine(dir, ManifestFile);
        if (!File.Exists(file) || IsLink(file)) return null;
        var manifest = CharacterManifest.Parse(File.ReadAllText(file));
        if (manifest is null) return null;
        // Имя папки — источник правды: переименованная руками папка не должна вести в чужую
        manifest.Slug = slug;
        return manifest;
    }

    public static ImageEditCallResult<CharacterChange> Create(string root, CharacterDraft draft, DateTime now)
    {
        var name = draft.Name?.Trim() ?? "";
        if (name.Length == 0) return Invalid("Укажите имя персонажа");
        if (draft.Photos.Count is < MinPhotos or > MaxPhotos)
            return Invalid($"Нужно от {MinPhotos} до {MaxPhotos} фото");
        if (CheckPhotos(draft.Photos) is { } bad) return Invalid(bad);

        var folder = FolderPath(root);
        if (folder is null) return Invalid("Папка персонажей недоступна");
        Directory.CreateDirectory(folder);

        // Существующую папку не перетираем никогда: занятый slug → anya-2, anya-3
        var baseSlug = SlugFromName(name);
        string slug = baseSlug, dir;
        for (var n = 2; ; n++)
        {
            dir = CharacterDir(root, slug) ?? "";
            if (dir.Length > 0 && !Directory.Exists(dir) && !File.Exists(dir)) break;
            if (n > 999) return Invalid("Не удалось подобрать свободное имя папки");
            slug = $"{TrimForSuffix(baseSlug, n)}-{n}";
        }
        Directory.CreateDirectory(dir);

        var manifest = new CharacterManifest
        {
            Name = name,
            Slug = slug,
            Description = Normalize(draft.Description),
            CreatedAt = now,
            Providers = new() { [CharacterManifest.HiggsfieldProvider] = CharacterManifest.EmptyHiggsfield() },
        };
        var written = WritePhotos(root, slug, dir, manifest, draft.Photos);
        EnsurePrimary(manifest);
        WriteNew(Path.Combine(dir, ManifestFile), System.Text.Encoding.UTF8.GetBytes(manifest.Serialize()));
        written.Add(Rel(slug, ManifestFile));
        return ImageEditCallResult<CharacterChange>.Ok(new CharacterChange(manifest, written, []));
    }

    public static ImageEditCallResult<CharacterChange>? Update(string root, string slug, CharacterPatch patch)
    {
        var manifest = Get(root, slug);
        var dir = CharacterDir(root, slug);
        if (manifest is null || dir is null) return null;

        if (patch.Name is { } newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) return Invalid("Укажите имя персонажа");
            manifest.Name = newName.Trim();
        }
        if (patch.Description is not null) manifest.Description = Normalize(patch.Description);

        var remove = patch.RemovePhotos.ToHashSet(StringComparer.Ordinal);
        if (remove.Any(f => manifest.Photos.All(p => p.File != f)))
            return Invalid("Такого фото у персонажа нет");
        var remaining = manifest.Photos.Count - remove.Count + patch.AddPhotos.Count;
        if (remaining is < MinPhotos or > MaxPhotos)
            return Invalid($"Нужно от {MinPhotos} до {MaxPhotos} фото");
        if (CheckPhotos(patch.AddPhotos) is { } bad) return Invalid(bad);
        if (patch.PrimaryPhoto is { } primary
            && (remove.Contains(primary) || manifest.Photos.All(p => p.File != primary)))
            return Invalid("Главное фото не найдено");

        var deleted = new List<string>();
        foreach (var file in remove)
        {
            var full = PhotoPath(dir, file);
            if (full is not null && File.Exists(full) && !IsLink(full)) File.Delete(full);
            deleted.Add(Rel(slug, file));
        }
        manifest.Photos.RemoveAll(p => remove.Contains(p.File));

        var written = WritePhotos(root, slug, dir, manifest, patch.AddPhotos);
        if (patch.PrimaryPhoto is { } chosen)
            manifest.Photos = [.. manifest.Photos.Select(p => p with { Primary = p.File == chosen })];
        EnsurePrimary(manifest);

        // Манифест перезаписывается целиком через временный файл: оборванная запись не
        // должна оставить персонажа без character.json
        var target = Path.Combine(dir, ManifestFile);
        var temp = target + ".tmp";
        File.WriteAllText(temp, manifest.Serialize());
        File.Move(temp, target, overwrite: true);
        written.Add(Rel(slug, ManifestFile));
        return ImageEditCallResult<CharacterChange>.Ok(new CharacterChange(manifest, written, deleted));
    }

    // false — персонажа нет. Удаляется ровно папка персонажа внутри characters/.
    public static bool Delete(string root, string slug)
    {
        if (Get(root, slug) is null) return false;
        var dir = CharacterDir(root, slug);
        if (dir is null) return false;
        Directory.Delete(dir, recursive: true);
        return true;
    }

    // Фото отдаётся, только если оно перечислено в манифесте персонажа
    public static EditedImage? OpenPhoto(string root, string slug, string file)
    {
        var manifest = Get(root, slug);
        var dir = CharacterDir(root, slug);
        if (manifest is null || dir is null || manifest.Photos.All(p => p.File != file)) return null;
        var full = PhotoPath(dir, file);
        if (full is null || !File.Exists(full) || IsLink(full)) return null;
        var bytes = File.ReadAllBytes(full);
        return new EditedImage(bytes, ContentTypeOf(ImageFormatSniffer.DetectExtension(bytes)));
    }

    // Персонаж для запроса: primary первым, затем остальные в порядке манифеста
    public static CharacterForRequest? ForRequest(string root, string slug, int maxPhotos = PhotosPerRequest)
    {
        var manifest = Get(root, slug);
        var dir = CharacterDir(root, slug);
        if (manifest is null || dir is null) return null;

        var photos = new List<ReferenceImage>();
        foreach (var photo in manifest.Photos.OrderByDescending(p => p.Primary))
        {
            if (photos.Count >= maxPhotos) break;
            var full = PhotoPath(dir, photo.File);
            if (full is null || !File.Exists(full) || IsLink(full)) continue;
            var bytes = File.ReadAllBytes(full);
            var ext = ImageFormatSniffer.DetectExtension(bytes);
            if (ext is null) continue;
            photos.Add(new ReferenceImage(bytes, ContentTypeOf(ext), ReferenceRole.Character, manifest.Name));
        }
        if (photos.Count == 0) return null;
        return new CharacterForRequest(new CharacterRef(manifest.Slug, manifest.Name, manifest.Description), photos);
    }

    // Абсолютный путь папки персонажа; null — slug не проходит белый список, путь вышел за
    // корень или по дороге символическая ссылка
    public static string? CharacterDir(string root, string slug)
    {
        if (!IsValidSlug(slug)) return null;
        var folder = FolderPath(root);
        if (folder is null) return null;
        var dir = Join(root, $"{Folder}/{slug}");
        if (dir is null || IsLink(dir)) return null;
        return dir;
    }

    private static string? FolderPath(string root)
    {
        var folder = Join(root, Folder);
        return folder is null || IsLink(folder) ? null : folder;
    }

    private static string? PhotoPath(string dir, string file)
    {
        if (string.IsNullOrEmpty(file) || Path.GetFileName(file) != file || file == ManifestFile) return null;
        return Join(dir, file);
    }

    private static string? Join(string root, string relative)
    {
        try { return SafePath.Join(root, relative); }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool IsLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.Exists && info.LinkTarget is not null;
    }

    private static List<string> WritePhotos(
        string root, string slug, string dir, CharacterManifest manifest, IReadOnlyList<CharacterPhotoUpload> uploads)
    {
        var written = new List<string>();
        var next = NextIndex(manifest);
        foreach (var upload in uploads)
        {
            var ext = ImageFormatSniffer.DetectExtension(upload.Bytes)!;
            string file;
            do file = $"face-{next++:00}{ext}";
            while (File.Exists(Path.Combine(dir, file)));
            WriteNew(PhotoPath(dir, file) ?? throw new UnauthorizedAccessException("Путь фото вне папки персонажа"),
                upload.Bytes);
            manifest.Photos.Add(new CharacterPhoto(file, false, Normalize(upload.Angle)));
            written.Add(Rel(slug, file));
        }
        return written;
    }

    private static int NextIndex(CharacterManifest manifest)
    {
        var max = 0;
        foreach (var photo in manifest.Photos)
        {
            var name = Path.GetFileNameWithoutExtension(photo.File);
            if (name.StartsWith("face-", StringComparison.Ordinal) && int.TryParse(name[5..], out var n) && n > max)
                max = n;
        }
        return max + 1;
    }

    private static void EnsurePrimary(CharacterManifest manifest)
    {
        if (manifest.Photos.Count == 0 || manifest.Photos.Any(p => p.Primary)) return;
        manifest.Photos[0] = manifest.Photos[0] with { Primary = true };
    }

    private static string? CheckPhotos(IReadOnlyList<CharacterPhotoUpload> photos)
    {
        foreach (var photo in photos)
        {
            if (photo.Bytes.Length > MaxPhotoMb * 1024L * 1024L) return $"Фото больше {MaxPhotoMb} МБ";
            if (ImageFormatSniffer.DetectExtension(photo.Bytes) is not (".jpg" or ".png" or ".webp"))
                return "Фото должно быть в формате JPEG, PNG или WebP";
        }
        return null;
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.Write(bytes);
    }

    private static string TrimForSuffix(string slug, int n)
    {
        var room = MaxSlugChars - n.ToString().Length - 1;
        return slug.Length > room ? slug[..room].TrimEnd('-') : slug;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Rel(string slug, string file) => $"{Folder}/{slug}/{file}";

    private static string ContentTypeOf(string? ext) => ext switch
    {
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "application/octet-stream",
    };

    private static ImageEditCallResult<CharacterChange> Invalid(string error) =>
        ImageEditCallResult<CharacterChange>.Fail(ImageEditErrorCodes.InvalidRequest, error);
}
