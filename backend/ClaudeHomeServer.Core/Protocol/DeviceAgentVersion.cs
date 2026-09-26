using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Protocol;

/// <summary>
/// Версия агента устройства (ADR-016, agent-distribution Р1) — номер выкатки:
/// <c>1.N.0</c>, где N — число коммитов; сборка из грязного дерева —
/// <c>1.N.0-dirty.yyyyMMddHHmmss</c>, иначе два разных бинаря получили бы одну версию.
/// Хвост <c>+sha</c> из InformationalVersion — метаданные сборки: в сравнении и равенстве
/// не участвует, в <see cref="ToString"/> (имя каталога версии, URL) не попадает.
///
/// Порядок — semver: грязная сборка младше чистой той же тройки, грязные между собой
/// упорядочены штампом. Других пре-релизных хвостов выкатка не выпускает — они не
/// разбираются, чтобы в имя каталога не просочилось ничего, кроме цифр и точек.
/// </summary>
public sealed class DeviceAgentVersion : IEquatable<DeviceAgentVersion>, IComparable<DeviceAgentVersion>
{
    private DeviceAgentVersion(int major, int minor, int patch, string? dirtyStamp, string? build)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        DirtyStamp = dirtyStamp;
        Build = build;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>Штамп <c>yyyyMMddHHmmss</c> грязной сборки; null — сборка из чистого дерева.</summary>
    public string? DirtyStamp { get; }

    /// <summary>Метаданные после <c>+</c> (обычно sha коммита); null — их нет.</summary>
    public string? Build { get; }

    public bool IsDirty => DirtyStamp is not null;

    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out DeviceAgentVersion? version)
    {
        version = null;
        if (text is null) return false;
        var m = Pattern.Match(text);
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups["major"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(m.Groups["minor"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(m.Groups["patch"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
            return false;
        var dirty = m.Groups["dirty"].Success ? m.Groups["dirty"].Value : null;
        var build = m.Groups["build"].Success ? m.Groups["build"].Value : null;
        version = new DeviceAgentVersion(major, minor, patch, dirty, build);
        return true;
    }

    public static DeviceAgentVersion Parse(string text) =>
        TryParse(text, out var version)
            ? version
            : throw new FormatException($"Не версия агента устройства: «{text}»");

    public int CompareTo(DeviceAgentVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        return (DirtyStamp, other.DirtyStamp) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            // Штамп фиксированной длины из цифр — порядковое сравнение строк и есть числовое
            var (a, b) => string.CompareOrdinal(a, b),
        };
    }

    public bool Equals(DeviceAgentVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => Equals(obj as DeviceAgentVersion);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, DirtyStamp);

    /// <summary>Версия без метаданных сборки — то, что идёт в имя каталога и в URL.</summary>
    public override string ToString() =>
        DirtyStamp is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-dirty.{DirtyStamp}";

    public static bool operator ==(DeviceAgentVersion? a, DeviceAgentVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(DeviceAgentVersion? a, DeviceAgentVersion? b) => !(a == b);
    public static bool operator <(DeviceAgentVersion a, DeviceAgentVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(DeviceAgentVersion a, DeviceAgentVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(DeviceAgentVersion a, DeviceAgentVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(DeviceAgentVersion a, DeviceAgentVersion b) => a.CompareTo(b) >= 0;

    // Обычный Regex, а не [GeneratedRegex]: генератор кладёт свои типы в namespace
    // System.Text.RegularExpressions.Generated, а Core.dll держит сторож разрешённых
    // неймспейсов (SubsystemBoundaryTests.CoreDll_СодержитТолькоРазрешённыеНеймспейсы)
    private static readonly Regex Pattern = new(
        @"^(?<major>0|[1-9]\d{0,8})\.(?<minor>0|[1-9]\d{0,8})\.(?<patch>0|[1-9]\d{0,8})(?:-dirty\.(?<dirty>\d{14}))?(?:\+(?<build>[0-9A-Za-z][0-9A-Za-z.-]{0,63}))?\z",
        RegexOptions.CultureInvariant);
}

/// <summary>
/// Совместимость агента устройства с этим сервером (agent-distribution Р2). Минимальная
/// версия — свойство кода, а не настройка: её поднимают вместе с ломающей правкой протокола
/// канала устройства. Агент ниже минимума получает отказ хода, выше — работает и
/// обновляется между ходами до версии, которую назовёт сервер.
/// </summary>
public static class DeviceAgentCompatibility
{
    /// <summary>
    /// 1.0.0 — версия, которую SDK ставит сборке без <c>-p:Version</c>: агенты, собранные до
    /// версий выкатки, остаются совместимыми.
    /// </summary>
    public const string MinVersion = "1.0.0";

    public static DeviceAgentVersion Min { get; } = DeviceAgentVersion.Parse(MinVersion);
}
