namespace ClaudeHomeServer.DeviceAgent.Composition;

/// <summary>
/// Что машина знает о себе для запретного списка: профили пользователя (<c>$HOME</c>,
/// <c>%USERPROFILE%</c> — и лексически, и реальным путём), каталоги самого агента и точки
/// монтирования. Собирает боевые значения <c>Hosting.ProjectFolderBinder</c>, тесты — свои.
/// </summary>
internal sealed record AgentForbiddenContext(
    IReadOnlyList<string> Homes,
    IReadOnlyList<string> AgentDirectories,
    IReadOnlyList<string> MountPoints);

/// <summary>
/// Запретный список автовыдачи папок (решение владельца 2026-09-27, ADR-016 §5) — одна точка
/// правды. Сервер список не видит и переопределить не может: агент отказывает сам.
///
/// Запрещены: корень диска и точки монтирования; сам профиль пользователя (подпапки можно) и
/// всё, что его содержит; системные каталоги; <c>AppData</c> и <c>.config</c> целиком; каталоги
/// агента; <c>.ssh</c>, <c>.gnupg</c>, <c>.aws</c>, <c>.kube</c>, <c>.claude</c> — сами, всё
/// внутри них и всё, что их содержит. Путь проверяется дважды: лексически (с разбором «..»)
/// и реальным — со всеми ссылками и junction по дороге, раскрытыми <see cref="AgentPathPolicy.RealPath"/>.
///
/// Сравнение — без учёта регистра на любой ОС: лишний отказ на Linux (<c>/home/u/.SSH</c>)
/// дешевле пропуска на Windows и macOS.
/// </summary>
internal static class AgentForbiddenPaths
{
    /// <summary>Каталоги с ключами и учётками: запрещены в любом месте пути.</summary>
    private static readonly string[] SecretDirectories = [".ssh", ".gnupg", ".aws", ".kube", ".claude"];

    /// <summary>Каталоги настроек приложений: запрещены целиком, в любом месте пути.</summary>
    private static readonly string[] SettingsDirectories = ["AppData", ".config"];

    private static readonly string[] UnixSystemDirectories =
        ["etc", "usr", "bin", "sbin", "boot", "var", "opt", "proc", "sys", "dev"];

    private static readonly string[] WindowsSystemDirectories = ["Windows", "ProgramData"];

    private const string WindowsProgramFiles = "Program Files";

    private static readonly StringComparison Cmp = StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Текст отказа или null, если путь можно выдать. <paramref name="path"/> — абсолютный путь
    /// этой машины; проверяются и он сам, и его реальный путь.
    /// </summary>
    public static string? Check(string path, AgentForbiddenContext context)
    {
        if (ShapeRefusalOf(path) is { } shape) return shape;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return "Нужен абсолютный путь этой машины";
        string real;
        try { real = AgentPathPolicy.RealPath(path); }
        catch (IOException e) { return "Путь не разбирается: " + e.Message; }
        return RefusalOf(path, context) ?? RefusalOf(real, context);
    }

