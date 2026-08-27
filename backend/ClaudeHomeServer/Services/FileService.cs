namespace ClaudeHomeServer.Services;

public record FileEntry(string Name, string Path, bool IsDirectory, long? Size, DateTime Modified, bool IsModified, string? Synced = null, bool IsNew = false);

// Вид мутации файла через файловый сервис — для подписчиков OnMutated
public enum FileMutationKind { Write, Create, Delete, Rename }

public class FileService(
    ClaudeHomeServer.Services.Git.GitService? git = null,
    ProjectManager? projects = null,
    ILogger<FileService>? logger = null)
{
    private readonly ILogger<FileService>? _logger = logger;

    // git/projects/logger опциональны (DI подставляет): git-операции идут через слой Execution
    // с резолвом владельца по корню — статусы/дифф/револт честны и для container-юзеров.
    // Без них (юнит-тесты) — прежний прямой запуск git на хосте и тихое логирование.

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

    // Папка вложений чата в рабочей папке (файлы, загруженные в сообщение с компьютера).
    // Служебная: исключена из дерева, ватчеров, дефолтного .gitignore и синка базы знаний.
    public const string AttachmentsDir = ".cc-attachments";

    // Папки, которые не обходим при рекурсивном Tree (тяжёлые/нерелевантные для офлайна).
    // internal — переиспользуется FileWatcherService для фильтрации событий ФС.
    internal static readonly HashSet<string> TreeExcludes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "dev-dist",
        ".vs", ".idea", "publish", ".next", "target", ".cache",
        AttachmentsDir,
    };

    // Предохранитель от патологически больших деревьев
    private const int TreeMaxEntries = 20000;

    // .claude/worktrees/<имя> — полные копии репозитория (git worktree). При showHidden=true
    // рекурсия в них раздувает дерево и вытесняет настоящие файлы проекта из TreeMaxEntries.
    // Проверка по относительному пути, а не по имени "worktrees" — обычная папка с таким
    // именем в другом месте проекта исключаться не должна.
    private static bool IsWorktreesPath(string relativePath) =>
        relativePath.Equals(".claude/worktrees", StringComparison.OrdinalIgnoreCase) ||
        relativePath.StartsWith(".claude/worktrees/", StringComparison.OrdinalIgnoreCase);

    // Защита от path traversal.
    // ВАЖНО: второй аргумент — путь ОТНОСИТЕЛЬНО корня; ведущие разделители срезаются.
    // Абсолютный путь сюда передавать нельзя: на Linux «/a/b» станет относительным «a/b»
    // и приклеится к корню — вместо отказа получится путь внутри проекта, то есть проверка
    // «ссылка наружу» молча исчезнет. На Windows подмена незаметна (Path.Combine отдаёт
    // приоритет второму абсолютному пути), поэтому такое ловится только в CI на Linux.
    // Есть абсолютный путь — сначала Path.GetRelativePath(root, full).
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
            // Чтение метаданных одной записи может бросить IOException/UnauthorizedAccessException
            // (резервные DOS-имена вроде «nul», гонка «удалили между GetFiles и FileInfo», битые
            // reparse-точки). Запись пропускается — иначе упадёт весь листинг папки.
            string name;
            DateTime modifiedAt;
            string relDir;
            try
            {
                var info = new DirectoryInfo(d);
                name = info.Name;
                modifiedAt = info.LastWriteTimeUtc;
                relDir = Path.GetRelativePath(rootPath, d).Replace('\\', '/');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogDebug(ex, "Пропуск подкаталога: не удалось прочитать метаданные ({Path})", d);
                continue;
            }
            if (TreeExcludes.Contains(name)) continue;
            if (!showHidden && name.StartsWith('.')) continue;
            entries.Add(new FileEntry(name, relDir, true, null, modifiedAt, false));
        }

        foreach (var f in Directory.GetFiles(dir).OrderBy(x => x))
        {
            // см. комментарий в цикле директорий выше
            string name;
            DateTime modifiedAt;
            long size;
            string rel;
            try
            {
                var info = new FileInfo(f);
                name = info.Name;
                size = info.Length;
                modifiedAt = info.LastWriteTimeUtc;
                rel = Path.GetRelativePath(rootPath, f).Replace('\\', '/');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogDebug(ex, "Пропуск файла: не удалось прочитать метаданные ({Path})", f);
                continue;
            }
            if (!showHidden && name.StartsWith('.')) continue;
            entries.Add(new FileEntry(name, rel, false, size, modifiedAt,
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
                // см. комментарий в List: одна битая запись не должна ронять весь поиск
                try
                {
                    var info = new FileInfo(f);
                    var rel = Path.GetRelativePath(rootPath, f).Replace('\\', '/');
                    return new FileEntry(info.Name, rel, false, info.Length, info.LastWriteTimeUtc,
                        modified.Contains(rel), IsNew: newFiles.Contains(rel));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger?.LogDebug(ex, "Пропуск файла при поиске: не удалось прочитать метаданные ({Path})", f);
                    return null;
                }
            })
            .Where(e => e is not null)
            .Select(e => e!);
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
                // см. комментарий в List: битая запись пропускается, иначе упадёт всё дерево
                string name;
                DateTime modifiedAt;
                string relDir;
                try
                {
                    var info = new DirectoryInfo(d);
                    name = info.Name;
                    modifiedAt = info.LastWriteTimeUtc;
                    relDir = Path.GetRelativePath(rootPath, d).Replace('\\', '/');
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger?.LogDebug(ex, "Пропуск подкаталога в Tree: не удалось прочитать метаданные ({Path})", d);
                    continue;
                }
                if (TreeExcludes.Contains(name)) continue;
                if (!showHidden && name.StartsWith('.')) continue;
                if (IsWorktreesPath(relDir)) continue;
                result.Add(new FileEntry(name, relDir, true, null, modifiedAt, false));
                Walk(d);
            }

            foreach (var f in Directory.GetFiles(dir).OrderBy(x => x))
            {
                if (result.Count >= TreeMaxEntries) return;
                // см. комментарий выше
                string name;
                DateTime modifiedAt;
                long size;
                string rel;
                try
                {
                    var info = new FileInfo(f);
                    name = info.Name;
                    size = info.Length;
                    modifiedAt = info.LastWriteTimeUtc;
                    rel = Path.GetRelativePath(rootPath, f).Replace('\\', '/');
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger?.LogDebug(ex, "Пропуск файла в Tree: не удалось прочитать метаданные ({Path})", f);
                    continue;
                }
                if (!showHidden && name.StartsWith('.')) continue;
                result.Add(new FileEntry(name, rel, false, size, modifiedAt,
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
        [".pdf"] = ("pdf", "application/pdf"),
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

    public void CreateFile(string rootPath, string relativePath) => CreateFile(rootPath, relativePath, "");

    // Создание с начальным содержимым (диаграммы из меню «+»: шаблон пишется одним
    // запросом, чтобы сбой не оставлял пустой файл). Коллизия — ошибка, а не тихая
    // перезапись: File.WriteAllText молча стёр бы существующий файл.
    // Guard best-effort: Exists+WriteAllText не атомарны, при двух одновременных
    // create на один путь победит один из них — 409 это подсказка UI, не гарантия.
    public void CreateFile(string rootPath, string relativePath, string content)
    {
        var path = SafeJoin(rootPath, relativePath);
        if (File.Exists(path)) throw new InvalidOperationException("файл уже существует");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
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
    // Идущие прямо сейчас вычисления статуса по rootPath — single-flight: при монтировании
    // панели фронт шлёт пачку параллельных листингов (корень + восстановленные раскрытые
    // папки + полное дерево), и все они обязаны ждать ОДИН запуск git status, а не делать
    // одинаковую работу независимо.
    private static readonly Dictionary<string, Lazy<(HashSet<string> modified, HashSet<string> @new)>>
        _statusInFlight = new();
    private static readonly Lock _cacheLock = new();

    // Подмена вычислителя git-статуса в тестах (счётчик вызовов для single-flight);
    // null — штатный путь через GitService / прямой запуск git.
    internal Func<string, (HashSet<string> modified, HashSet<string> @new)>? GitStatusComputer { get; set; }

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
        Lazy<(HashSet<string> modified, HashSet<string> @new)> flight;
        lock (_cacheLock)
        {
            if (_statusCache.TryGetValue(rootPath, out var cached) && cached.ExpiresAt > now)
                return (cached.Modified, cached.New);
            if (!_statusInFlight.TryGetValue(rootPath, out flight!))
            {
                // ExecutionAndPublication: фабрику исполняет ровно один поток,
                // остальные блокируются на flight.Value до её результата
                flight = new Lazy<(HashSet<string>, HashSet<string>)>(
                    () => ComputeGitStatus(rootPath),
                    System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
                _statusInFlight[rootPath] = flight;
            }
        }

        try
        {
            var result = flight.Value;
            var ttl = System.Diagnostics.Stopwatch.Frequency * 5; // 5 секунд
            lock (_cacheLock)
            {
                _statusCache[rootPath] = new GitStatusCache(result.modified, result.@new,
                    System.Diagnostics.Stopwatch.GetTimestamp() + ttl);
            }
            return result;
        }
        finally
        {
            // Снимаем flight по ссылке: запись мог уже заменить следующий промах кэша
            lock (_cacheLock)
            {
                if (_statusInFlight.TryGetValue(rootPath, out var cur) && ReferenceEquals(cur, flight))
                    _statusInFlight.Remove(rootPath);
            }
        }
    }

    private (HashSet<string> modified, HashSet<string> @new) ComputeGitStatus(string rootPath)
    {
        if (GitStatusComputer is not null)
            return GitStatusComputer(rootPath);

        var modified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var @new = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (git is not null)
            {
                // Через слой Execution: для container-юзеров git выполняется в песочнице
                // по правильному дереву (владелец резолвится по корню проекта).
                // Лёгкий StatusPathsAsync — только пути, один запуск git status: тяжёлый
                // StatusAsync с построчной статистикой здесь не нужен, числа выбрасывались.
                var st = git.StatusPathsAsync(OwnerOf(rootPath), rootPath).GetAwaiter().GetResult();
                foreach (var f in st.Staged) modified.Add(f.Path.Replace('\\', '/'));
                foreach (var f in st.Unstaged) modified.Add(f.Path.Replace('\\', '/'));
                foreach (var f in st.Untracked) @new.Add(f.Path.Replace('\\', '/'));
            }
            else
            {
                // Фолбэк без DI (юнит-тесты): прежний прямой запуск git на хосте
                // -uall — как в GitService.StatusPathsAsync: иначе новая папка приходит одной записью
                // «dir/», и файлы внутри неё не помечаются новыми в дереве
                var psi = new System.Diagnostics.ProcessStartInfo("git", "status --porcelain -uall")
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

        return (modified, @new);
    }
}
