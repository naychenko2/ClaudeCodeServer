namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Параметр <c>tree</c> у run_tests/build: прогон не в дереве чата, а в другом дереве. Произвольный
/// путь = запуск чего угодно на хосте, поэтому легальных форм ровно две:
/// (а) копия мутации, созданная <c>scripts/mutation-copy.sh create</c>: прямой потомок корня копий
/// (без ссылок в пути), с метаданными <c>.meta/&lt;имя&gt;</c>, чей <c>source</c> — дерево этого чата;
/// (б) worktree проекта чата — путь, который числится в <c>git worktree list</c> репозитория
/// проекта; сверка по канонической форме (ссылки разыменованы, «..» свёрнуты).
/// Очередь и счётчик <c>BuildConcurrencyGate</c> остаются теми же: меняется лишь корень запуска,
/// а с ним — ключ блокировки дерева и отпечатков (они ключуются по корню).
/// </summary>
internal static class MutationCopyTree
{
    internal const string ParamName = "tree";

    // Метаданные пишет скрипт: три строки source/created/head — потолок с запасом
    private const int MaxMetaBytes = 4096;

    internal const string ParamDescription =
        "Другое дерево для прогона вместо дерева чата: абсолютный путь worktree ЭТОГО проекта (из "
        + "`git worktree list` его репозитория) ИЛИ копии мутации, созданной из дерева этого чата скриптом "
        + "копий (если он есть в проекте; путь — последняя строка его stdout). Прогон идёт в той же очереди "
        + "сборок, разные деревья друг друга не блокируют. Произвольный путь — отказ; target остаётся "
        + "относительным корня tree.";

    internal const string BothFormsHint =
        "tree принимает только абсолютный путь worktree этого проекта (он должен числиться в "
        + "`git worktree list` репозитория проекта) или копии мутации (scripts/mutation-copy.sh create, "
        + "если этот скрипт есть в проекте).";

    /// <summary>
    /// Пусто — корнем остаётся дерево чата. Иначе — копия мутации или worktree проекта из
    /// <paramref name="projectWorktrees"/> (пути `git worktree list`); всё прочее — отказ с обеими формами.
    /// </summary>
    internal static Task<(bool Ok, string Root, string Error)> ResolveAsync(string? raw, string chatTreeRoot,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? projectWorktrees, CancellationToken ct) =>
        ResolveAsync(raw, chatTreeRoot, DefaultCopiesRoot(), projectWorktrees, ct);

    internal static async Task<(bool Ok, string Root, string Error)> ResolveAsync(string? raw, string chatTreeRoot,
        string copiesRoot, Func<CancellationToken, Task<IReadOnlyList<string>>>? projectWorktrees,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (true, chatTreeRoot, "");
        if (!Path.IsPathRooted(raw)) return (false, chatTreeRoot, "tree — абсолютный путь. " + BothFormsHint);

        if (TryResolve(raw, chatTreeRoot, copiesRoot, out var copy, out var copyError)) return (true, copy, "");

        // Путь под корнем копий — это попытка копии: её точная причина отказа полезнее общей
        var copies = Normalize(SafePath.Canonical(copiesRoot));
        var lexical = Normalize(SafeFullPath(raw));
        var underCopies = copies is not null && lexical is not null
            && lexical.StartsWith(copies + "/", Comparison);

        // Ссылки и «..» сворачиваются до сверки: путь через ссылку на законный worktree — тот же worktree
        // (и тот же ключ блокировки дерева), ссылка наружу списка не проходит. Список правит лишь тот, у кого
        // и так есть Bash в этом репозитории, — права шире Bash'а tree не даёт
        var canonical = Normalize(SafePath.Canonical(raw));
        if (canonical is not null && Directory.Exists(canonical) && HasGit(canonical) && projectWorktrees is not null)
        {
            IReadOnlyList<string> list;
            try { list = await projectWorktrees(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { list = []; }
            foreach (var worktree in list)
            {
                if (string.IsNullOrWhiteSpace(worktree)) continue;
                if (string.Equals(Normalize(SafePath.Canonical(worktree)), canonical, Comparison))
                    return (true, Path.GetFullPath(canonical), "");
            }
        }

        if (underCopies) return (false, chatTreeRoot, copyError);
        return (false, chatTreeRoot, canonical is null || !Directory.Exists(canonical)
            ? $"Каталог tree «{raw}» не найден. " + BothFormsHint
            : $"«{raw}» не числится в `git worktree list` проекта и не копия мутации этого чата. " + BothFormsHint);
    }

    private static bool HasGit(string dir) =>
        Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"));

    private static StringComparison Comparison => SafePath.ComparisonFor(OperatingSystem.IsWindows());

    private static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

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
