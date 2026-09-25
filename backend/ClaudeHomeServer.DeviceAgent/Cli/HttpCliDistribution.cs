namespace ClaudeHomeServer.DeviceAgent.Cli;

/// <summary>
/// Официальная раздача нативных сборок Claude Code: та же корзина, из которой качают
/// install.sh / install.ps1. Раскладка: <c>{base}/{version}/manifest.json</c> и
/// <c>{base}/{version}/manifest.json.sig</c> и <c>{base}/{version}/{platform}/{binary}</c>.
/// </summary>
public sealed class HttpCliDistribution(HttpClient http, Uri? baseUri = null) : ICliDistribution
{
    public static readonly Uri DefaultBaseUri = new("https://downloads.claude.ai/claude-code-releases/");

    // Манифест — пара килобайт; потолок не даёт подсунуть вместо него что-то огромное.
    private const int MaxManifestBytes = 1 << 20;

    private readonly Uri _base = EnsureTrailingSlash(baseUri ?? DefaultBaseUri);

    public string Name => _base.Host;

    public async Task<SignedCliManifest> GetManifestAsync(string version, CancellationToken ct)
    {
        var manifest = await DownloadSmallAsync($"{version}/manifest.json", MaxManifestBytes, "манифест выпуска", optional: false, ct);
        // Нет подписи — не ошибка сети, а отказ проверки: решает верификатор, не источник.
        var signature = await DownloadSmallAsync($"{version}/manifest.json.sig", CliManifestVerifier.MaxSignatureBytes,
            "подпись манифеста", optional: true, ct);
        return new SignedCliManifest(manifest!, signature);
    }

    public async Task<Stream> OpenBinaryAsync(string version, string platform, string binary, CancellationToken ct)
    {
        var response = await http.GetAsync(new Uri(_base, $"{version}/{platform}/{binary}"),
            HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            response.EnsureSuccessStatusCode();
            return new OwningStream(await response.Content.ReadAsStreamAsync(ct), response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<byte[]?> DownloadSmallAsync(string path, int maxBytes, string what, bool optional, CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(_base, path), HttpCompletionOption.ResponseHeadersRead, ct);
        if (optional && response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new CliIntegrityException($"{what}: подозрительно большой размер");

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new CliIntegrityException($"{what}: подозрительно большой размер");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");

    /// <summary>Поток тела, который при закрытии освобождает и сам ответ.</summary>
    private sealed class OwningStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

/// <summary>Скачанное не совпало с манифестом или сам манифест негоден.</summary>
public sealed class CliIntegrityException(string message) : Exception(message);
