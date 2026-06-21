namespace ClaudeCodeServer.Services;

public record FileEntry(string Name, string Path, bool IsDirectory, long? Size, DateTime Modified, bool IsModified);

public class FileService
{
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
                IsGitModified(rootPath, rel)));
        }

        return entries;
    }

    public IEnumerable<FileEntry> Search(string rootPath, string query)
    {
        return Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(100)
            .Select(f =>
            {
                var info = new FileInfo(f);
                var rel = Path.GetRelativePath(rootPath, f).Replace('\\', '/');
                return new FileEntry(info.Name, rel, false, info.Length, info.LastWriteTimeUtc,
                    IsGitModified(rootPath, rel));
            });
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

    private static bool IsGitModified(string rootPath, string relativePath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", $"status --porcelain \"{relativePath}\"")
            {
                WorkingDirectory = rootPath,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(1000);
            return !string.IsNullOrWhiteSpace(output);
        }
        catch { return false; }
    }
}
