using System.Net;

namespace ClaudeHomeServer.Services.AudioEditor.Engines;

// Скачивание файлов результата fal. Ссылка приходит из ответа поставщика, поэтому ей не доверяем:
// только https на стандартном порту и только публичный адрес (литерал IP или все адреса имени —
// мимо loopback, частных сетей и link-local, классификация SsrfGuard из Core). Клиент без
// автоследования редиректов: каждый шаг редиректа проверяется заново до запроса. Размер — с потолком:
// по Content-Length отказ сразу, без него чтение обрывается на потолке. Все отказы — значением
internal sealed class FalDownload(HttpClient client, Func<string, CancellationToken, Task<IPAddress[]>> resolve)
{
    public const long DefaultMaxBytes = 200L * 1024 * 1024;
    private const int MaxRedirects = 5;

    public long MaxBytes { get; init; } = DefaultMaxBytes;

    public sealed record Fetched(byte[] Bytes, string? ContentType, Uri Uri);

    public sealed record Outcome(Fetched? File, string? Error);

    // null — адрес годится, иначе текст отказа
    public async Task<string?> CheckAsync(string url, CancellationToken ct) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? await CheckAsync(uri, ct) : "ссылка на файл не разбирается";

    private async Task<string?> CheckAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) return "ссылка на файл не https";
        if (uri.Port != 443) return "ссылка на файл ведёт на нестандартный порт";
        var host = uri.IdnHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return "ссылка на файл ведёт на внутренний адрес";

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal)) addresses = [literal];
        else
        {
            try
            {
                addresses = await resolve(host, ct);
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
            {
                return "адрес файла не найден";
            }
        }
        if (addresses.Length == 0) return "адрес файла не найден";
        return addresses.All(SsrfGuard.IsPublic) ? null : "ссылка на файл ведёт на внутренний адрес";
    }

    public async Task<Outcome> GetAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return new Outcome(null, "ссылка на файл не разбирается");
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (await CheckAsync(uri, ct) is { } refused) return new Outcome(null, refused);
            using var resp = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } location)
            {
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (!resp.IsSuccessStatusCode) return new Outcome(null, $"сервер файла ответил {(int)resp.StatusCode}");
            if (resp.Content.Headers.ContentLength > MaxBytes) return new Outcome(null, TooBig);

            var bytes = await ReadCappedAsync(resp.Content, ct);
            if (bytes is null) return new Outcome(null, TooBig);
            if (bytes.Length == 0) return new Outcome(null, "пустой файл результата");
            return new Outcome(new Fetched(bytes, resp.Content.Headers.ContentType?.MediaType, uri), null);
        }
        return new Outcome(null, "слишком много перенаправлений");
    }

    private string TooBig => $"файл результата больше {MaxBytes / (1024 * 1024)} МБ";

    // null — потолок превышен; чтение обрывается, не дочитывая остаток
    private async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
