namespace ClaudeHomeServer.Services;

public record FileEntry(string Name, string Path, bool IsDirectory, long? Size, DateTime Modified, bool IsModified, string? Synced = null, bool IsNew = false);

// Вид мутации файла через файловый сервис — для подписчиков OnMutated
public enum FileMutationKind { Write, Create, Delete, Rename }

public class FileService(
    ClaudeHomeServer.Services.Git.GitService? git = null,
    ProjectManager? projects = null)
{
    // git/projects опциональны (DI подставляет): git-операции идут через слой Execution
    // с резолвом владельца по корню — статусы/дифф/револт честны и для container-юзеров.
    // Без них (юнит-тесты) — прежний прямой запуск git на хосте.

    // Владелец по корню проекта: у соседей по папке владелец один по построению
    private string? OwnerOf(string rootPath) =>
        projects?.GetByRootPath(rootPath).FirstOrDefault()?.OwnerId;

    // Мутации через файловый API (UI, OnlyOffice, upload; правки Claude идут мимо — их ловят
    // ватчеры). Подписчик — ProjectKnowledgeSyncService (синк базы знаний).
    // Аргументы: root, относительный путь, вид, новый путь (только для Rename).
    public event Action<string, string, FileMutationKind, string?>? OnMutated;

    // Уведомление подписчиков; сбой подписчика не должен ронять файловую операцию.
    // internal — дёргают и точки записи мимо FileService (Upload/SaveFromUrl в FilesController).
    internal void NotifyMutated(string root, string rel, FileMutationKind kind, string? newRel = null)
    {
        try { OnMutated?.Invoke(root, rel, kind, newRel); }
        catch { /* синк знаний best-effort */ }
    }

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
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Сравнение с разделителем на конце: иначе root "C:\Data\Proj" пропускает "C:\Data\Proj2\..."
        if (!full.Equals(rootFull, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Доступ за пределы проекта запрещён");
        return full;
    }

    // Публичная обёртка SafeJoin для использования вне сборки (WebDav и др.)
    public static string SafeJoinPublic(string root, string relativePath) =>
        SafeJoin(root, relativePath);

    public IEnumerable<FileEntry> List(string rootPath, string relativePath = "", bool showHidden = false)
    {
        var dir = SafeJoin(rootPath, relativePath);
        if (!Directory.Exists(dir))
        {
            // Виртуальная папка заметок: показываем notes/ в дереве всегда, физически
            // она появляется при первой заметке (NotesService.Create). Раскрытие
            // несозданной папки — пустой список, не 404.
            if (IsNotesPath(relativePath)) return [];
            throw new DirectoryNotFoundException();
        }

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

        // Папка заметок в корне проекта присутствует всегда (даже если ещё не создана
        // физически) — vault проекта виден и до первой заметки.
        if (string.IsNullOrEmpty(relativePath) &&
            !entries.Any(e => e.IsDirectory && e.Name.Equals("notes", StringComparison.OrdinalIgnoreCase)))
        {
            entries.Insert(0, new FileEntry("notes", "notes", true, null, DateTime.UtcNow, false));
        }

        return entries;
    }

    // Путь указывает на папку заметок проекта (сам notes/ или внутри неё)
    private static bool IsNotesPath(string relativePath)
    {
        var norm = relativePath.Replace('\\', '/').Trim('/');
        return norm.Equals("notes", StringComparison.OrdinalIgnoreCase) ||
               norm.StartsWith("notes/", StringComparison.OrdinalIgnoreCase);
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
        // Visio-диаграммы: OnlyOffice DS открывает их только на просмотр (Diagram Viewer, DS 9.0+)
        [".vsdx"] = ("visio", "application/vnd.ms-visio.drawing"),
        [".vsdm"] = ("visio", "application/vnd.ms-visio.drawing.macroenabled.12"),
        [".vssx"] = ("visio", "application/vnd.ms-visio.stencil"),
        [".vssm"] = ("visio", "application/vnd.ms-visio.stencil.macroenabled.12"),
        [".vstx"] = ("visio", "application/vnd.ms-visio.template"),
        [".vstm"] = ("visio", "application/vnd.ms-visio.template.macroenabled.12"),
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
        NotifyMutated(rootPath, relativePath, FileMutationKind.Write);
    }

    public void WriteFileBytes(string rootPath, string relativePath, byte[] content)
    {
        var path = SafeJoin(rootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        NotifyMutated(rootPath, relativePath, FileMutationKind.Write);
    }

    public void CreateFile(string rootPath, string relativePath)
    {
        var path = SafeJoin(rootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        NotifyMutated(rootPath, relativePath, FileMutationKind.Create);
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
        NotifyMutated(rootPath, relativePath, FileMutationKind.Delete);
    }

    public void Rename(string rootPath, string oldRelative, string newRelative)
    {
        var src = SafeJoin(rootPath, oldRelative);
        var dst = SafeJoin(rootPath, newRelative);
        if (Directory.Exists(src)) Directory.Move(src, dst);
        else File.Move(src, dst);
        NotifyMutated(rootPath, oldRelative, FileMutationKind.Rename, newRelative);
    }

    public string? GetDiff(string rootPath, string relativePath)
    {
        if (!IsGitRepo(rootPath)) return null;
        try
        {
            // Путь через SafeJoin — валидация до передачи в git
            SafeJoin(rootPath, relativePath);
            // diff рабочего дерева vs HEAD (покрывает изменённые отслеживаемые файлы)
            var output = RunGit(rootPath, "diff", "HEAD", "--", relativePath);
            // Если пусто — файл может быть новым в индексе (git add, но ещё не commit)
            if (string.IsNullOrWhiteSpace(output))
                output = RunGit(rootPath, "diff", "--cached", "--", relativePath);
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    // Запуск git с учётом среды владельца (Execution через GitService); без DI — прежний хостовый
    private string RunGit(string rootPath, params string[] args) =>
        git is not null
            ? git.RunAsync(OwnerOf(rootPath), rootPath, args).GetAwaiter().GetResult().Stdout
            : GitRun(rootPath, args);

    /// <summary>
    /// Последние коммиты репозитория (сырье для продуктовой сводки). Алиасы авторов:
    /// map email → отображаемое имя; нет совпадения — остается git user.name.
    /// projectName проставляется в каждый коммит — для агрегации по всем проектам.
    /// </summary>
    public List<Models.GitCommitRaw> GetCommitsRaw(string rootPath, string projectName = "", int limit = 200, IReadOnlyDictionary<string, string>? authorAliases = null)
    {
        if (!IsGitRepo(rootPath)) return [];
        try
        {
            // Аргументы передаём раздельно (защита от инъекции); %x1f/%x1e —
            // unit/record separators: subject и body могут содержать переводы строк
            var output = GitRun(rootPath, "log", "-n", limit.ToString(),
                "--pretty=format:%H%x1f%an%x1f%ae%x1f%aI%x1f%s%x1f%b%x1e");
            var commits = new List<Models.GitCommitRaw>();
            foreach (var record in output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = record.Trim('\n', '\r').Split('\x1f');
                if (f.Length < 6) continue;
                if (!DateTimeOffset.TryParse(f[3], out var date)) continue;
                var name = f[1];
                var email = f[2];
                if (authorAliases != null && authorAliases.TryGetValue(email, out var alias))
                    name = alias;
                commits.Add(new Models.GitCommitRaw(f[0], name, email, date, f[4], f[5].Trim(), projectName));
            }
            return commits;
        }
        catch { return []; }
    }

    private static string GitRun(string rootPath, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = rootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // git выводит UTF-8; без явной кодировки .NET читает в системной (OEM/ANSI)
            // и кириллица в сообщениях коммитов превращается в кракозябры (особенно на проде)
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = System.Diagnostics.Process.Start(psi)!;
        // stderr читаем асинхронно, чтобы многословный git не забил буфер и не подвесил ReadToEnd
        proc.BeginErrorReadLine();
        var output = proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(3000))
            try { proc.Kill(entireProcessTree: true); } catch { /* уже завершился */ }
        return output;
    }

    public bool RevertFile(string rootPath, string relativePath)
    {
        if (!IsGitRepo(rootPath)) return false;
        // git checkout HEAD -- file
        try
        {
            SafeJoin(rootPath, relativePath);
            if (git is not null)
            {
                // Через слой Execution (container-юзеры) — DiscardAsync бросает при ошибке
                git.DiscardAsync(OwnerOf(rootPath), rootPath, relativePath).GetAwaiter().GetResult();
                // Откат меняет содержимое файла — подписчики (синк знаний) должны узнать
                NotifyMutated(rootPath, relativePath, FileMutationKind.Write);
                return true;
            }
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = rootPath,
                UseShellExecute = false,
                RedirectStandardError = true
            };
            foreach (var a in new[] { "checkout", "HEAD", "--", relativePath }) psi.ArgumentList.Add(a);
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.BeginErrorReadLine();
            if (!proc.WaitForExit(3000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            // Откат меняет содержимое файла — подписчики (синк знаний) должны узнать
            if (proc.ExitCode == 0) NotifyMutated(rootPath, relativePath, FileMutationKind.Write);
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

    private (HashSet<string> modified, HashSet<string> @new) GetGitStatus(string rootPath)
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
            if (git is not null)
            {
                // Через слой Execution: для container-юзеров git выполняется в песочнице
                // по правильному дереву (владелец резолвится по корню проекта)
                var st = git.StatusAsync(OwnerOf(rootPath), rootPath).GetAwaiter().GetResult();
                foreach (var f in st.Staged) modified.Add(f.Path.Replace('\\', '/'));
                foreach (var f in st.Unstaged) modified.Add(f.Path.Replace('\\', '/'));
                foreach (var f in st.Untracked) @new.Add(f.Path.Replace('\\', '/'));
            }
            else
            {
                // Фолбэк без DI (юнит-тесты): прежний прямой запуск git на хосте
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
