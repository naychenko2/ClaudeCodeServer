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

// Отпечатки последних успешных сборок; ключ — (дерево, цель). Живёт в TestRunService
// (синглтон подсистемы)
internal sealed class BuildFingerprintStore
{
    private readonly ConcurrentDictionary<(string Tree, string Target), string> _built =
        new(new KeyComparer());

    public bool Matches(string tree, string? target, string fingerprint) =>
        _built.TryGetValue((tree, target ?? ""), out var known) && known == fingerprint;

    public void Remember(string tree, string? target, string fingerprint) => _built[(tree, target ?? "")] = fingerprint;

    public void Forget(string tree, string? target) => _built.TryRemove((tree, target ?? ""), out _);

    private sealed class KeyComparer : IEqualityComparer<(string Tree, string Target)>
    {
        private static readonly StringComparer Paths =
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        public bool Equals((string Tree, string Target) a, (string Tree, string Target) b) =>
            Paths.Equals(a.Tree, b.Tree) && Paths.Equals(a.Target, b.Target);

        public int GetHashCode((string Tree, string Target) k) => HashCode.Combine(Paths.GetHashCode(k.Tree), Paths.GetHashCode(k.Target));
    }
}
