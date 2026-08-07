using System.Text;

namespace ClaudeHomeServer.Services.Llm.Claude;

/// <summary>
/// Чтение CLAUDE.md с раскрытием @-импортов (`@rules/git.md` и т.п.).
///
/// Это НАША РЕКОНСТРУКЦИЯ того, что CLI кладёт в контекст, а не его вывод: сам claude CLI
/// текст своего слоя наружу не отдаёт. Поэтому раскрываем консервативно — только
/// относительные пути внутри папки исходного файла. Формы, которые CLI понимает, а мы
/// намеренно оставляем строкой как есть: `@~/…`, абсолютные пути, выход выше папки.
/// Цепочку родительских CLAUDE.md вверх по дереву не собираем вовсе.
/// </summary>
public static class ClaudeMdExpander
{
    // Глубина вложенности импортов — как у CLI
    public const int MaxDepth = 5;
    // Потолок на весь результат: файл едет в снимок хода, а снимков на чат до полусотни
    public const int MaxTotalChars = 256 * 1024;

    /// <summary>
    /// Прочитать файл и раскрыть импорты. null — файла нет или он не читается.
    /// </summary>
    public static string? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        Append(sb, path, visited, depth: 0);
        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static void Append(StringBuilder sb, string path, HashSet<string> visited, int depth)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        // Цикл A → B → A: файл уже вставлен выше по дереву импортов
        if (!visited.Add(full))
        {
            sb.Append("<!-- импорт пропущен: циклическая ссылка на ").Append(Path.GetFileName(full)).Append(" -->\n");
            return;
        }

        string text;
        try { text = File.ReadAllText(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            sb.Append("<!-- импорт не прочитан: ").Append(Path.GetFileName(full)).Append(" -->\n");
            return;
        }

        var dir = Path.GetDirectoryName(full) ?? "";
        foreach (var line in text.Split('\n'))
        {
            if (sb.Length >= MaxTotalChars)
            {
                sb.Append("\n<!-- обрезано: превышен лимит размера -->\n");
                return;
            }

            var import = ImportTarget(line);
            if (import is null)
            {
                sb.Append(line).Append('\n');
                continue;
            }

            if (depth >= MaxDepth)
            {
                sb.Append(line).Append("  <!-- импорт не раскрыт: предел вложенности -->\n");
                continue;
            }

            var target = ResolveInside(dir, import);
            if (target is null)
            {
                // `@~/…`, абсолютный путь или выход выше папки — оставляем строкой как есть
                sb.Append(line).Append('\n');
                continue;
            }

            sb.Append("<!-- ↓ ").Append(import).Append(" -->\n");
            Append(sb, target, visited, depth + 1);
            sb.Append("<!-- ↑ ").Append(import).Append(" -->\n");
        }
    }

    // Строка-импорт — это `@путь` целиком (так их пишет и сам CLI). Внутритекстовые
    // упоминания вида «см. @rules/git.md» намеренно не трогаем: там это ссылка, а не импорт.
    private static string? ImportTarget(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '@') return null;
        var target = trimmed[1..];
        return target.Contains(' ') || target.Contains('\t') ? null : target;
    }

    // Путь внутри папки исходного файла — иначе null (не раскрываем).
    private static string? ResolveInside(string dir, string relative)
    {
        if (relative.StartsWith('~') || Path.IsPathRooted(relative)) return null;
        try
        {
            var full = Path.GetFullPath(Path.Combine(dir, relative));
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
