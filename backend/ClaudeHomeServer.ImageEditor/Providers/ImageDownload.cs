using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Services.ImageEditor;

// Скачивание результата поставщика бэкендом (ADR-017, раздел 8): внешние ссылки fal и
// Higgsfield наружу не уходят, варианты отдаются только своей ручкой из рабочей папки.
// Ссылка пришла от поставщика — качаем только через SafeMediaDownloader (SSRF, потолок размера).
internal static class ImageDownload
{
    public static async Task<EditedImage?> FetchAsync(SafeMediaDownloader downloader, string url, string? declaredType, CancellationToken ct)
    {
        var fallback = string.IsNullOrWhiteSpace(declaredType) ? "image/png" : declaredType.Trim();

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',');
            if (comma < 0) return null;
            var type = url[5..comma].Split(';')[0];
            try
            {
                return new EditedImage(Convert.FromBase64String(url[(comma + 1)..]),
                    string.IsNullOrEmpty(type) ? fallback : type);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        var download = await downloader.DownloadAsync(url, SafeMediaDownloader.ImageMaxBytes, ct);
        return download.Bytes is { } bytes ? new EditedImage(bytes, download.ContentType ?? fallback) : null;
    }
}
