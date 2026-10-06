using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Services.ImageEditor;

// Скачивание результата поставщика бэкендом (ADR-017, раздел 8): внешние ссылки fal и
// Higgsfield наружу не уходят, варианты отдаются только своей ручкой из рабочей папки.
// Ссылка пришла от поставщика — качаем только через SafeMediaDownloader (SSRF, потолок размера).
internal static class ImageDownload
{
    public static async Task<EditedImage?> FetchAsync(SafeMediaDownloader downloader, string url, string? declaredType, CancellationToken ct) =>
        (await FetchWithErrorAsync(downloader, url, declaredType, ct)).Image;

    // То же, но с кодом отказа загрузчика (private-address, timeout, http-403…) — его показываем человеку
    public static async Task<(EditedImage? Image, string? Error)> FetchWithErrorAsync(SafeMediaDownloader downloader, string url,
        string? declaredType, CancellationToken ct)
    {
        var fallback = string.IsNullOrWhiteSpace(declaredType) ? "image/png" : declaredType.Trim();

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',');
            if (comma < 0) return (null, "bad-data-uri");
            var type = url[5..comma].Split(';')[0];
            try
            {
                return (new EditedImage(Convert.FromBase64String(url[(comma + 1)..]),
                    string.IsNullOrEmpty(type) ? fallback : type), null);
            }
            catch (FormatException)
            {
                return (null, "bad-data-uri");
            }
        }

        var download = await downloader.DownloadAsync(url, SafeMediaDownloader.ImageMaxBytes, ct);
        return download.Bytes is { } bytes
            ? (new EditedImage(bytes, download.ContentType ?? fallback), null)
            : (null, download.Error);
    }

    // Код отказа загрузчика — текстом для человека
    public static string Explain(string? error) => error switch
    {
        "private-address" => "адрес хоста не публичный (подменный DNS или внутренняя сеть)",
        "dns-failed" => "не удалось разрешить имя хоста",
        "not-https" or "bad-url" or "bad-data-uri" => "недопустимая ссылка",
        "too-large" => $"файл больше {SafeMediaDownloader.ImageMaxBytes / (1024 * 1024)} МБ",
        "timeout" => "истёк срок скачивания",
        "network" => "сбой сети",
        "empty" => "пустой файл",
        null => "неизвестная ошибка",
        _ => error,
    };
}
