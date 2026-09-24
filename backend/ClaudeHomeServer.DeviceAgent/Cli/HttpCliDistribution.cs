using System.Text.Json;

namespace ClaudeHomeServer.DeviceAgent.Cli;

/// <summary>
/// Официальная раздача нативных сборок Claude Code: та же корзина, из которой качают
/// install.sh / install.ps1. Раскладка: <c>{base}/{version}/manifest.json</c> и
/// <c>{base}/{version}/{platform}/{binary}</c>.
/// </summary>
public sealed class HttpCliDistribution(HttpClient http, Uri? baseUri = null) : ICliDistribution
{
    public static readonly Uri DefaultBaseUri = new("https://downloads.claude.ai/claude-code-releases/");

    // Манифест — пара килобайт; потолок не даёт подсунуть вместо него что-то огромное.
    private const int MaxManifestBytes = 1 << 20;

    private readonly Uri _base = EnsureTrailingSlash(baseUri ?? DefaultBaseUri);

    public string Name => _base.Host;

    public async Task<CliManifest> GetManifestAsync(string version, CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(_base, $"{version}/manifest.json"),
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxManifestBytes)
            throw new CliIntegrityException("манифест выпуска подозрительно большой");

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxManifestBytes)
                throw new CliIntegrityException("манифест выпуска подозрительно большой");
            buffer.Write(chunk, 0, read);
        }

        return Parse(buffer.ToArray());
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

    internal static CliManifest Parse(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var version = root.GetProperty("version").GetString()
                ?? throw new CliIntegrityException("в манифесте нет версии");

            var platforms = new Dictionary<string, CliPlatformBuild>(StringComparer.Ordinal);
            foreach (var p in root.GetProperty("platforms").EnumerateObject())
            {
                if (!p.Value.TryGetProperty("binary", out var bin) ||
                    !p.Value.TryGetProperty("checksum", out var sum) ||
                    !p.Value.TryGetProperty("size", out var size))
                    continue;
                platforms[p.Name] = new CliPlatformBuild(bin.GetString() ?? "", sum.GetString() ?? "", size.GetInt64());
            }
            return new CliManifest(version, platforms);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new CliIntegrityException($"манифест выпуска не читается: {e.Message}");
        }
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
