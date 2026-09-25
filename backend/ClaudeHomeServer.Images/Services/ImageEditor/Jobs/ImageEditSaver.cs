using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Versioning;

namespace ClaudeHomeServer.Services.Images.Editing;

// Запись варианта в проект новым файлом (ADR-017, раздел 5). Флага перезаписи нет по
// построению: правка файла идёт через версионное сохранение (hero.png → hero.v2.png,
// FileMode.CreateNew), «Нарисовать картинку» — новым именем в папке, занятое даёт -2, -3.
public sealed class ImageEditSaver(IVersionedImageStore versions) : IImageEditSaver
{
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

    private static string SaveNew(string root, string? folder, string? fileName, byte[] bytes)
    {
        var stem = Path.GetFileNameWithoutExtension(Path.GetFileName(fileName ?? "").Trim());
        if (string.IsNullOrWhiteSpace(stem) || stem.StartsWith('.')) stem = "image";
        var ext = ImageFormatSniffer.DetectExtension(bytes) ?? ".png";
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
