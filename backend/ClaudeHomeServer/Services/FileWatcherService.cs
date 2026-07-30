using System.Collections.Concurrent;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Services.CodeGraph;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

// Следит за файлами проекта, пока к нему подключён хотя бы один клиент (ref-count по connectionId),
// и шлёт в группу "project_{id}" событие "filesChanged" { projectId, paths } с дебаунсом.
// События тяжёлых/нерелевантных папок (.git, node_modules, bin, obj, …) отфильтрованы.
//
// Второй вид watcher'ов — по произвольному пути (WatchPath/UnwatchPath, ключ "worktree:{sessionId}"):
// отдельное дерево чата лежит вне RootPath проекта, поэтому проектный watcher его не видит,
// и граф кода worktree не обновлялся бы никогда (ADR-003). Такой watcher SignalR/знания не трогает —
// только реактивный триггер CodeGraph.
public class FileWatcherService : IDisposable
{
    private const int DebounceMs = 400;
    private const int MaxPaths = 200;
    // Защита от зависания обхода на огромных деревьях в polling-режиме.
    private const int SnapshotMaxEntries = 20000;
    // Буфер событий path-watcher'ов: дефолтные 8 КБ переполняются на git checkout / npm install
    // в отдельном дереве (Error → RecreateWatcher, часть событий теряется).
    private const int PathBufferBytes = 64 * 1024;
    // Автоснятие path-watcher'а по бездействию: к графу worktree давно не обращались —
    // гасим handle, при следующем запросе он поднимется лениво снова.
    private const int PathIdleMinutes = 30;
    private const int IdleSweepMs = 5 * 60 * 1000;

    private class Entry
    {
        public FileSystemWatcher? Watcher;
        public Timer? Poll;                         // polling-режим (ФС без inotify: 9p/virtiofs bind-mount)
        public Dictionary<string, long>? Snapshot;  // rel -> LastWriteTicks (-1 = директория), для polling-диффа
        public string Root = "";
        // Проект watcher'а; null — watcher произвольного пути (worktree чата): без SignalR-группы
        // и синка знаний, только CodeGraph.
        public string? ProjectId;
        public DateTime LastTouchUtc = DateTime.UtcNow; // только для path-watcher'ов (автоснятие)
        public readonly HashSet<string> Connections = new();
        public readonly HashSet<string> PendingPaths = new(StringComparer.OrdinalIgnoreCase);
        public Timer? Debounce;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();             // key (projectId | worktree:{id}) -> Entry
    private readonly ConcurrentDictionary<string, HashSet<string>> _byConnection = new(); // connId -> projectIds
    // Таймер автоснятия простаивающих path-watcher'ов (заводится с первым таким watcher'ом).
    private Timer? _idleSweep;
    private readonly ProjectManager _projects;
    private readonly IHubContext<SessionHub> _hub;
    private readonly ProjectKnowledgeSyncService _knowledgeSync;
    private readonly CodeGraphService _codeGraphs;
    private readonly Lock _lock = new();
    // Polling вместо FileSystemWatcher — для bind-mount ФС без inotify (9p/virtiofs в Docker Desktop).
    private readonly bool _usePolling;
    private readonly int _pollIntervalMs;

    public FileWatcherService(ProjectManager projects, IHubContext<SessionHub> hub,
        ProjectKnowledgeSyncService knowledgeSync, CodeGraphService codeGraphs, IConfiguration config)
    {
        _projects = projects;
        _hub = hub;
        _knowledgeSync = knowledgeSync;
        _codeGraphs = codeGraphs;
        _usePolling = config.GetValue("FileWatcher:UsePolling", false);
        _pollIntervalMs = config.GetValue("FileWatcher:PollIntervalMs", 2000);
    }

