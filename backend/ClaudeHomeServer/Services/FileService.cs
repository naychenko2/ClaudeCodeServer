namespace ClaudeHomeServer.Services;

public record FileEntry(string Name, string Path, bool IsDirectory, long? Size, DateTime Modified, bool IsModified, string? Synced = null, bool IsNew = false);

public class FileService
{
    // Папки, которые не обходим при рекурсивном Tree (тяжёлые/нерелевантные для офлайна).
    // internal — переиспользуется FileWatcherService для фильтрации событий ФС.
    internal static readonly HashSet<string> TreeExcludes = new(StringComparer.OrdinalIgnoreCase)
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

    // Публичная обёртка SafeJoin для использования вне сборки (WebDav и др.)
    public static string SafeJoinPublic(string root, string relativePath) =>
        SafeJoin(root, relativePath);

    public IEnumerable<FileEntry> List(string rootPath, string relativePath = "", bool showHidden = false)
    {
        var dir = SafeJoin(rootPath, relativePath);
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException();

        var (modified, newFiles) = GetGitStatus(rootPath);
        var entries = new List<FileEntry>();

        foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x))
        {
            var info = new DirectoryInfo(d);
            if (TreeExcludes.Contains(info.Name)) continue;
            if (!showHidden && info.Name.StartsWith('.')) continue;
            entries.Add(new FileEntry(info.Name, Path.GetRelativePath(rootPath, d).Replace('\\', '/'),
                true, null, info.LastWriteTimeUtc, false));
        }

        foreach (var f in Directory.GetFiles(dir).OrderBy(x => x))
        {
            var info = new FileInfo(f);
            if (!showHidden && info.Name.StartsWith('.')) continue;
            var rel = Path.GetRelativePath(rootPath, f).Replace('\\', '/');
            entries.Add(new FileEntry(info.Name, rel, false, info.Length, info.LastWriteTimeUtc,
                modified.Contains(rel), IsNew: newFiles.Contains(rel)));
        }

        return entries;
    }

    public IEnumerable<FileEntry> Search(string rootPath, string query)
    {
        var (modified, newFiles) = GetGitStatus(rootPath);
        return Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(100)
            .Select(f =>
            {
                var info = new FileInfo(f);
                var rel = Path.GetRelativePath(rootPath, f).Replace('\\', '/');
                return new FileEntry(info.Name, rel, false, info.Length, info.LastWriteTimeUtc,
                    modified.Contains(rel), IsNew: newFiles.Contains(rel));
            });
    }

    // Рекурсивный листинг всего поддерева — для prefetch офлайн-снапшота и синхронизации папок.
    // Исключает тяжёлые папки (TreeExcludes), ограничен TreeMaxEntries.
    public IEnumerable<FileEntry> Tree(string rootPath, string relativePath = "", bool showHidden = false)
    {
        var start = SafeJoin(rootPath, relativePath);
        if (!Directory.Exists(start)) throw new DirectoryNotFoundException();

        var (modified, newFiles) = GetGitStatus(rootPath);
        var result = new List<FileEntry>();

        void Walk(string dir)
        {
            if (result.Count >= TreeMaxEntries) return;

            foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x))
            {
                if (result.Count >= TreeMaxEntries) return;
                var info = new DirectoryInfo(d);
                if (TreeExcludes.Contains(info.Name)) continue;
                if (!showHidden && info.Name.StartsWith('.')) continue;
                result.Add(new FileEntry(info.Name, Path.GetRelativePath(rootPath, d).Replace('\\', '/'),
                    true, null, info.LastWriteTimeUtc, false));
                Walk(d);
            }

            foreach (var f in Directory.GetFiles(dir).OrderBy(x => x))
            {
                if (result.Count >= TreeMaxEntries) return;
                var info = new FileInfo(f);
                if (!showHidden && info.Name.StartsWith('.')) continue;
                var rel = Path.GetRelativePath(rootPath, f).Replace('\\', '/');
                result.Add(new FileEntry(info.Name, rel, false, info.Length, info.LastWriteTimeUtc,
                    modified.Contains(rel), IsNew: newFiles.Contains(rel)));
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
            ".mp3", ".mp4", ".avi", ".mov", ".wasm", ".so", ".dylib",
            ".ppt" };
        return binaryExts.Contains(ext);
    }

    public bool IsImageFile(string rootPath, string relativePath)
    {
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        return new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".svg", ".webp" }.Contains(ext);
    }

    public static bool IsVideoFile(string relativePath)
    {
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        return new[] { ".mp4", ".webm", ".mov", ".avi", ".mkv" }.Contains(ext);
    }

    public static bool IsAudioFile(string relativePath)
    {
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        return new[] { ".mp3", ".wav", ".ogg", ".flac", ".aac", ".m4a", ".opus", ".weba" }.Contains(ext);
    }

    public byte[] ReadFileBytes(string rootPath, string relativePath)
    {
        var path = SafeJoin(rootPath, relativePath);
        return File.ReadAllBytes(path);
    }

    // Документы: PDF рендерится на клиенте (pdf.js), Office-форматы — через OnlyOffice DS.
    private static readonly Dictionary<string, (string Kind, string Mime)> ViewableDocuments = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"]  = ("pdf",  "application/pdf"),
        [".docx"] = ("docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        [".xlsx"] = ("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        [".pptx"] = ("pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
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

    public void WriteFileBytes(string rootPath, string relativePath, byte[] content)
    {
        var path = SafeJoin(rootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
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
        if (!IsGitRepo(rootPath)) return null;
        try
        {
            // diff рабочего дерева vs HEAD (покрывает изменённые отслеживаемые файлы)
            var output = GitRun(rootPath, $"diff HEAD -- \"{relativePath}\"");
            // Если пусто — файл может быть новым в индексе (git add, но ещё не commit)
            if (string.IsNullOrWhiteSpace(output))
                output = GitRun(rootPath, $"diff --cached -- \"{relativePath}\"");
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    private static string GitRun(string rootPath, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = rootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(3000);
        return output;
    }

    public bool RevertFile(string rootPath, string relativePath)
    {
        if (!IsGitRepo(rootPath)) return false;
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

    private record GitStatusCache(HashSet<string> Modified, HashSet<string> New, long ExpiresAt);
    private static readonly Dictionary<string, GitStatusCache> _statusCache = new();
    private static readonly Lock _cacheLock = new();

    // Кеш признака «папка — git-репо». Меняется редко (git init), TTL длиннее статуса.
    private record GitRepoCache(bool IsRepo, long ExpiresAt);
    private static readonly Dictionary<string, GitRepoCache> _repoCache = new();

    private static bool IsGitRepo(string rootPath)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_cacheLock)
        {
            if (_repoCache.TryGetValue(rootPath, out var cached) && cached.ExpiresAt > now)
                return cached.IsRepo;
        }
        // .git — папка (обычный репо) или файл-указатель (worktree/submodule)
        var isRepo = Path.Exists(Path.Combine(rootPath, ".git"));
        var ttl = System.Diagnostics.Stopwatch.Frequency * 60; // 60 секунд
        lock (_cacheLock)
        {
            _repoCache[rootPath] = new GitRepoCache(isRepo, now + ttl);
        }
        return isRepo;
    }

    private static (HashSet<string> modified, HashSet<string> @new) GetGitStatus(string rootPath)
    {
        // Не git-репо — не спавним git (иначе на каждый листинг летит
        // `fatal: not a git repository` в stderr и плодятся процессы)
        if (!IsGitRepo(rootPath))
            return (new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_cacheLock)
        {
            if (_statusCache.TryGetValue(rootPath, out var cached) && cached.ExpiresAt > now)
                return (cached.Modified, cached.New);
        }

        var modified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var @new = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                if (line[0] == '!' && line[1] == '!') continue; // ignored
                var path = line[3..];
                // для переименований: "R  old -> new" берём новый путь
                var arrowIdx = path.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrowIdx >= 0) path = path[(arrowIdx + 4)..];
                var normalizedPath = path.Trim().Replace('\\', '/');
                if (line[0] == '?' && line[1] == '?')
                    @new.Add(normalizedPath);
                else
                    modified.Add(normalizedPath);
            }
            proc.WaitForExit(3000);
        }
        catch { }

        var ttl = System.Diagnostics.Stopwatch.Frequency * 5; // 5 секунд
        lock (_cacheLock)
        {
            _statusCache[rootPath] = new GitStatusCache(modified, @new, now + ttl);
        }
        return (modified, @new);
    }
}
