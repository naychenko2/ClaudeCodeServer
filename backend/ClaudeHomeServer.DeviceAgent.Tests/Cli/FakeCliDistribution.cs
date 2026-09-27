using System.Security.Cryptography;
using ClaudeHomeServer.DeviceAgent.Cli;

namespace ClaudeHomeServer.DeviceAgent.Tests.Cli;

/// <summary>Источник раздачи без сети: выпуски в памяти плюс рычаги поломок.</summary>
internal sealed class FakeCliDistribution : ICliDistribution
{
    private readonly Dictionary<string, (byte[] Manifest, byte[] Bytes)> _releases = new();

    public string Name => "fake-dist";
    public int ManifestCalls { get; private set; }
    public int BinaryCalls { get; private set; }

    /// <summary>Манифест не отдаётся: сеть лежит.</summary>
    public bool Offline { get; set; }

    /// <summary>Поток бинаря падает после стольких байт (обрыв соединения).</summary>
    public int? FailAfterBytes { get; set; }

    /// <summary>Поток бинаря штатно кончается после стольких байт (сервер закрыл раньше).</summary>
    public int? TruncateAfterBytes { get; set; }

    /// <summary>Отдать эти байты вместо настоящих (подмена бинаря).</summary>
    public byte[]? Tamper { get; set; }

    /// <summary>Чем подписывать манифест; по умолчанию — ключ, которому доверяют тесты.</summary>
    public TestPgpKey Signer { get; set; } = TestPgpKey.Trusted;

    /// <summary>manifest.json.sig в раздаче нет.</summary>
    public bool OmitSignature { get; set; }

    /// <summary>Подменить манифест ПОСЛЕ подписи: подпись настоящая, байты — нет.</summary>
    public Func<byte[], byte[]>? TamperManifest { get; set; }

    public static byte[] Payload(string version) =>
        System.Text.Encoding.UTF8.GetBytes($"#!/bin/sh\necho '{version} (Claude Code)'\n" + new string('x', 200_000));

    public void Publish(string version, string platform = "linux-x64", string binary = "claude",
        string? sha256 = null, long? size = null, string? manifestVersion = null)
    {
        var bytes = Payload(version);
        var build = new CliPlatformBuild(binary, sha256 ?? Convert.ToHexStringLower(SHA256.HashData(bytes)), size ?? bytes.Length);
        var manifest = new
        {
            version = manifestVersion ?? version,
            platforms = new Dictionary<string, object>
            {
                [platform] = new { binary = build.Binary, checksum = build.Sha256, size = build.Size },
            },
        };
        _releases[version] = (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest), bytes);
    }

    public Task<SignedCliManifest> GetManifestAsync(string version, CancellationToken ct)
    {
        ManifestCalls++;
        if (Offline) throw new HttpRequestException("Name or service not known (fake-dist:443)");
        if (!_releases.TryGetValue(version, out var r))
            throw new HttpRequestException("Not Found", null, System.Net.HttpStatusCode.NotFound);
        var signature = OmitSignature ? null : Signer.Sign(r.Manifest);
        var manifest = TamperManifest is { } tamper ? tamper(r.Manifest) : r.Manifest;
        return Task.FromResult(new SignedCliManifest(manifest, signature));
    }

    public Task<Stream> OpenBinaryAsync(string version, string platform, string binary, CancellationToken ct)
    {
        BinaryCalls++;
        var bytes = Tamper ?? _releases[version].Bytes;
        if (TruncateAfterBytes is { } cut) bytes = bytes[..cut];
        Stream stream = new MemoryStream(bytes);
        if (FailAfterBytes is { } fail) stream = new BreakingStream(stream, fail);
        return Task.FromResult(stream);
    }

    private sealed class BreakingStream(Stream inner, int failAfter) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= failAfter) throw new IOException("Connection reset by peer");
            var n = inner.Read(buffer, offset, (int)Math.Min(count, failAfter - _read));
            _read += n;
            return n;
        }
    }
}

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}