    // Клиент начал смотреть проект — поднимаем watcher (или увеличиваем ref-count)
    public void Watch(string projectId, string connectionId)
    {
        var project = _projects.GetById(projectId);
        if (project is null || !Directory.Exists(project.RootPath)) return;

        lock (_lock)
        {
            var entry = _entries.GetOrAdd(projectId,
                _ => new Entry { Root = project.RootPath, ProjectId = projectId });
            entry.Connections.Add(connectionId);
            _byConnection.GetOrAdd(connectionId, _ => new HashSet<string>()).Add(projectId);
            if (entry.Watcher is null && entry.Poll is null)
            {
                if (_usePolling) StartPolling(projectId, entry);
                else entry.Watcher = CreateWatcher(projectId, entry);
            }
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

    // Watcher произвольного пути (отдельное дерево чата): поднимается лениво при первом
    // обращении к графу этого дерева и продлевается каждым следующим (LastTouch).
    // Ref-count'а тут нет намеренно — в отличие от проектных watcher'ов, которые считаются
    // по SignalR-коннектам: путь worktree уникален по ветке (ветка уникализируется при
    // создании в SessionManager.SetWorktreeAsync), поэтому связь ключ↔путь строго 1:1.
    public void WatchPath(string key, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(rootPath)) return;
        string full;
        try { full = Path.GetFullPath(rootPath); } catch { return; }
        if (!Directory.Exists(full)) return;

        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                // Тот же ключ на другом пути (чат пересоздал дерево) — перевешиваем watcher.
                if (string.Equals(existing.Root, full, StringComparison.OrdinalIgnoreCase))
                {
                    existing.LastTouchUtc = DateTime.UtcNow;
                    return;
                }
                DisposeEntry(key, existing);
            }

            var entry = new Entry { Root = full, LastTouchUtc = DateTime.UtcNow };
            _entries[key] = entry;
            if (_usePolling) StartPolling(key, entry);
            else entry.Watcher = CreateWatcher(key, entry, largeBuffer: true);
            _idleSweep ??= new Timer(_ => SweepIdlePaths(), null, IdleSweepMs, IdleSweepMs);
        }
    }

