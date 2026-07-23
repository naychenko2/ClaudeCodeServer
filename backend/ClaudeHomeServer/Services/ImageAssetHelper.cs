using ClaudeHomeServer.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services;

// Общие хелперы работы с загружаемыми/генерируемыми картинками-ассетами (аватар персоны,
// иконка проекта): валидация по magic bytes, сохранение, отдача файла, парс параметров кропа.
// Вынесено из PersonasController, чтобы переиспользовать в ProjectsController без дублирования.
public static class ImageAssetHelper
{
    private static readonly string[] AllowedImageTypes = ["image/jpeg", "image/png", "image/webp"];

    // Проверка загружаемой картинки: заявленный ContentType из белого списка
    // И настоящие magic bytes (FF D8 FF / PNG / RIFF..WEBP). Ext — по фактическому типу.
    public static async Task<(string? Error, string Ext)> ValidateImageAsync(IFormFile file)
    {
        if (!AllowedImageTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
            return ("Допустимы только изображения JPEG, PNG или WebP", "");

        var head = new byte[12];
        await using (var stream = file.OpenReadStream())
        {
            var read = await stream.ReadAtLeastAsync(head, 12, throwOnEndOfStream: false);
            if (read < 12) return ("Файл не похож на изображение", "");
        }

        var ext = DetectImageExt(head);
        return ext is null ? ("Файл не похож на изображение (сигнатура не совпадает)", "") : (null, ext);
    }

    // Определение типа по magic bytes; null — не картинка из белого списка
    public static string? DetectImageExt(byte[] head)
    {
        if (head is [0xFF, 0xD8, 0xFF, ..]) return ".jpg";
        if (head is [0x89, 0x50, 0x4E, 0x47, ..]) return ".png";
        if (head.Length >= 12
            && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F'
            && head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P')
            return ".webp";
        return null;
    }

    public static async Task SaveFormFileAsync(IFormFile file, string path)
    {
        await using var target = File.Create(path);
        await file.CopyToAsync(target);
    }

    // Расширение по content-type сгенерированной картинки (fal отдаёт готовые байты)
    public static string ExtFor(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        _ => ".png",
    };

    // Параметры кропа из multipart-поля (JSON {scale, offsetX, offsetY}); мусор → null
    public static AvatarCropState? ParseCrop(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<AvatarCropState>(raw,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    // Отдать физический файл с content-type по расширению (для GET картинки/оригинала/кандидата).
    // Тип возврата IActionResult — чтобы тернар `... ? PhysicalFileByExt : NotFound()` компилировался.
    public static IActionResult PhysicalFileByExt(string full)
    {
        var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
        if (!provider.TryGetContentType(full, out var contentType))
            contentType = "application/octet-stream";
        return new PhysicalFileResult(full, contentType);
    }
}
