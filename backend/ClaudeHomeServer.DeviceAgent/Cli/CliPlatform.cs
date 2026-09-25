using System.Runtime.InteropServices;

namespace ClaudeHomeServer.DeviceAgent.Cli;

/// <summary>
/// Платформа сборки CLI в терминах манифеста выпуска: <c>linux-x64</c>, <c>linux-arm64-musl</c>,
/// <c>win32-x64</c>, <c>darwin-arm64</c> и т. п. — ровно те ключи, которыми официальный
/// установщик выбирает бинарь в <c>manifest.json</c>.
/// </summary>
public sealed record CliPlatform(string Key, bool IsWindows)
{
    public static CliPlatform Detect()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            var other => throw new PlatformNotSupportedException($"CLI не выпускается для архитектуры {other}")
        };

        if (OperatingSystem.IsWindows()) return new CliPlatform($"win32-{arch}", IsWindows: true);
        if (OperatingSystem.IsMacOS()) return new CliPlatform($"darwin-{arch}", IsWindows: false);
        if (OperatingSystem.IsLinux())
            return new CliPlatform(IsMusl() ? $"linux-{arch}-musl" : $"linux-{arch}", IsWindows: false);

        throw new PlatformNotSupportedException($"CLI не выпускается для ОС {RuntimeInformation.OSDescription}");
    }

    // Та же проверка, что у официального install.sh: musl-загрузчик в /lib.
    private static bool IsMusl()
    {
        if (RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return Directory.Exists("/lib") && Directory.EnumerateFiles("/lib", "ld-musl-*").Any();
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