    // Снять watcher произвольного пути (удаление чата, выключение отдельного дерева).
    public void UnwatchPath(string key)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry)) DisposeEntry(key, entry);
        }
    }

    // Гасим path-watcher'ы, к графу которых давно не обращались: иначе handle'ы копятся
    // от забытых чатов. Следующий запрос к графу поднимет watcher заново.
    private void SweepIdlePaths()
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-PathIdleMinutes);
            foreach (var (key, entry) in _entries.ToArray())
                if (entry.ProjectId is null && entry.LastTouchUtc < cutoff)
                    DisposeEntry(key, entry);
        }
    }

    private FileSystemWatcher CreateWatcher(string key, Entry entry, bool largeBuffer = false)
    {
        var w = new FileSystemWatcher(entry.Root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        if (largeBuffer) w.InternalBufferSize = PathBufferBytes;
        void OnChange(object _, FileSystemEventArgs e) => OnFsEvent(key, entry, e.FullPath);
        w.Created += OnChange;
        w.Changed += OnChange;
        w.Deleted += OnChange;
        w.Renamed += (_, e) => { OnFsEvent(key, entry, e.FullPath); OnFsEvent(key, entry, e.OldFullPath); };
        w.Error += (_, _) => RecreateWatcher(key, entry);
        try { w.EnableRaisingEvents = true; } catch { /* недоступный путь — оставим без watcher */ }
        return w;
    }

    private void OnFsEvent(string key, Entry entry, string fullPath)
    {
        string rel;
        try { rel = Path.GetRelativePath(entry.Root, fullPath).Replace('\\', '/'); }
        catch { return; }
        if (rel.Length == 0 || rel == "." || IsExcluded(rel)) return;

        lock (_lock)
        {
            entry.PendingPaths.Add(rel);
            if (entry.Debounce is null)
                entry.Debounce = new Timer(_ => Flush(key, entry), null, DebounceMs, Timeout.Infinite);
            else
                entry.Debounce.Change(DebounceMs, Timeout.Infinite);
        }
    }

    // --- Polling-режим (ФС без inotify) -------------------------------------

    // Базовый снапшот без эмита — дальше шлём только дельту. Таймер one-shot,
    // перевзводится в конце скана, чтобы сканы не накладывались на медленной 9p.
    private void StartPolling(string key, Entry entry)
    {
        entry.Snapshot = BuildSnapshot(entry.Root);
        entry.Poll = new Timer(_ => PollScan(key, entry), null, _pollIntervalMs, Timeout.Infinite);
    }

    // Обход дерева с теми же исключениями, что и FileService.Tree.
    // Значение: тики последней записи файла, -1 — маркер директории.
    private static Dictionary<string, long> BuildSnapshot(string root)
    {
        var snap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] dirs, files;
            try { dirs = Directory.GetDirectories(dir); files = Directory.GetFiles(dir); }
            catch { continue; }

            foreach (var f in files)
            {
                if (snap.Count >= SnapshotMaxEntries) return snap;
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                if (IsExcluded(rel)) continue;
                long ticks;
                try { ticks = File.GetLastWriteTimeUtc(f).Ticks; } catch { ticks = 0; }
                snap[rel] = ticks;
            }
            foreach (var d in dirs)
            {
                if (FileService.TreeExcludes.Contains(Path.GetFileName(d))) continue;
                if (snap.Count >= SnapshotMaxEntries) return snap;
                snap[Path.GetRelativePath(root, d).Replace('\\', '/')] = -1;
                stack.Push(d);
            }
        }
        return snap;
    }

    private void PollScan(string key, Entry entry)
    {
        try
        {
            var old = entry.Snapshot;
            if (old is null) return;
            var cur = BuildSnapshot(entry.Root);

            var changed = new List<string>();
            foreach (var kv in cur)
                if (!old.TryGetValue(kv.Key, out var t) || t != kv.Value) changed.Add(kv.Key);
            foreach (var rel in old.Keys)
                if (!cur.ContainsKey(rel)) changed.Add(rel);

            entry.Snapshot = cur;
            if (changed.Count == 0) return;

            lock (_lock)
            {
                if (!_entries.ContainsKey(key)) return; // entry уже снят
                foreach (var p in changed) entry.PendingPaths.Add(p);
            }
            Flush(key, entry);
        }
        finally
        {
            // Перевзвод one-shot таймера; если entry уже disposed — Change бросит, игнорируем.
            try { entry.Poll?.Change(_pollIntervalMs, Timeout.Infinite); } catch { /* disposed */ }
        }
    }

    // Любой сегмент пути в списке исключений → игнорируем
    private static bool IsExcluded(string rel)
    {
        foreach (var seg in rel.Split('/'))
            if (FileService.TreeExcludes.Contains(seg)) return true;
        return false;
    }

    private void Flush(string key, Entry entry)
    {
        string[] paths;
        lock (_lock)
        {
            if (entry.PendingPaths.Count == 0) return;
            paths = entry.PendingPaths.Take(MaxPaths).ToArray();
            entry.PendingPaths.Clear();
        }
        // Отдельное дерево чата (path-watcher) в UI не показывается и в знания не синкается —
        // у него нет ни SignalR-группы, ни датасета: он живёт только ради графа кода.
        if (entry.ProjectId is string projectId)
        {
            _ = _hub.Clients.Group("project_" + projectId)
                .SendAsync("filesChanged", new { projectId, paths });
            // Правки Claude/внешние идут мимо файлового API — синк знаний узнаёт о них отсюда
            _knowledgeSync.QueueSync(entry.Root, paths);
        }
        // Реактивный триггер CodeGraph: .cs-правки планируют инкрементальное перестроение графа
        // (дебаунс 15с живёт в CodeGraphService — серия правок схлопывается в один rebuild).
        NotifyCodeGraph(entry.Root, paths);
    }

    // Фильтрует .cs из накопленных путей и передаёт в CodeGraphService для инкрементального
    // перестроения. abs-пути: провайдер нормализует их к rel через CompilationBuilder.Rel.
    private void NotifyCodeGraph(string rootPath, string[] paths)
    {
        List<string>? csFiles = null;
        foreach (var rel in paths)
        {
            if (!rel.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            try { (csFiles ??= new List<string>()).Add(Path.GetFullPath(Path.Combine(rootPath, rel))); }
            catch { /* пропускаем некорректный путь */ }
        }
        if (csFiles is { Count: > 0 })
            _codeGraphs.InvalidateIncremental(rootPath, csFiles);
    }

    private void RecreateWatcher(string key, Entry entry)
    {
        lock (_lock)
        {
            try { entry.Watcher?.Dispose(); } catch { }
            entry.Watcher = CreateWatcher(key, entry, largeBuffer: entry.ProjectId is null);
        }
    }

    private void DisposeEntry(string key, Entry entry)
    {
        try { entry.Watcher?.Dispose(); } catch { }
        entry.Poll?.Dispose();
        entry.Debounce?.Dispose();
        _entries.TryRemove(key, out _);
    }

    public void Dispose()
    {
        _idleSweep?.Dispose();
        _idleSweep = null;
        foreach (var e in _entries.Values)
        {
            try { e.Watcher?.Dispose(); } catch { }
            e.Poll?.Dispose();
            e.Debounce?.Dispose();
        }
        _entries.Clear();
    }
}