    /// <summary>
    /// Проверка одной строки пути без обращения к диску: корень и сегменты разбираются вручную,
    /// «..» схлопываются, форма пути Windows разбирается и на Linux (так её и тестируют).
    /// </summary>
    internal static string? RefusalOf(string path, AgentForbiddenContext context)
    {
        if (ShapeRefusalOf(path) is { } shape) return shape;
        if (Parse(path) is not { } p) return "Нужен абсолютный путь этой машины";
        var (root, segments, windows) = p;

        if (segments.Count == 0) return "Нельзя выдать корень диска";

        foreach (var segment in segments)
        {
            if (SecretDirectories.Any(s => segment.Equals(s, Cmp)))
                return $"Нельзя выдать папку с ключами и учётными данными («{segment}») и ничего внутри неё";
            if (SettingsDirectories.Any(s => segment.Equals(s, Cmp)))
                return $"Нельзя выдать каталог настроек приложений («{segment}») и ничего внутри него";
        }

        var top = segments[0];
        if (windows
            ? WindowsSystemDirectories.Any(s => top.Equals(s, Cmp)) || top.StartsWith(WindowsProgramFiles, Cmp)
            : UnixSystemDirectories.Any(s => top.Equals(s, Cmp)))
            return $"Нельзя выдать системный каталог «{Join(root, segments[..1], windows)}» и ничего внутри него";

        foreach (var home in context.Homes)
        {
            if (Parse(home) is not { } h || h.Windows != windows || !SameRoot(h.Root, root)) continue;
            if (IsUnder(h.Segments, segments))
                return segments.Count == h.Segments.Count
                    ? "Нельзя выдать весь профиль пользователя — укажите подпапку"
                    : "Нельзя выдать папку, в которой лежит профиль пользователя";
        }

        foreach (var agent in context.AgentDirectories)
        {
            if (Parse(agent) is not { } a || a.Windows != windows || !SameRoot(a.Root, root)) continue;
            if (IsUnder(a.Segments, segments) || IsUnder(segments, a.Segments))
                return "Нельзя выдать каталог самого агента, ничего внутри него и ничего, что его содержит";
        }

        foreach (var mount in context.MountPoints)
        {
            if (Parse(mount) is not { } m || m.Windows != windows || !SameRoot(m.Root, root)) continue;
            if (m.Segments.Count == segments.Count && IsUnder(m.Segments, segments))
                return "Нельзя выдать точку монтирования диска — укажите подпапку";
        }

        return null;
    }

    /// <summary>
    /// Формы пути, которые лексическое сравнение не судит честно, — отказ до обращения к диску
    /// (биндер зовёт это раньше любых проверок ФС, чтобы не стучаться на чужой сервер):
    /// <c>\\?\</c>, <c>\\.\</c> и UNC обходят сравнение корней и не являются папкой этой машины;
    /// короткое имя 8.3 (<c>PROGRA~1</c>) ФС разворачивает в запретный каталог мимо сравнения
    /// сегментов; двоеточие в сегменте — альтернативный поток NTFS. Ждать <c>IOException</c> нельзя.
    /// </summary>
    internal static string? ShapeRefusalOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        // Любая пара разделителей в начале: «\\», «//», «/\\» — Windows все их читает как «\\»
        if (path.Length >= 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/')
            return @"Сетевые и служебные пути (\\…) не поддерживаются — укажите папку на диске этого компьютера";
        if (Parse(path) is not { Windows: true } p) return null;
        foreach (var segment in p.Segments)
        {
            if (segment.Contains(':'))
                return $"Двоеточие в имени папки недопустимо («{segment}»)";
            if (ShortName.IsMatch(segment))
                return $"Укажите полный путь без коротких имён (…~1): «{segment}»";
        }
        return null;
    }

    // Короткое имя 8.3: до восьми символов, тильда, номер, расширение до трёх символов
    private static readonly System.Text.RegularExpressions.Regex ShortName =
        new(@"^[^~\\/]{1,8}~\d+(\.[^\\/]{0,3})?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private sealed record Parsed(string Root, List<string> Segments, bool Windows);

    // Корень: «C:\» (Windows) или «/» (Unix). Относительный, сетевой и служебный путь — null
    private static Parsed? Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string root;
        string rest;
        bool windows;
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
        {
            root = char.ToUpperInvariant(path[0]) + ":";
            rest = path[3..];
            windows = true;
        }
        else if (path[0] == '/')
        {
            root = "/";
            rest = path[1..];
            windows = false;
        }
        else return null;

        var segments = new List<string>();
        foreach (var s in rest.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (s == ".") continue;
            if (s == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            // Windows отбрасывает хвостовые точки и пробелы: «.ssh.» — тот же «.ssh»
            segments.Add(windows ? s.TrimEnd('.', ' ') : s);
        }
        segments.RemoveAll(s => s.Length == 0);
        return new Parsed(root, segments, windows);
    }

    private static bool SameRoot(string a, string b) => a.Equals(b, Cmp);

    // path внутри base (или равен ему)
    private static bool IsUnder(List<string> path, List<string> @base) =>
        path.Count >= @base.Count && @base.Select((s, i) => s.Equals(path[i], Cmp)).All(x => x);

    private static string Join(string root, List<string> segments, bool windows) =>
        windows ? root + @"\" + string.Join('\\', segments) : "/" + string.Join('/', segments);
}
