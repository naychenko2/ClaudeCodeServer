using System.Net;
using System.Net.Sockets;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Итог скачивания: байты и тип либо причина отказа (<see cref="Error"/>); <see cref="FinalUri"/> —
/// адрес, с которого файл отдан после редиректов (по нему вызывающий узнаёт имя и расширение).
/// </summary>
public sealed record MediaDownloadResult(byte[]? Bytes, string? ContentType, string? Error, Uri? FinalUri = null)
{
    public bool Ok => Bytes is not null;

    internal static MediaDownloadResult Fail(string error) => new(null, null, error);
}

/// <summary>
/// Скачивание результата генерации по ссылке из ответа поставщика (fal, glif, Higgsfield)
/// и загрузка образца по выданной поставщиком ссылке (<see cref="UploadAsync"/>).
/// Ссылка приходит извне, поэтому сервер не должен стать прокси во внутреннюю сеть:
/// только https, хост проверяется <see cref="SsrfGuard"/> до запроса и ещё раз в
/// <c>ConnectCallback</c> по адресу реального соединения (DNS rebinding), редиректы
/// разбираются вручную с той же проверкой на каждом шаге, тело ограничено потолком.
/// Отказ возвращается значением; исключение летит только при отмене снаружи.
/// </summary>
public sealed class SafeMediaDownloader
{
    public const long ImageMaxBytes = 32L * 1024 * 1024;
    // Самый длинный звук поставщиков — песня ElevenLabs до 10 минут: WAV 48 кГц/24 бит/стерео за
    // 10 минут ≈ 173 МБ, потолок оставляет запас на такой файл и не пускает в память больше
    public const long AudioMaxBytes = 200L * 1024 * 1024;
    // Клип сцены (ADR-022 §3): видео весит на порядок больше аудио
    public const long VideoMaxBytes = 300L * 1024 * 1024;
    public const int MaxRedirects = 3;

    public static SafeMediaDownloader Shared { get; } = new(CreateHandler());

    private readonly HttpMessageInvoker _invoker;
    private readonly Func<Uri, CancellationToken, Task<SsrfGuard.AddressCheck>> _hostCheck;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <param name="handler">Транспорт; в бою — <see cref="CreateHandler"/>, в тестах — фейк.</param>
    /// <param name="hostCheck">Проверка хоста до запроса; по умолчанию <see cref="SsrfGuard.CheckAsync"/>.</param>
    public SafeMediaDownloader(HttpMessageHandler handler,
        Func<Uri, CancellationToken, Task<SsrfGuard.AddressCheck>>? hostCheck = null)
    {
        _invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        _hostCheck = hostCheck ?? SsrfGuard.CheckAsync;
    }

    public async Task<MediaDownloadResult> DownloadAsync(string url, long maxBytes, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return MediaDownloadResult.Fail("bad-url");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            for (var hop = 0; ; hop++)
            {
                if (await RefusalAsync(uri, cts.Token) is { } refused) return MediaDownloadResult.Fail(refused);

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await _invoker.SendAsync(request, cts.Token);

                if (IsRedirect(response.StatusCode))
                {
                    if (response.Headers.Location is not { } location) return MediaDownloadResult.Fail("bad-redirect");
                    if (hop >= MaxRedirects) return MediaDownloadResult.Fail("too-many-redirects");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                if (!response.IsSuccessStatusCode) return MediaDownloadResult.Fail($"http-{(int)response.StatusCode}");
                if (response.Content.Headers.ContentLength > maxBytes) return MediaDownloadResult.Fail("too-large");

                var bytes = await ReadCappedAsync(response.Content, maxBytes, cts.Token);
                if (bytes is null) return MediaDownloadResult.Fail("too-large");
                if (bytes.Length == 0) return MediaDownloadResult.Fail("empty");
                return new MediaDownloadResult(bytes, response.Content.Headers.ContentType?.MediaType, null, uri);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return MediaDownloadResult.Fail("timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return MediaDownloadResult.Fail("network");
        }
    }

    /// <summary>
    /// Загрузка байтов PUT-ом по ссылке поставщика (подписанный адрес хранилища из ответа
    /// media_upload): те же рубежи, что у скачивания, — https, адрес до запроса и в момент
    /// соединения, без прокси. Редиректу не следуем: тело второй раз не отправляется, и 3xx —
    /// отказ. null — загружено, иначе код отказа; до отказа по адресу байты не уходят.
    /// </summary>
    public async Task<string?> UploadAsync(string url, byte[] bytes, string contentType, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "bad-url";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            if (await RefusalAsync(uri, cts.Token) is { } refused) return refused;

            using var request = new HttpRequestMessage(HttpMethod.Put, uri) { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            using var response = await _invoker.SendAsync(request, cts.Token);
            return response.IsSuccessStatusCode ? null : $"http-{(int)response.StatusCode}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "timeout";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return "network";
        }
    }

    /// <summary>
    /// Проверка ссылки без скачивания — для ссылок поставщика, которые сервер не качает сам, а
    /// передаёт дальше. null — ссылка годится, иначе код отказа, как у <see cref="DownloadAsync"/>.
    /// </summary>
    public async Task<string?> CheckAsync(string url, CancellationToken ct) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? await RefusalAsync(uri, ct) : "bad-url";

    private async Task<string?> RefusalAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) return "not-https";
        return await _hostCheck(uri, ct) switch
        {
            SsrfGuard.AddressCheck.Public => null,
            SsrfGuard.AddressCheck.DnsFailed => "dns-failed",
            _ => "private-address",
        };
    }

    private static bool IsRedirect(HttpStatusCode code) => (int)code is >= 300 and <= 399 and not 304;

    // null — тело длиннее потолка: обрываем чтение, не дожидаясь конца
    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Боевой транспорт: без прокси (egress-прокси релеит на приватные адреса, см. ADR-005),
    /// без авто-редиректов и кук, адрес соединения резолвится и проверяется в момент connect.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = ConnectAsync,
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var address = await ResolvePublicAsync(context.DnsEndPoint.Host, ct)
            ?? throw new HttpRequestException($"Адрес {context.DnsEndPoint.Host} не публичный — соединение запрещено");
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Адрес для соединения, если ВСЕ адреса хоста публичные; иначе null (fail-closed, как у SsrfGuard).</summary>
    internal static async Task<IPAddress?> ResolvePublicAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal)) addresses = [literal];
        else
        {
            try { addresses = await Dns.GetHostAddressesAsync(host, ct); }
            catch (SocketException) { return null; }
        }
        return addresses.Length > 0 && addresses.All(SsrfGuard.IsPublic) ? addresses[0] : null;
    }
}
