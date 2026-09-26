using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Versioning;

namespace ClaudeHomeServer.Services.ImageEditor;

// Запись варианта в проект новым файлом (ADR-017, раздел 5). Флага перезаписи нет по
// построению: правка файла идёт через версионное сохранение (hero.png → hero.v2.png,
// FileMode.CreateNew), «Нарисовать картинку» — новым именем в папке, занятое даёт -2, -3.
// «Сохранить как…» (ADR-018 §5) — ровно выбранное имя: занятое не подменяется номером,
// а даёт NameTaken, подсказку следующего свободного строит Check.
public sealed class ImageEditSaver(IVersionedImageStore versions) : IImageEditSaver
{
    // Запрещённые символы берём по Windows: проект может переехать туда, и имя должно открыться
    private static readonly char[] ForbiddenChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];
    private static readonly Regex VersionSuffix = new(@"^(.+)\.v\d+$", RegexOptions.Compiled);
    private const int MaxStemLength = 200;
    private const int MaxSuggestion = 10_000;

    public ImageEditCallResult<ImageEditSaveResultDto> Save(string projectRoot, ImageEditSaveRequest request, EditedImage image)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(request.SourcePath))
                return Ok(versions.SaveNextVersion(projectRoot, request.SourcePath, image.Bytes));
            return Ok(SaveNew(projectRoot, request.Folder, request.FileName, image.Bytes));
        }
        catch (UnauthorizedAccessException)
        {
            return Invalid("Путь вне папки проекта");
        }
        catch (DirectoryNotFoundException)
        {
            return Invalid("Папка не найдена");
        }
    }

    public ImageEditCallResult<ImageEditSaveResultDto> SaveAs(
        string projectRoot, string? folder, string? fileName, EditedImage image)
    {
        var (target, error) = Resolve(projectRoot, folder, fileName, ExtensionOf(image.Bytes));
        if (target is null) return Invalid(error!);

        var name = target.Stem + target.Ext;
        var full = Path.Combine(target.Dir, name);
        try
        {
            using var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
            fs.Write(image.Bytes, 0, image.Bytes.Length);
            return Ok(target.RelativeOf(name));
        }
        catch (DirectoryNotFoundException)
        {
            return Invalid("Папка не найдена");
        }
        catch (IOException) when (Exists(full))
        {
            // Гонка двух сохранений и заранее занятое имя — один и тот же ответ
            return ImageEditCallResult<ImageEditSaveResultDto>.Fail(ImageEditErrorCodes.NameTaken,
                $"Файл {target.RelativeOf(name)} уже есть");
        }
    }

    public ImageEditCallResult<SaveCheckResponse> Check(
        string projectRoot, string? folder, string? fileName, string extension)
    {
        var (target, error) = Resolve(projectRoot, folder, fileName, extension);
        if (target is null)
            return ImageEditCallResult<SaveCheckResponse>.Fail(ImageEditErrorCodes.InvalidRequest, error!);

        var name = target.Stem + target.Ext;
        var taken = Exists(Path.Combine(target.Dir, name));
        return ImageEditCallResult<SaveCheckResponse>.Ok(
            new SaveCheckResponse(target.RelativeOf(name), taken, taken ? Suggest(target) : null));
    }

    // Расширение по фактическому формату байтов, а не по имени из запроса
    public static string ExtensionOf(byte[] bytes) => ImageFormatSniffer.DetectExtension(bytes) ?? ".png";

    private sealed record Target(string Dir, string DirRel, string Stem, string Ext)
    {
        public string RelativeOf(string name) => DirRel.Length == 0 ? name : $"{DirRel}/{name}";
    }

    private static (Target? Target, string? Error) Resolve(string root, string? folder, string? fileName, string ext)
    {
        if (ValidateName(fileName, out var stem) is { } nameError) return (null, nameError);

        var dir = root;
        var dirRel = "";
        var rel = (folder ?? "").Trim();
        if (rel.Length > 0 && rel != "." && rel != "./")
        {
            // Папку из диалога не создаём: она обязана быть, внутри корня и без ссылок
            if (ProjectLinkGuard.ResolveInside(root, rel) is not { } full)
                return (null, "Путь вне папки проекта или идёт через символическую ссылку");
            if (!Directory.Exists(full)) return (null, "Папка не найдена");
            dir = full;
            dirRel = Path.GetRelativePath(root, full).Replace('\\', '/');
            if (dirRel == ".") dirRel = "";
        }
        else if (!Directory.Exists(root))
        {
            return (null, "Папка не найдена");
        }
        return (new Target(dir, dirRel, stem, ext), null);
    }

    // null — имя годится. Имя обязано быть именно именем: «a/b.png» — не имя, а путь
    private static string? ValidateName(string? input, out string stem)
    {
        stem = "";
        var name = (input ?? "").Trim();
        if (name.Length == 0) return "Не указано имя файла";
        if (Path.GetFileName(name) != name || name.IndexOfAny(ForbiddenChars) >= 0 || name.Any(char.IsControl))
            return "Имя файла — без папок и символов < > : \" / \\ | ? *";
        if (name.StartsWith('.')) return "Имя файла не может начинаться с точки";

        // Вписанное руками расширение срезается: его ставит сервер по формату
        var stripped = true;
        while (stripped)
        {
            stripped = false;
            foreach (var ext in ImageExtensions)
            {
                if (name.Length > ext.Length && name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^ext.Length].TrimEnd();
                    stripped = true;
                }
            }
        }
        if (name.Length == 0 || name.EndsWith('.')) return "Недопустимое имя файла";
        if (name.Length > MaxStemLength) return $"Имя файла длиннее {MaxStemLength} символов";
        stem = name;
        return null;
    }

    // Следующий свободный номер версии, как у версионного сохранения: hero → hero.v2, hero.v2 → hero.v3
    private static string? Suggest(Target target)
    {
        var m = VersionSuffix.Match(target.Stem);
        var baseStem = m.Success ? m.Groups[1].Value : target.Stem;
        for (var n = 2; n < MaxSuggestion; n++)
        {
            var name = $"{baseStem}.v{n}{target.Ext}";
            if (!Exists(Path.Combine(target.Dir, name))) return target.RelativeOf(name);
        }
        return null;
    }

    // Висячая ссылка не Exists, но имя занято — CreateNew на ней тоже откажет
    private static bool Exists(string full) =>
        File.Exists(full) || Directory.Exists(full) || new FileInfo(full).LinkTarget is not null;

    private static string SaveNew(string root, string? folder, string? fileName, byte[] bytes)
    {
        var stem = Path.GetFileNameWithoutExtension(Path.GetFileName(fileName ?? "").Trim());
        if (string.IsNullOrWhiteSpace(stem) || stem.StartsWith('.')) stem = "image";
        var ext = ExtensionOf(bytes);
        var dir = (folder ?? "").Replace('\\', '/').Trim('/');

        for (var n = 1; ; n++)
        {
            var name = n == 1 ? $"{stem}{ext}" : $"{stem}-{n}{ext}";
            var rel = dir.Length == 0 ? name : $"{dir}/{name}";
            var full = SafePath.Join(root, rel);
            ProjectLinkGuard.EnsureNoLink(root, full);
            try
            {
                using var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
                fs.Write(bytes, 0, bytes.Length);
                return rel;
            }
            catch (IOException) when (File.Exists(full))
            {
                // имя занято — следующий номер
            }
        }
    }

    private static ImageEditCallResult<ImageEditSaveResultDto> Ok(string path) =>
        ImageEditCallResult<ImageEditSaveResultDto>.Ok(new ImageEditSaveResultDto(path));

    private static ImageEditCallResult<ImageEditSaveResultDto> Invalid(string error) =>
        ImageEditCallResult<ImageEditSaveResultDto>.Fail(ImageEditErrorCodes.InvalidRequest, error);
}
