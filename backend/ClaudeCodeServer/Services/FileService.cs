namespace ClaudeCodeServer.Services;

public record FileEntry(string Name, string Path, bool IsDirectory, long? Size, DateTime Modified, bool IsModified, string? Synced = null);

public class FileService
{
    // Папки, которые не обходим при рекурсивном Tree (тяжёлые/нерелевантные для офлайна)
    private static readonly HashSet<string> TreeExcludes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "dev-dist",
        ".vs", ".idea", "publish", ".next", "target", ".cache",
    };

    // Предохранитель от патологически больших деревьев
    private const int TreeMaxEntries = 20000;

    // Защита от path traversal
    internal static string SafeJoin(string root, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativePath.TrimStart('/', '\\')));
        if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Доступ за пределы проекта запрещён");
        return full;
    }

    public IEnumerable<FileEntry> List(string rootPath, string relativePath = "")
    {
        var dir = SafeJoin(rootPath, relativePath);
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException();

        var modified = GetModifiedFiles(rootPath);
        var entries = new List<FileEntry>();

        foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x))
        {
            var info = new DirectoryInfo(d);
            entries.Add(new FileEntry(info.Name, Path.GetRelativePath(rootPath, d).Replace('\\', '/'),
                true, null, info.LastWriteTimeUtc, false));
        }

        foreach (var f in Directory.GetFiles(dir).OrderBy(x => x))
        {
            var info = new FileInfo(f);
            var rel = Path.GetRelativePath(rootPath, f).Replace('\\', '/');
            entries.Add(new FileEntry(info.Name, rel, false, info.Length, info.LastWriteTimeUtc,
                modified.Contains(rel)));
        }

        return entries;
    }

    public IEnumerable<FileEntry> Search(string rootPath, string query)
    {
        var modified = GetModifiedFiles(rootPath);
        return Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(100)
            .Select(f =>
            {
                var info = new FileInfo(f);
                var rel = Path.GetRelativePath(rootPath, f).Replace('\\', '/');
                return new FileEntry(info.Name, rel, false, info.Length, info.LastWriteTimeUtc,
                    modified.Contains(rel));
            });
    }

    // Рекурсивный листинг всего поддерева — для prefetch офлайн-снапшота и синхронизации папок.
    // Исключает тяжёлые папки (TreeExcludes), ограничен TreeMaxEntries.
    public IEnumerable<FileEntry> Tree(string rootPath, string relativePath = "")
    {
        var start = SafeJoin(rootPath, relativePath);
        if (!Directory.Exists(start)) throw new DirectoryNotFoundException();

        var modified = GetModifiedFiles(rootPath);
        var result = new List<FileEntry>();

        void Walk(string dir)
        {
            if (result.Count >= TreeMaxEntries) return;

            foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x))
            {
                if (result.Count >= TreeMaxEntries) return;
                var info = new DirectoryInfo(d);
                if (TreeExcludes.Contains(info.Name)) continue;
                result.Add(new FileEntry(info.Name, Path.GetRelativePath(rootPath, d).Replace('\\', '/'),
                    true, null, info.LastWriteTimeUtc, false));
                Walk(d);
            }

            foreach (var f in Directory.GetFiles(dir).OrderBy(x => x))
            {
                if (result.Count >= TreeMaxEntries) return;
                var info = new FileInfo(f);
                var rel = Path.GetRelativePath(rootPath, f).Replace('\\', '/');
                result.Add(new FileEntry(info.Name, rel, false, info.Length, info.LastWriteTimeUtc,
                    modified.Contains(rel)));
            }
        }

        Walk(start);
        return result;
    }

    public string ReadFile(string rootPath, string relativePath)
    {
        var path = SafeJoin(rootPath, relativePath);
        return File.ReadAllText(path);
    }

    public bool IsBinaryFile(string rootPath, string relativePath)
    {
        var path = SafeJoin(rootPath, relativePath);
        if (!File.Exists(path)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var binaryExts = new[] { ".zip", ".tar", ".gz", ".exe", ".dll", ".bin", ".pdf",
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg",
            ".mp3", ".mp4", ".avi", ".mov", ".wasm", ".so", ".dylib" };
        return binaryExts.Contains(ext);
    }

    public bool IsImageFile(string rootPath, string relativePath)
    {
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        return new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".svg", ".webp" }.Contains(ext);
    }

    // Документы, которые рендерим на клиенте (pdf.js / docx-preview / SheetJS).
    // Отдаём их как base64 + mimeType, чтобы фронт собрал Blob и отрисовал, а офлайн-кеш сработал.
    private static readonly Dictionary<string, (string Kind, string Mime)> ViewableDocuments = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = ("pdf", "application/pdf"),
        [".docx"] = ("docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        [".xlsx"] = ("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
    };

    // Предельный размер документа для отдачи base64; больше — только скачивание.
    public const long MaxDocumentBytes = 25 * 1024 * 1024;

    public (string Kind, string Mime)? GetDocumentInfo(string relativePath)
    {
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        return ViewableDocuments.TryGetValue(ext, out var info) ? info : null;
    }

    public long GetFileSize(string rootPath, string relativePath)
    {
        var path = SafeJoin(rootPath, relativePath);
        return new FileInfo(path).Length;
    }

    public string GetFileBase64(string rootPath, string relativePath)
    {
        var path = SafeJoin(rootPath, relativePath);
        return Convert.ToBase64String(File.ReadAllBytes(path));
    }

    public void WriteFile(string rootPath, string relativePath, string content)
    {
        var path = SafeJoin(rootPath, relativePath);
        File.WriteAllText(path, content);
    }

    public void CreateFile(string rootPath, string relativePath)
    {
        var path = SafeJoin(rootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }

    public void CreateDirectory(string rootPath, string relativePath)
    {
        var path = SafeJoin(rootPath, relativePath);
        Directory.CreateDirectory(path);
    }

    public void Delete(string rootPath, string relativePath)
    {
        var path = SafeJoin(rootPath, relativePath);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
        else throw new FileNotFoundException();
    }

    public void Rename(string rootPath, string oldRelative, string newRelative)
    {
        var src = SafeJoin(rootPath, oldRelative);
        var dst = SafeJoin(rootPath, newRelative);
        if (Directory.Exists(src)) Directory.Move(src, dst);
        else File.Move(src, dst);
    }

    public string? GetDiff(string rootPath, string relativePath)
    {
        // Пробуем git diff
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", $"diff HEAD -- \"{relativePath}\"")
            {
                WorkingDirectory = rootPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    public bool RevertFile(string rootPath, string relativePath)
    {
        // git checkout HEAD -- file
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", $"checkout HEAD -- \"{relativePath}\"")
            {
                WorkingDirectory = rootPath,
                UseShellExecute = false,
                RedirectStandardError = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private record ModifiedCache(HashSet<string> Files, long ExpiresAt);
    private static readonly Dictionary<string, ModifiedCache> _modifiedCache = new();
    private static readonly Lock _cacheLock = new();

    private static HashSet<string> GetModifiedFiles(string rootPath)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_cacheLock)
        {
            if (_modifiedCache.TryGetValue(rootPath, out var cached) && cached.ExpiresAt > now)
                return cached.Files;
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "status --porcelain")
            {
                WorkingDirectory = rootPath,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) != null)
            {
                if (line.Length < 4) continue;
                var path = line[3..];
                // для переименований: "R  old -> new" берём новый путь
                var arrowIdx = path.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrowIdx >= 0) path = path[(arrowIdx + 4)..];
                result.Add(path.Trim().Replace('\\', '/'));
            }
            proc.WaitForExit(3000);
        }
        catch { }

        var ttl = System.Diagnostics.Stopwatch.Frequency * 5; // 5 секунд
        lock (_cacheLock)
        {
            _modifiedCache[rootPath] = new ModifiedCache(result, now + ttl);
        }
        return result;
    }
}
