using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.Images.Editing;

// Скачивание результата поставщика бэкендом (ADR-016, раздел 8): внешние ссылки fal и
// Higgsfield наружу не уходят, варианты отдаются только своей ручкой из рабочей папки.
internal static class ImageDownload
{
    public static async Task<EditedImage?> FetchAsync(HttpClient client, string url, string? declaredType, CancellationToken ct)
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

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        using var resp = await client.GetAsync(uri, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        return bytes.Length == 0 ? null : new EditedImage(bytes, resp.Content.Headers.ContentType?.MediaType ?? fallback);
    }
}
