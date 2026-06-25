using System.Collections.Concurrent;
using ClaudeHomeServer.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

// Следит за файлами проекта, пока к нему подключён хотя бы один клиент (ref-count по connectionId),
// и шлёт в группу "project_{id}" событие "filesChanged" { projectId, paths } с дебаунсом.
// События тяжёлых/нерелевантных папок (.git, node_modules, bin, obj, …) отфильтрованы.
public class FileWatcherService : IDisposable
{
    private const int DebounceMs = 400;
    private const int MaxPaths = 200;

    private class Entry
    {
        public FileSystemWatcher? Watcher;
        public string Root = "";
        public readonly HashSet<string> Connections = new();
        public readonly HashSet<string> PendingPaths = new(StringComparer.OrdinalIgnoreCase);
        public Timer? Debounce;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();             // projectId -> Entry
    private readonly ConcurrentDictionary<string, HashSet<string>> _byConnection = new(); // connId -> projectIds
    private readonly ProjectManager _projects;
    private readonly IHubContext<SessionHub> _hub;
    private readonly Lock _lock = new();

    public FileWatcherService(ProjectManager projects, IHubContext<SessionHub> hub)
    {
        _projects = projects;
        _hub = hub;
    }

    // Клиент начал смотреть проект — поднимаем watcher (или увеличиваем ref-count)
    public void Watch(string projectId, string connectionId)
    {
        var project = _projects.GetById(projectId);
        if (project is null || !Directory.Exists(project.RootPath)) return;

        lock (_lock)
        {
            var entry = _entries.GetOrAdd(projectId, _ => new Entry { Root = project.RootPath });
            entry.Connections.Add(connectionId);
            _byConnection.GetOrAdd(connectionId, _ => new HashSet<string>()).Add(projectId);
            if (entry.Watcher is null)
                entry.Watcher = CreateWatcher(projectId, entry);
        }
    }

    // Клиент перестал смотреть проект
    public void Unwatch(string projectId, string connectionId)
    {
        lock (_lock)
        {
            if (_byConnection.TryGetValue(connectionId, out var set)) set.Remove(projectId);
            if (_entries.TryGetValue(projectId, out var entry))
            {
                entry.Connections.Remove(connectionId);
                if (entry.Connections.Count == 0) DisposeEntry(projectId, entry);
            }
        }
    }

    // Клиент отключился — снимаем все его watch'и
    public void RemoveConnection(string connectionId)
    {
        lock (_lock)
        {
            if (!_byConnection.TryRemove(connectionId, out var projectIds)) return;
            foreach (var pid in projectIds)
            {
                if (_entries.TryGetValue(pid, out var entry))
                {
                    entry.Connections.Remove(connectionId);
                    if (entry.Connections.Count == 0) DisposeEntry(pid, entry);
                }
            }
        }
    }

    private FileSystemWatcher CreateWatcher(string projectId, Entry entry)
    {
        var w = new FileSystemWatcher(entry.Root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        void OnChange(object _, FileSystemEventArgs e) => OnFsEvent(projectId, entry, e.FullPath);
        w.Created += OnChange;
        w.Changed += OnChange;
        w.Deleted += OnChange;
        w.Renamed += (_, e) => { OnFsEvent(projectId, entry, e.FullPath); OnFsEvent(projectId, entry, e.OldFullPath); };
        w.Error += (_, _) => RecreateWatcher(projectId, entry);
        try { w.EnableRaisingEvents = true; } catch { /* недоступный путь — оставим без watcher */ }
        return w;
    }

    private void OnFsEvent(string projectId, Entry entry, string fullPath)
    {
        string rel;
        try { rel = Path.GetRelativePath(entry.Root, fullPath).Replace('\\', '/'); }
        catch { return; }
        if (rel.Length == 0 || rel == "." || IsExcluded(rel)) return;

        lock (_lock)
        {
            entry.PendingPaths.Add(rel);
            if (entry.Debounce is null)
                entry.Debounce = new Timer(_ => Flush(projectId, entry), null, DebounceMs, Timeout.Infinite);
            else
                entry.Debounce.Change(DebounceMs, Timeout.Infinite);
        }
    }

    // Любой сегмент пути в списке исключений → игнорируем
    private static bool IsExcluded(string rel)
    {
        foreach (var seg in rel.Split('/'))
            if (FileService.TreeExcludes.Contains(seg)) return true;
        return false;
    }

    private void Flush(string projectId, Entry entry)
    {
        string[] paths;
        lock (_lock)
        {
            if (entry.PendingPaths.Count == 0) return;
            paths = entry.PendingPaths.Take(MaxPaths).ToArray();
            entry.PendingPaths.Clear();
        }
        _ = _hub.Clients.Group("project_" + projectId)
            .SendAsync("filesChanged", new { projectId, paths });
    }

    private void RecreateWatcher(string projectId, Entry entry)
    {
        lock (_lock)
        {
            try { entry.Watcher?.Dispose(); } catch { }
            entry.Watcher = CreateWatcher(projectId, entry);
        }
    }

    private void DisposeEntry(string projectId, Entry entry)
    {
        try { entry.Watcher?.Dispose(); } catch { }
        entry.Debounce?.Dispose();
        _entries.TryRemove(projectId, out _);
    }

    public void Dispose()
    {
        foreach (var e in _entries.Values)
        {
            try { e.Watcher?.Dispose(); } catch { }
            e.Debounce?.Dispose();
        }
        _entries.Clear();
    }
}
