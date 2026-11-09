namespace ClaudeHomeServer.Services.Files;

// Сборочные артефакты git worktree: что это за каталоги и как их убрать.
// Один корень правды для трёх потребителей: GitService (после git worktree remove),
// ночной уборщик (WorktreeCleanupService) и Directory.Build.props (вынос bin/obj).
//
// Удаляются только заведомо воспроизводимые каталоги: backend/<проект>/{bin,obj},
// frontend/dist, frontend/node_modules (если он СВОЙ — симлинк на основное дерево,
// который кладёт scripts/worktree-setup.sh, не трогается) и вынесенный каталог
// $CCS_BUILD_ROOT/<имя-worktree>. Символические ссылки не разыменовываются никогда.
public static class WorktreeBuildArtifacts
{
    public const string BuildRootEnv = "CCS_BUILD_ROOT";

    /// <summary>Корень вынесенных артефактов из окружения; null — вынос не включён.</summary>
    public static string? ExternalRoot()
    {
        var raw = Environment.GetEnvironmentVariable(BuildRootEnv);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return Path.GetFullPath(raw.Trim()); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Вынесенный каталог артефактов этого worktree: &lt;корень&gt;/&lt;имя каталога worktree&gt;.
    /// Имя берётся так же, как в Directory.Build.props (имя каталога над backend/).
    /// </summary>
    public static string? ExternalDirFor(string worktreePath, string? buildRoot = null)
    {
        buildRoot ??= ExternalRoot();
        if (buildRoot is null) return null;
        var name = Path.GetFileName(TrimSeparators(worktreePath));
        if (string.IsNullOrEmpty(name)) return null;
        return Path.Combine(buildRoot, name);
    }

    /// <summary>Каталоги артефактов внутри дерева worktree (существующие, без симлинков на чужое).</summary>
    public static IReadOnlyList<string> InTreeDirs(string worktreePath, bool includeNodeModules)
    {
        var result = new List<string>();
        var backend = Path.Combine(worktreePath, "backend");
        if (Directory.Exists(backend))
        {
            foreach (var project in SafeEnumerateDirs(backend))
            {
                foreach (var leaf in new[] { "bin", "obj" })
                {
                    var dir = Path.Combine(project, leaf);
                    if (Directory.Exists(dir)) result.Add(dir);
                }
            }
        }

        var frontend = Path.Combine(worktreePath, "frontend");
        var dist = Path.Combine(frontend, "dist");
        if (Directory.Exists(dist)) result.Add(dist);
        var modules = Path.Combine(frontend, "node_modules");
        if (includeNodeModules && Directory.Exists(modules) && !IsSymlink(modules)) result.Add(modules);
        return result;
    }

    /// <summary>
    /// Удалить артефакты worktree (в дереве и вынесенные). Возвращает освобождённые байты.
    /// Отказывается работать с путём, который не похож на рабочее дерево git (нет .git),
    /// — защита от опечатки в пути, превращающей уборку в rm -rf чужого каталога.
    /// </summary>
    public static long Clean(string worktreePath, bool includeNodeModules, string? buildRoot = null)
    {
        if (!LooksLikeWorktree(worktreePath)) return 0;
        long freed = 0;
        foreach (var dir in InTreeDirs(worktreePath, includeNodeModules))
            freed += DeleteTree(dir);
        freed += CleanExternal(worktreePath, buildRoot);
        return freed;
    }

    /// <summary>
    /// Удалить только вынесенный каталог (дерево уже снесено git worktree remove,
    /// проверка на .git невозможна — полагаемся на то, что каталог лежит строго внутри корня).
    /// </summary>
    public static long CleanExternal(string worktreePath, string? buildRoot = null)
    {
        buildRoot ??= ExternalRoot();
        var dir = ExternalDirFor(worktreePath, buildRoot);
        if (dir is null || buildRoot is null || !Directory.Exists(dir)) return 0;
        return IsStrictlyInside(buildRoot, dir) ? DeleteTree(dir) : 0;
    }

    public static bool LooksLikeWorktree(string worktreePath) =>
        !string.IsNullOrWhiteSpace(worktreePath)
        && Path.IsPathRooted(worktreePath)
        && (File.Exists(Path.Combine(worktreePath, ".git")) || Directory.Exists(Path.Combine(worktreePath, ".git")));

    public static bool IsStrictlyInside(string root, string path)
    {
        var r = TrimSeparators(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var p = Path.GetFullPath(path);
        return p.StartsWith(r, StringComparison.Ordinal) && p.Length > r.Length;
    }

    /// <summary>Размер дерева без захода в симлинки; недоступное считается нулём.</summary>
    public static long SizeOf(string dir)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
                {
                    if (entry.LinkTarget is not null) continue;
                    if (entry is DirectoryInfo) stack.Push(entry.FullName);
                    else if (entry is FileInfo f) total += f.Length;
                }
            }
            catch (Exception) { /* исчез или недоступен — не считаем */ }
        }
        return total;
    }

    private static long DeleteTree(string dir)
    {
        var size = SizeOf(dir);
        try
        {
            // Симлинк удаляется самой ссылкой, без захода внутрь
            if (IsSymlink(dir)) Directory.Delete(dir, false);
            else Directory.Delete(dir, true);
            return size;
        }
        catch (Exception)
        {
            // Часть файлов могла остаться (занят процессом): считаем по факту
            var left = Directory.Exists(dir) ? SizeOf(dir) : 0;
            return Math.Max(0, size - left);
        }
    }

    private static bool IsSymlink(string path)
    {
        try { return new DirectoryInfo(path).LinkTarget is not null; }
        catch (Exception) { return false; }
    }

    private static IEnumerable<string> SafeEnumerateDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); }
        catch (Exception) { return []; }
    }

    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
