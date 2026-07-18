using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm;

// Настройки шумоподавления ватчера (из секции конфига FileWatcher). Дефолты покрывают
// служебные каталоги инструментов (.omc, .claude), артефакты сборки и временные файлы,
// чтобы командные ходы (OmO, workflow) не спамили ленту чата чужими изменениями.
public sealed record FileWatcherOptions(
    IReadOnlyList<string> IgnoreDirs,
    IReadOnlyList<string> IgnoreFilePatterns,
    bool RespectGitignore)
{
    public static readonly FileWatcherOptions Default = new(
        IgnoreDirs: [".git", ".omc", ".claude", "node_modules", "obj", "bin", "dist", ".vs", ".idea", ".playwright"],
        IgnoreFilePatterns: ["*~", "*.tmp", "*.tmp.*"],
        RespectGitignore: true);
}

// Следит за изменениями файлов в рабочей папке на время хода и шлёт FileChangedMessage.
// Один экземпляр на сессию: кэш содержимого живёт между ходами, чтобы diff считался
// от последнего известного состояния. Общий для всех адаптеров.
public sealed class TurnFileWatcher : IDisposable
{
    private readonly string _rootPath;
    private readonly Func<ServerMessage, Task> _onMessage;
    private readonly FileWatcherOptions _options;
    private readonly HashSet<string> _ignoreDirs;
    private FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, string?> _fileCache = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounce = new();
    // Кэш вердикта git check-ignore по полному пути (живёт на сессию — файлы те же
    // ход за ходом, а запуск git-процесса на каждое событие дорог)
    private readonly ConcurrentDictionary<string, bool> _gitIgnoreCache = new();
    private readonly bool _isGitRepo;

    public TurnFileWatcher(string rootPath, Func<ServerMessage, Task> onMessage, FileWatcherOptions? options = null)
    {
        _rootPath = rootPath;
        _onMessage = onMessage;
        _options = options ?? FileWatcherOptions.Default;
        _ignoreDirs = new HashSet<string>(_options.IgnoreDirs, StringComparer.OrdinalIgnoreCase);
        // Дешёвая проверка «это git-репо»: .git — каталог (обычный клон) или файл
        // (worktree/submodule). Без неё git check-ignore в не-git папке впустую
        // плодит процессы с кодом 128 на каждый новый путь.
        var gitDir = Path.Combine(rootPath, ".git");
        _isGitRepo = Directory.Exists(gitDir) || File.Exists(gitDir);
    }

    public void Start()
    {
        if (!Directory.Exists(_rootPath)) return;
        // Повторный Start без Stop (новый ход при опоздавшей финализации старого прогона)
        // не должен утекать прежним FileSystemWatcher
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(_rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileSystemEvent;
        _watcher.Created += OnFileSystemEvent;
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        foreach (var cts in _debounce.Values) cts.Cancel();
        _debounce.Clear();
    }

    public void Dispose() => Stop();

    private void OnFileSystemEvent(object _, FileSystemEventArgs e)
    {
        var fullPath = e.FullPath;
        // Дешёвый чёрный список (каталоги/маски имён) — до debounce и запуска git
        if (ShouldIgnore(fullPath)) return;

        if (_debounce.TryRemove(fullPath, out var old)) old.Cancel();
        var cts = new CancellationTokenSource();
        _debounce[fullPath] = cts;

        Task.Delay(400, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _debounce.TryRemove(fullPath, out CancellationTokenSource? _);
            try
            {
                if (!File.Exists(fullPath) && !_fileCache.ContainsKey(fullPath)) return;
                // .gitignore проверяем после debounce (реже) и до чтения файла
                if (IsGitIgnored(fullPath)) return;

                var rel = Path.GetRelativePath(_rootPath, fullPath).Replace('\\', '/');
                var newContent = File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
                _fileCache.TryGetValue(fullPath, out var oldContent);
                _fileCache[fullPath] = newContent;
                var (added, removed) = CountLineDiff(oldContent, newContent);
                if (added == 0 && removed == 0) return;
                _ = _onMessage(new FileChangedMessage(rel, added, removed));
            }
            catch { /* файл занят/удалён между событиями watcher-а — пропускаем */ }
        }, TaskScheduler.Default);
    }

    // Игнор по служебным каталогам-сегментам пути и маскам имени файла (из конфига).
    private bool ShouldIgnore(string fullPath)
    {
        var rel = Path.GetRelativePath(_rootPath, fullPath);
        // Путь вне rootPath (GetRelativePath вернул абсолютный/«..») — не наш, игнор
        if (Path.IsPathRooted(rel) || rel.StartsWith("..")) return true;
        var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Любой каталог-сегмент (кроме последнего — имени файла) в чёрном списке
        for (var i = 0; i < segments.Length - 1; i++)
            if (_ignoreDirs.Contains(segments[i])) return true;
        var fileName = segments[^1];
        foreach (var pattern in _options.IgnoreFilePatterns)
            if (FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true)) return true;
        return false;
    }

    // Игнорируется ли путь git-ом (git check-ignore). Только в git-репо и при
    // включённой опции; вердикт кэшируется на сессию.
    private bool IsGitIgnored(string fullPath)
    {
        if (!_options.RespectGitignore || !_isGitRepo) return false;
        return _gitIgnoreCache.GetOrAdd(fullPath, p =>
        {
            try
            {
                var psi = new ProcessStartInfo("git")
                {
                    WorkingDirectory = _rootPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var a in new[] { "check-ignore", "-q", p }) psi.ArgumentList.Add(a);
                using var proc = Process.Start(psi)!;
                if (!proc.WaitForExit(1500))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return false;
                }
                // exit 0 — путь игнорируется; 1 — нет; 128 — ошибка/не репо
                return proc.ExitCode == 0;
            }
            catch { return false; }
        });
    }

    private static (int added, int removed) CountLineDiff(string? oldContent, string? newContent)
    {
        var oldCount = oldContent?.Split('\n').Length ?? 0;
        var newCount = newContent?.Split('\n').Length ?? 0;
        return (Math.Max(0, newCount - oldCount), Math.Max(0, oldCount - newCount));
    }
}
