namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Параметр <c>tree</c> у run_tests/build: прогон в копии мутации, созданной
/// <c>scripts/mutation-copy.sh create</c>. Произвольный путь = запуск чего угодно на хосте,
/// поэтому принимается ТОЛЬКО законная копия: прямой потомок корня копий (без ссылок в пути),
/// с метаданными <c>.meta/&lt;имя&gt;</c>, чей <c>source</c> — дерево этого же чата.
/// Очередь и счётчик <c>BuildConcurrencyGate</c> остаются теми же: меняется лишь корень запуска.
/// </summary>
internal static class MutationCopyTree
{
    internal const string ParamName = "tree";

    // Метаданные пишет скрипт: три строки source/created/head — потолок с запасом
    private const int MaxMetaBytes = 4096;

    internal const string ParamDescription =
        "Путь копии мутации, созданной `scripts/mutation-copy.sh create` из дерева этого чата (последняя строка "
        + "его stdout). Прогон идёт в копии, в той же очереди сборок. Чужой или произвольный путь — отказ; "
        + "target остаётся относительным корня копии.";

    // Корень копий — как в скрипте: $CCS_MUTATION_ROOT или ~/.cache/ccs-mutation
    internal static string DefaultCopiesRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable("CCS_MUTATION_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "ccs-mutation");
    }

    /// <summary>Пусто — корнем остаётся дерево чата. Иначе — проверенный путь копии или отказ с понятным текстом.</summary>
    internal static bool TryResolve(string? raw, string chatTreeRoot, out string root, out string error) =>
        TryResolve(raw, chatTreeRoot, DefaultCopiesRoot(), out root, out error);

    internal static bool TryResolve(string? raw, string chatTreeRoot, string copiesRoot, out string root,
        out string error)
    {
        root = chatTreeRoot;
        error = "";
        if (string.IsNullOrWhiteSpace(raw)) return true;

        if (!Path.IsPathRooted(raw))
            return Refuse(out error, "tree — абсолютный путь копии, который печатает `scripts/mutation-copy.sh create`.");

        var copies = Normalize(SafePath.Canonical(copiesRoot));
        if (copies is null || !Directory.Exists(copiesRoot))
            return Refuse(out error, "Корень копий мутации не найден: создай копию через `scripts/mutation-copy.sh create`.");

        // Сам путь не должен идти через ссылки: каноническая форма (с разыменованием) обязана
        // совпасть с лексической. Иначе «копией» оказался бы любой каталог за ссылкой
        var lexical = Normalize(Path.GetFullPath(raw));
        var canonical = Normalize(SafePath.Canonical(raw));
        if (canonical is null || lexical is null || canonical != lexical)
            return Refuse(out error, "tree ведёт через символическую ссылку или не разбирается — вызов отклонён.");

        // Копия — прямой потомок корня копий; служебная .meta копией не бывает
        var name = Path.GetFileName(canonical);
        if (name.Length == 0 || name.StartsWith('.') || Normalize(Path.GetDirectoryName(canonical)) != copies)
            return Refuse(out error, "tree должен быть копией мутации в корне копий (scripts/mutation-copy.sh create), а не произвольным путём.");

        if (!Directory.Exists(canonical)
            || !(Directory.Exists(Path.Combine(canonical, ".git")) || File.Exists(Path.Combine(canonical, ".git"))))
            return Refuse(out error, "Копия не найдена или не содержит .git — создай её заново через `scripts/mutation-copy.sh create`.");

        var source = ReadSource(Path.Combine(copies, ".meta", name));
        if (source is null)
            return Refuse(out error, "У копии нет метаданных .meta — это не копия, созданная scripts/mutation-copy.sh.");
        var chat = Normalize(SafePath.Canonical(chatTreeRoot));
        if (chat is null || Normalize(SafePath.Canonical(source)) != chat)
            return Refuse(out error, "Копия создана из другого дерева, не из дерева этого чата — прогон в ней запрещён.");

        root = canonical;
        return true;
    }

    private static string? ReadSource(string metaPath)
    {
        try
        {
            var info = new FileInfo(metaPath);
            if (!info.Exists || info.LinkTarget is not null || info.Length is <= 0 or > MaxMetaBytes) return null;
            foreach (var line in File.ReadLines(metaPath))
                if (line.StartsWith("source=", StringComparison.Ordinal))
                    return line["source=".Length..].Trim() is { Length: > 0 } s ? s : null;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private static string? Normalize(string? path) =>
        path?.Replace('\\', '/').TrimEnd('/') is { Length: > 0 } p ? p : null;

    private static bool Refuse(out string error, string text)
    {
        error = text;
        return false;
    }
}
