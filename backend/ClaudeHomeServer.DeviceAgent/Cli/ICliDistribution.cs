namespace ClaudeHomeServer.DeviceAgent.Cli;

/// <summary>Сборка CLI под одну платформу из манифеста выпуска.</summary>
public sealed record CliPlatformBuild(string Binary, string Sha256, long Size);

/// <summary>
/// Манифест выпуска: <c>{base}/{version}/manifest.json</c> с SHA256 и размером бинаря под
/// каждую платформу. Сверка по нему — тот же способ проверки, что у официального установщика.
/// </summary>
public sealed record CliManifest(string Version, IReadOnlyDictionary<string, CliPlatformBuild> Platforms);

/// <summary>
/// Источник раздачи CLI. Боевая реализация — <see cref="HttpCliDistribution"/>
/// (downloads.claude.ai), в тестах — фейк без сети.
/// </summary>
public interface ICliDistribution
{
    /// <summary>Человекочитаемое имя источника для текстов отказа.</summary>
    string Name { get; }

    Task<CliManifest> GetManifestAsync(string version, CancellationToken ct);

    /// <summary>Поток бинаря; читается до конца вызывающим, проверку он же и делает.</summary>
    Task<Stream> OpenBinaryAsync(string version, string platform, string binary, CancellationToken ct);
}
