using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Models;

/// <summary>
/// Версия пакета плагина (ADR-031 §2.2): semver <c>MAJOR.MINOR.PATCH</c> с опциональным
/// pre-release. <see cref="PluginVersion.Pattern"/>: <c>\d+.\d+.\d+(-[A-Za-z0-9.-]+)?</c>.
///
/// Метод <see cref="TryNormalize"/> — единая точка проверки версии (M3): <c>version: ".."</c>
/// отклоняется до <c>SafePath.Join</c> (иначе <c>packages/{id}/../..</c> выносит каталог пакета
/// за пределы владельца → 500, а не 400).
/// </summary>
public sealed record PluginVersion
{
    /// <summary>Формат версии: semver с опциональным pre-release.</summary>
    public const string Pattern = "^\\d+\\.\\d+\\.\\d+(-[A-Za-z0-9.-]+)?$";

    private static readonly Regex Regex = new(Pattern, RegexOptions.Compiled);

    /// <summary>Каноническая строка версии.</summary>
    public string Value { get; }

    public PluginVersion(string value)
    {
        Value = value;
    }

    /// <summary>
    /// Проверить версию: <paramref name="raw"/> — строка версии из манифеста. <c>true</c> —
    /// semver корректен, <paramref name="version"/> — каноническая форма. <c>false</c> — пусто
    /// или не по формату (M3: 400, не 500).
    /// </summary>
    public static bool TryNormalize(string? raw, out PluginVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!Regex.IsMatch(raw)) return false;
        version = new PluginVersion(raw);
        return true;
    }

    /// <summary>Проверка формата (M3): <c>true</c> — версия валидна.</summary>
    public static bool IsValid(string? raw) => TryNormalize(raw, out _);
}
