using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.HandsBridge.Policy;

/// <summary>
/// Нормализация путей программ по правилам Win32, но без обращения к диску: одна и та же на
/// Windows и в тестах на Linux. Проверяется и запускается ОДНА строка — результат нормализации,
/// поэтому расхождения «сверили одно, запустили другое» нет по построению.
/// </summary>
public static class HandsAppPaths
{
    private static readonly char[] InvalidChars = ['"', '<', '>', '|', '?', '*'];

    /// <summary>
    /// Полный путь вида <c>X:\...</c>: «/» → «\», пустые и «.» сегменты убраны, «..» свёрнуты,
    /// хвостовые точки и пробелы сегментов срезаны (так их режет Win32), буква диска — заглавная.
    /// null — путь не полный или подозрительный: относительный, голое имя (поиска по PATH нет),
    /// UNC и <c>\\?\</c>, «..» выше корня, поток NTFS через «:», управляющие символы.
    /// </summary>
    public static string? TryNormalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (var ch in path)
        {
            if (char.IsControl(ch) || Array.IndexOf(InvalidChars, ch) >= 0)
                return null;
        }

        var p = path.Replace('/', '\\');
        if (p.Length < 4 || !char.IsAsciiLetter(p[0]) || p[1] != ':' || p[2] != '\\')
            return null;
        if (p.IndexOf(':', 2) >= 0)
            return null;

        var segments = new List<string>();
        foreach (var raw in p[3..].Split('\\'))
        {
            if (raw.Length == 0 || raw == ".")
                continue;
            if (raw == "..")
            {
                if (segments.Count == 0)
                    return null;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            var segment = raw.TrimEnd('.', ' ');
            if (segment.Length == 0)
                return null;
            segments.Add(segment);
        }

        if (segments.Count == 0)
            return null;

        return $"{char.ToUpperInvariant(p[0])}:\\{string.Join('\\', segments)}";
    }

    /// <summary>Имя файла из нормализованного пути.</summary>
    public static string FileName(string normalizedPath) =>
        normalizedPath[(normalizedPath.LastIndexOf('\\') + 1)..];

    /// <summary>
    /// Интерпретатор из <see cref="HandsForbiddenApps"/>: по имени, по имени без расширения
    /// (<c>cmd.com</c> — тот же cmd) и по префиксам <c>python*</c>, <c>pwsh*</c>. Регистр не важен.
    /// </summary>
    public static bool IsForbidden(string normalizedPath)
    {
        var name = FileName(normalizedPath);
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;

        foreach (var forbidden in HandsForbiddenApps.Names)
        {
            var forbiddenDot = forbidden.LastIndexOf('.');
            var forbiddenStem = forbiddenDot > 0 ? forbidden[..forbiddenDot] : forbidden;
            if (name.Equals(forbidden, StringComparison.OrdinalIgnoreCase) ||
                stem.Equals(forbiddenStem, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var prefix in HandsForbiddenApps.NamePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Запускаем только <c>.exe</c>: <c>CreateProcess</c> молча отдаёт <c>.bat</c>/<c>.cmd</c>
    /// интерпретатору cmd, а ярлыки и прочее без ShellExecute не открываются вовсе.
    /// </summary>
    public static bool IsExecutable(string normalizedPath) =>
        normalizedPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
}
