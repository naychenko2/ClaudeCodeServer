using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Mcp.Catalog;

/// <summary>
/// Точный semver (без диапазонов): разбор и сравнение по правилам semver.org —
/// prerelease ниже релиза, числовые идентификаторы ниже буквенных и сравниваются
/// численно, build-метаданные игнорируются. Нужен ревизии каталога для флага
/// «в реестре есть версия новее импортированной»; непарсящаяся строка — «не знаем»,
/// а не «нулевая версия»: сравнение с ней честно не проводится.
/// </summary>
internal static class McpCatalogSemVer
{
    private static readonly Regex Pattern =
        new(@"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.Compiled);

    internal readonly record struct Version(int Major, int Minor, int Patch, string[] Pre);

    internal static bool TryParse(string? text, out Version version)
    {
        version = default;
        if (text is null) return false;
        var match = Pattern.Match(text.Trim());
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups[1].Value, out var major)
            || !int.TryParse(match.Groups[2].Value, out var minor)
            || !int.TryParse(match.Groups[3].Value, out var patch))
            return false;
        var pre = match.Groups[4].Success
            ? match.Groups[4].Value.Split('.', StringSplitOptions.RemoveEmptyEntries)
            : [];
        version = new Version(major, minor, patch, pre);
        return true;
    }

    /// <summary>candidate строго новее baseline; любая непарсящаяся сторона — false.</summary>
    public static bool IsNewer(string? candidate, string? baseline) =>
        TryParse(candidate, out var cand) && TryParse(baseline, out var base_)
        && Compare(cand, base_) > 0;

    /// <summary>Старшая из двух версий по semver; непарсящиеся и null — младше парсящихся.</summary>
    public static string? MaxBySemVer(string? a, string? b)
    {
        var aOk = TryParse(a, out var va);
        var bOk = TryParse(b, out var vb);
        if (aOk && bOk) return Compare(va, vb) >= 0 ? a : b;
        if (bOk) return b;
        return a ?? b;
    }

    private static int Compare(Version a, Version b)
    {
        var core = a.Major != b.Major ? a.Major.CompareTo(b.Major)
            : a.Minor != b.Minor ? a.Minor.CompareTo(b.Minor)
            : a.Patch.CompareTo(b.Patch);
        if (core != 0) return core;
        // Релиз (без prerelease) выше любой prerelease-версии: 1.0.0-beta < 1.0.0
        if (a.Pre.Length == 0 || b.Pre.Length == 0)
            return b.Pre.Length.CompareTo(a.Pre.Length);
        for (var i = 0; i < Math.Min(a.Pre.Length, b.Pre.Length); i++)
        {
            var cmp = CompareIdentifiers(a.Pre[i], b.Pre[i]);
            if (cmp != 0) return cmp;
        }
        // Общий префикс равен: короче — младше (1.0.0-alpha < 1.0.0-alpha.1)
        return a.Pre.Length.CompareTo(b.Pre.Length);
    }

    private static int CompareIdentifiers(string a, string b)
    {
        var aNumeric = a.All(char.IsDigit);
        var bNumeric = b.All(char.IsDigit);
        // Числовой идентификатор ниже буквенного: 1 (сборка 1) < alpha
        if (aNumeric != bNumeric) return aNumeric ? -1 : 1;
        if (aNumeric)
        {
            // long хватает любому реальному semver; сороконечные числа реестр не отдаёт
            return long.Parse(a).CompareTo(long.Parse(b));
        }
        return string.CompareOrdinal(a, b);
    }
}
