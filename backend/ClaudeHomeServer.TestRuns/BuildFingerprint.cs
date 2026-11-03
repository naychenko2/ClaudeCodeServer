using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeHomeServer.Services.TestRuns;

// Отпечаток исходников дерева для dotnet-прогона: если с прошлой УСПЕШНОЙ сборки в дереве
// ничего не менялось, повторная `dotnet build` — холостая (10–20 с на проверку up-to-date),
// и её можно пропустить. Только в памяти процесса: после рестарта первая сборка обязательна.
// Обход дешёвый: содержимое файлов не читается, учитывается (путь, mtime, размер).
public static class BuildFingerprint
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".csproj", ".props", ".targets", ".json", ".resx" };

    // Каталоги, чьё содержимое сборку не определяет (или определяется ею самой)
    private static readonly HashSet<string> SkippedDirs = new(StringComparer.Ordinal)
        { "bin", "obj", "node_modules", ".git", ".cc-attachments" };

    public static string Compute(string root)
    {
        var entries = new List<(string Path, long Ticks, long Length)>();
        Walk(new DirectoryInfo(root), root, entries);
        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, ticks, length) in entries)
            hash.AppendData(Encoding.UTF8.GetBytes($"{path}\0{ticks}\0{length}\n"));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Walk(DirectoryInfo dir, string root, List<(string, long, long)> entries)
    {
        IEnumerable<FileSystemInfo> children;
        try { children = dir.EnumerateFileSystemInfos().ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        foreach (var child in children)
        {
            // Ссылки не раскрываем: обход не должен уйти за дерево или зациклиться
            if (child.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (child is DirectoryInfo sub)
            {
                if (!SkippedDirs.Contains(sub.Name)) Walk(sub, root, entries);
            }
            else if (child is FileInfo file && Extensions.Contains(file.Extension))
                entries.Add((Path.GetRelativePath(root, file.FullName), file.LastWriteTimeUtc.Ticks, file.Length));
        }
    }
}

// Отпечатки последних успешных сборок; ключ — (дерево, цель). Один на процесс: живёт в
// PhasePipeline (синглтон подсистемы), пишут его и прогон тестов, и инструмент build — поэтому
// «build → run_tests» без правок собирает один раз. Сборка решения .slnx покрывает каждый его
// проект (прогон проекта после неё пропускает сборку); сборка проекта решение не покрывает.
// Пишется только после УСПЕШНОЙ сборки, отпечаток снимается ДО неё
public sealed class BuildFingerprintStore
{
    // Причина пропуска при точном совпадении цели
    public const string Unchanged = "дерево не менялось";

    private static readonly StringComparer Paths =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly ConcurrentDictionary<(string Tree, string Target), Entry> _built = new(new KeyComparer());

    // Projects — проекты решения (каталоги относительно дерева); у проекта — null
    private sealed record Entry(string Fingerprint, IReadOnlySet<string>? Projects);

    // Ключ дерева: полный путь без хвостового разделителя
    public static string TreeKey(string workingDirectory) => Path.GetFullPath(workingDirectory)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // Цель относительно дерева: «/», без «./» и хвостового «/»; .csproj — его каталог
    internal static string NormTarget(string? target)
    {
        var t = (target ?? "").Replace('\\', '/').Trim();
        while (t.StartsWith("./", StringComparison.Ordinal)) t = t[2..];
        t = t.TrimEnd('/');
        if (t.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            t = t.LastIndexOf('/') is var slash and >= 0 ? t[..slash] : "";
        return t;
    }

    private static bool IsSolution(string target) => target.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    // Причина пропуска сборки или null — сборка нужна
    public string? Covers(string tree, string? target, string fingerprint)
    {
        var t = NormTarget(target);
        if (_built.TryGetValue((tree, t), out var exact) && exact.Fingerprint == fingerprint) return Unchanged;
        if (t.Length == 0 || IsSolution(t)) return null;
        foreach (var (key, entry) in _built)
            if (entry.Projects is { } projects && entry.Fingerprint == fingerprint
                && Paths.Equals(key.Tree, tree) && projects.Contains(t))
                return $"{Unchanged} после сборки {key.Target}";
        return null;
    }

    public void Remember(string tree, string? target, string fingerprint)
    {
        var t = NormTarget(target);
        _built[(tree, t)] = new Entry(fingerprint, IsSolution(t) ? SolutionProjects(tree, t) : null);
    }

    // Отпечаток соврал: забываем и цель, и решения, которые её покрывали
    public void Forget(string tree, string? target)
    {
        var t = NormTarget(target);
        _built.TryRemove((tree, t), out _);
        foreach (var (key, entry) in _built)
            if (entry.Projects?.Contains(t) == true && Paths.Equals(key.Tree, tree))
                _built.TryRemove(key, out _);
    }

    // Проекты .slnx (атрибут Path у Project, относительно каталога решения) — каталоги
    // относительно дерева; не прочиталось — null: решение покрывает только само себя
    private static IReadOnlySet<string>? SolutionProjects(string tree, string solution)
    {
        try
        {
            var path = Path.Combine(tree, solution);
            var dir = Path.GetDirectoryName(path)!;
            var projects = new HashSet<string>(Paths);
            foreach (var node in System.Xml.Linq.XDocument.Load(path).Descendants("Project"))
            {
                if (node.Attribute("Path")?.Value is not { Length: > 0 } rel) continue;
                var full = Path.GetFullPath(Path.Combine(dir, rel.Replace('\\', '/')));
                projects.Add(NormTarget(Path.GetRelativePath(tree, full)));
            }
            return projects;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException
            or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed class KeyComparer : IEqualityComparer<(string Tree, string Target)>
    {
        public bool Equals((string Tree, string Target) a, (string Tree, string Target) b) =>
            Paths.Equals(a.Tree, b.Tree) && Paths.Equals(a.Target, b.Target);

        public int GetHashCode((string Tree, string Target) k) => HashCode.Combine(Paths.GetHashCode(k.Tree), Paths.GetHashCode(k.Target));
    }
}
