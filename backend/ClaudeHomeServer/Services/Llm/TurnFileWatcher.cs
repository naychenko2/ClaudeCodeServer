using System.Collections.Concurrent;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm;

// Следит за изменениями файлов в рабочей папке на время хода и шлёт FileChangedMessage.
// Один экземпляр на сессию: кэш содержимого живёт между ходами, чтобы diff считался
// от последнего известного состояния. Общий для всех адаптеров.
public sealed class TurnFileWatcher(string rootPath, Func<ServerMessage, Task> onMessage) : IDisposable
{
    private FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, string?> _fileCache = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounce = new();

    public void Start()
    {
        if (!Directory.Exists(rootPath)) return;
        // Повторный Start без Stop (новый ход при опоздавшей финализации старого прогона)
        // не должен утекать прежним FileSystemWatcher
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(rootPath)
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
        var fileName = Path.GetFileName(fullPath);
        // Игнорируем .git, временные файлы компиляторов, служебные директории
        var sep = Path.DirectorySeparatorChar;
        if (fullPath.Contains(sep + ".git" + sep) ||
            fullPath.EndsWith(sep + ".git") ||
            fullPath.Contains(sep + ".playwright") ||
            fullPath.Contains(sep + "obj" + sep) ||
            fullPath.Contains(sep + "node_modules" + sep) ||
            // state-файлы скиллов oh-my-claudecode — служебный шум каждого командного хода
            fullPath.Contains(sep + ".omc" + sep + "state" + sep) ||
            fullPath.Contains(sep + ".omc" + sep + "sessions" + sep) ||
            fileName == "project-memory.json" && fullPath.Contains(sep + ".omc" + sep) ||
            fileName.EndsWith("~") ||
            fileName.EndsWith(".tmp") ||
            fileName.Contains(".tmp.")) return;

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

                var rel = Path.GetRelativePath(rootPath, fullPath).Replace('\\', '/');
                var newContent = File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
                _fileCache.TryGetValue(fullPath, out var oldContent);
                _fileCache[fullPath] = newContent;
                var (added, removed) = CountLineDiff(oldContent, newContent);
                if (added == 0 && removed == 0) return;
                _ = onMessage(new FileChangedMessage(rel, added, removed));
            }
            catch { /* файл занят/удалён между событиями watcher-а — пропускаем */ }
        }, TaskScheduler.Default);
    }

    private static (int added, int removed) CountLineDiff(string? oldContent, string? newContent)
    {
        var oldCount = oldContent?.Split('\n').Length ?? 0;
        var newCount = newContent?.Split('\n').Length ?? 0;
        return (Math.Max(0, newCount - oldCount), Math.Max(0, oldCount - newCount));
    }
}
