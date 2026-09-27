using System.Text.Json;

namespace ClaudeHomeServer.DeviceAgent.Cli;

/// <summary>Сборка CLI под одну платформу из манифеста выпуска.</summary>
public sealed record CliPlatformBuild(string Binary, string Sha256, long Size);

/// <summary>
/// Манифест выпуска: <c>{base}/{version}/manifest.json</c> с SHA256 и размером бинаря под
/// каждую платформу. Сверка по нему — тот же способ проверки, что у официального установщика.
/// Разбирается ТОЛЬКО после проверки подписи (<see cref="CliManifestVerifier"/>).
/// </summary>
public sealed record CliManifest(string Version, IReadOnlyDictionary<string, CliPlatformBuild> Platforms)
{
    public static CliManifest Parse(byte[] json)
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
}

/// <summary>
/// Сырые байты <c>manifest.json</c> и его отсоединённой подписи <c>manifest.json.sig</c>
/// (null — подписи в раздаче нет). Подпись заверяет именно эти байты, поэтому источник
/// отдаёт их как есть, без разбора.
/// </summary>
public sealed record SignedCliManifest(byte[] Manifest, byte[]? Signature);

/// <summary>
/// Источник раздачи CLI. Боевая реализация — <see cref="HttpCliDistribution"/>
/// (downloads.claude.ai), в тестах — фейк без сети.
/// </summary>
public interface ICliDistribution
{
    /// <summary>Человекочитаемое имя источника для текстов отказа.</summary>
    string Name { get; }

    Task<SignedCliManifest> GetManifestAsync(string version, CancellationToken ct);

    /// <summary>Поток бинаря; читается до конца вызывающим, проверку он же и делает.</summary>
    Task<Stream> OpenBinaryAsync(string version, string platform, string binary, CancellationToken ct);
}
