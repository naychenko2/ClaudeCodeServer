using System.Security.Cryptography;
using System.Text;

namespace ClaudeHomeServer.DeviceAgent.Hands;

/// <summary>
/// Профиль Chrome браузерной руки на проект (ADR-016 §7.1). Ключ — первые 16 hex-символов
/// SHA-256 от корня проекта, уже сверенного агентом с корнями машины: id проекта в spec нет,
/// а путь сервер и модель не задают. Цена: переезд папки проекта начинает профиль (и логины в
/// нём) заново.
/// </summary>
internal static class HandsBrowserProfile
{
    /// <summary>Каталог профиля под <paramref name="profilesRoot"/>; регистр корня значим только на Linux, как у политики путей.</summary>
    public static string PathFor(string profilesRoot, string projectRoot) =>
        Path.Combine(profilesRoot, Key(projectRoot, ignoreCase: !OperatingSystem.IsLinux()));

    internal static string Key(string projectRoot, bool ignoreCase)
    {
        var root = projectRoot.TrimEnd('\\', '/');
        if (ignoreCase) root = root.ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16];
    }
}
