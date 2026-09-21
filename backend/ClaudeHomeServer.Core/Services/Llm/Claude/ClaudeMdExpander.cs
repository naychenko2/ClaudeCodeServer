using System.Text;

namespace ClaudeHomeServer.Services.Llm.Claude;

/// <summary>
/// Импорт, который раскрыть не удалось: файла нет на диске. Target — путь как он написан
/// в карте (`rules/typo.md` для строки `@rules/typo.md`), SourceFile — полный путь файла,
/// где эта строка стоит (импорты бывают вложенными).
/// </summary>
public record ClaudeMdMissingImport(string Target, string SourceFile);

/// <summary>
/// Результат раскрытия. Text — собранный текст (null, если исходного файла нет).
///
/// Остальное — то, о чём раскрытие обязано доложить наружу: молчаливая потеря импорта
/// это худший класс дефекта карты (человек уверен, что правило едет в контекст, а его
/// там нет), а усечение по объёму и глубине занижает измеренный размер — отчёт гигиены
/// сказал бы «всё хорошо» ровно у самой запущенной карты.
/// </summary>
public record ClaudeMdExpansion(
    string? Text,
    int ImportCount,
    IReadOnlyList<ClaudeMdMissingImport> MissingImports,
    bool Truncated,
    bool DepthExceeded);

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
    /// Тонкая обёртка над <see cref="Expand"/>: снимку промпта нужен только текст.
    /// </summary>
    public static string? Read(string path) => Expand(path).Text;

    /// <summary>
    /// Прочитать файл, раскрыть импорты и доложить о том, что раскрытие потеряло:
    /// ненайденные импорты, усечение по объёму и упор в предел вложенности.
    /// </summary>
    public static ClaudeMdExpansion Expand(string path)
    {
        var state = new ExpandState();
        try
        {
            if (!File.Exists(path))
                return new ClaudeMdExpansion(null, 0, [], false, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ClaudeMdExpansion(null, 0, [], false, false);
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        Append(sb, path, visited, depth: 0, state);
        return new ClaudeMdExpansion(sb.Length > 0 ? sb.ToString() : null,
            state.ImportCount, state.Missing, state.Truncated, state.DepthExceeded);
    }

    // Что накопилось по дороге вглубь дерева импортов
    private sealed class ExpandState
    {
        public int ImportCount;
        public bool Truncated;
        public bool DepthExceeded;
        public readonly List<ClaudeMdMissingImport> Missing = [];
    }

    private static void Append(StringBuilder sb, string path, HashSet<string> visited, int depth,
        ExpandState state)
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
        var fence = new MarkdownFence();
        foreach (var line in text.Split('\n'))
        {
            if (sb.Length >= MaxTotalChars)
            {
                sb.Append("\n<!-- обрезано: превышен лимит размера -->\n");
                state.Truncated = true;
                return;
            }

            // Содержимое ```-забора CLI импортом не считает, и мы обязаны совпадать с ним:
            // пример `@rules/typo.md` в блоке кода иначе даёт ложный «мёртвый импорт», а
            // пример на существующий файл вклеивает в снимок промпта то, чего в контексте нет
            if (fence.Consume(line) || fence.InFence)
            {
                sb.Append(line).Append('\n');
                continue;
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
                state.DepthExceeded = true;
                continue;
            }

            var target = ResolveInside(dir, import);
            if (target is null)
            {
                // `@~/…`, абсолютный путь или выход выше папки — оставляем строкой как есть
                sb.Append(line).Append('\n');
                continue;
            }

            // Импорта нет на диске — раньше он молча оставался строкой текста, и человек
            // считал, что правило уехало в контекст. Отдельная находка, а не тишина
            if (!ImportExists(target))
            {
                sb.Append(line).Append("  <!-- импорт не найден -->\n");
                state.Missing.Add(new ClaudeMdMissingImport(import, full));
                continue;
            }

            state.ImportCount++;
            sb.Append("<!-- ↓ ").Append(import).Append(" -->\n");
            Append(sb, target, visited, depth + 1, state);
            sb.Append("<!-- ↑ ").Append(import).Append(" -->\n");
        }
    }

    // Строка-импорт — это `@путь` целиком (так их пишет и сам CLI). Внутритекстовые
    // упоминания вида «см. @rules/git.md» намеренно не трогаем: там это ссылка, а не импорт.
    // Бэктик в строке снимает импорт целиком: `@rules/git.md` в code span — это пример,
    // CLI его не раскрывает (проверка на первый символ ловит лишь часть таких записей)
    private static string? ImportTarget(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '@') return null;
        var target = trimmed[1..];
        return target.Contains(' ') || target.Contains('\t') || target.Contains('`') ? null : target;
    }

    private static bool ImportExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;   // не прочитали — не наше дело различать, почему: импорт не доехал
        }
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
