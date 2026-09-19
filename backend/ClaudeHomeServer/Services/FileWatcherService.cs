using System.Collections.Concurrent;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.Knowledge;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

// Следит за файлами проекта, пока к нему подключён хотя бы один клиент (ref-count по connectionId),
// и шлёт в группу "project_{id}" событие "filesChanged" { projectId, paths, full } с дебаунсом.
// full=true — сигнал полной пересинхронизации (переполнение лимита путей, пересоздание
// watcher'а после сбоя, либо reconnect после обрыва — Watch вернул true):
// paths при нём пуст, клиент перезагружает всё раскрытое.
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
    // Буфер событий watcher'ов: дефолтные 8 КБ переполняются на git checkout / npm install
    // (Error → RecreateWatcher, часть событий теряется). Ставится ВСЕМ watcher'ам, не только
    // отдельным деревьям: массовые правки бывают и в RootPath проекта, а пересоздание
    // проектного watcher'а с дефолтным буфером давало цикл «переполнился → такой же → снова».
    private const int PathBufferBytes = 64 * 1024;
    // Автоснятие path-watcher'а по бездействию: к графу worktree давно не обращались —
    // гасим handle, при следующем запросе он поднимется лениво снова.
    private const int PathIdleMinutes = 30;
    private const int IdleSweepMs = 5 * 60 * 1000;
    // Ошибка, пришедшая в пределах этого окна после прошлого пересоздания, — продолжение той же
    // серии сбоев: пауза перед следующим пересозданием удваивается (до потолка RecreateMaxDelay).
    private static readonly TimeSpan RecreateStreakWindow = TimeSpan.FromMinutes(10);

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
        // Отложенное пересоздание watcher'а после Error (одно на entry, серия ошибок схлопывается).
        public Timer? Recreate;
        public int RecreateStreak;
        public DateTime LastRecreateUtc = DateTime.MinValue;
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
    // Пауза перед пересозданием после Error: база и потолок экспоненты.
    private readonly int _recreateDelayMs;
    private readonly int _recreateMaxDelayMs;
    private int _recreateCount;
    // Для тестов: сколько раз пересоздавался watcher (серия ошибок не должна давать шторм).
    internal int RecreateCount { get { lock (_lock) return _recreateCount; } }

    public FileWatcherService(ProjectManager projects, IHubContext<SessionHub> hub,
        ProjectKnowledgeSyncService knowledgeSync, CodeGraphService codeGraphs, IConfiguration config)
    {
        _projects = projects;
        _hub = hub;
        _knowledgeSync = knowledgeSync;
        _codeGraphs = codeGraphs;
        _usePolling = config.GetValue("FileWatcher:UsePolling", false);
        _pollIntervalMs = config.GetValue("FileWatcher:PollIntervalMs", 2000);
        _recreateDelayMs = Math.Max(1, config.GetValue("FileWatcher:RecreateDelayMs", 2000));
        _recreateMaxDelayMs = Math.Max(_recreateDelayMs, config.GetValue("FileWatcher:RecreateMaxDelayMs", 5 * 60 * 1000));
    }

    // Клиент начал смотреть проект — поднимаем watcher (или увеличиваем ref-count).
    // Возвращает true, если watcher поднят заново (entry был без Watcher/Poll — в наблюдении
    // был пробел, например disconnect → rejoin): клиенту нужен full-ресинк. False —
    // к живому watcher'у прибавился ещё один connectionId (второй таб) или ранний выход.
    public bool Watch(string projectId, string connectionId)
    {
        var project = _projects.GetById(projectId);
        if (project is null || !Directory.Exists(project.RootPath)) return false;

        lock (_lock)
        {
            var entry = _entries.GetOrAdd(projectId,
                _ => new Entry { Root = project.RootPath, ProjectId = projectId });
            entry.Connections.Add(connectionId);
            _byConnection.GetOrAdd(connectionId, _ => new HashSet<string>()).Add(projectId);
            if (entry.Watcher is null && entry.Poll is null)
            {
                if (_usePolling) StartPolling(projectId, entry);
                else StartWatcher(projectId, entry);
                return true;
            }
            return false;
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
                // Тот же путь без живого watcher'а (старт упал, пересоздание не назначено) — поднимаем заново.
                if (string.Equals(existing.Root, full, StringComparison.OrdinalIgnoreCase)
                    && (existing.Watcher is not null || existing.Poll is not null || existing.Recreate is not null))
                {
                    existing.LastTouchUtc = DateTime.UtcNow;
                    return;
                }
                DisposeEntry(key, existing);
            }

            var entry = new Entry { Root = full, LastTouchUtc = DateTime.UtcNow };
            _entries[key] = entry;
            if (_usePolling) StartPolling(key, entry);
            else StartWatcher(key, entry);
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

    // Вызывается под _lock. entry.Watcher присваивается ДО включения: на Linux ошибки старта
    // (не удалось поставить слежку) приходят в Error синхронно, изнутри EnableRaisingEvents, —
    // обработчик должен узнать в отправителе текущий watcher.
    private void StartWatcher(string key, Entry entry)
    {
        var w = new FileSystemWatcher(entry.Root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = PathBufferBytes,
        };
        void OnChange(object _, FileSystemEventArgs e) => OnFsEvent(key, entry, e.FullPath);
        w.Created += OnChange;
        w.Changed += OnChange;
        w.Deleted += OnChange;
        w.Renamed += (_, e) => { OnFsEvent(key, entry, e.FullPath); OnFsEvent(key, entry, e.OldFullPath); };
        w.Error += (sender, _) => OnWatcherError(key, entry, sender);
        entry.Watcher = w;
        try { w.EnableRaisingEvents = true; }
        catch
        {
            // Недоступный путь либо исчерпан лимит inotify-экземпляров (EMFILE) — без watcher'а,
            // с повтором по той же лестнице пауз, что и после Error.
            if (ReferenceEquals(entry.Watcher, w)) entry.Watcher = null;
            try { w.Dispose(); } catch { }
            ScheduleRecreate(key, entry);
        }
    }

    // Error watcher'а (переполнение очереди, не удалось поставить слежку на новую папку) —
    // только ЗАКАЗ пересоздания, не само пересоздание. Инцидент 2026-09-19: пересоздание прямо
    // из колбэка при исчерпанном бюджете слежек (ENOSPC) давало рекурсию — новый watcher падал
    // синхронно внутри своего же старта и заказывал следующий, и так до исчерпания лимита
    // inotify-экземпляров (8076 штук). Каждый watcher, у которого не встала корневая слежка,
    // держит inotify-fd навсегда: поток .NET висит в read(), будить его нечем, Dispose не
    // помогает. Поэтому — пауза с удвоением на серию сбоев и не больше одного заказа на entry.
    // Сам колбэк в _lock не лезет: .NET зовёт Error из-под своего внутреннего замка слежек,
    // а Dispose под нашим _lock ждёт тот же замок — была бы взаимоблокировка.
    private void OnWatcherError(string key, Entry entry, object? sender) =>
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_lock)
            {
                // Ошибка снятого/заменённого watcher'а (досылка при Dispose) — не повод пересоздавать живой
                if (!ReferenceEquals(sender, entry.Watcher)) return;
                ScheduleRecreate(key, entry);
            }
        });

    // Вызывается под _lock.
    private void ScheduleRecreate(string key, Entry entry)
    {
        if (entry.Recreate is not null || !IsLive(key, entry)) return;
        var now = DateTime.UtcNow;
        entry.RecreateStreak = now - entry.LastRecreateUtc < RecreateStreakWindow ? entry.RecreateStreak + 1 : 0;
        var delay = Math.Min((long)_recreateDelayMs << Math.Min(entry.RecreateStreak, 20), _recreateMaxDelayMs);
        entry.Recreate = new Timer(_ => RecreateWatcher(key, entry), null, delay, Timeout.Infinite);
    }

    private bool IsLive(string key, Entry entry) =>
        _entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry);

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
                if (TreeExcludes.Contains(Path.GetFileName(d))) continue;
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
            if (TreeExcludes.Contains(seg)) return true;
        return false;
    }

    private void Flush(string key, Entry entry)
    {
        string[] allPaths;
        bool full;
        lock (_lock)
        {
            if (entry.PendingPaths.Count == 0) return;
            full = entry.PendingPaths.Count > MaxPaths;
            allPaths = entry.PendingPaths.ToArray();
            entry.PendingPaths.Clear();
        }
        // Отдельное дерево чата (path-watcher) в UI не показывается и в знания не синкается —
        // у него нет ни SignalR-группы, ни датасета: он живёт только ради графа кода.
        if (entry.ProjectId is string projectId)
        {
            // Переполнение MaxPaths: раньше всё сверх лимита молча выбрасывалось и часть
            // изменений не доезжала до клиента никогда. Теперь — признак полной
            // пересинхронизации; сами пути не слаим, клиент при full перезагружает всё раскрытое.
            var clientPaths = full ? Array.Empty<string>() : allPaths;
            _ = _hub.Clients.Group(Composition.SessionHubBroadcaster.ProjectGroup(projectId))
                .SendAsync("filesChanged", new { projectId, paths = clientPaths, full });
            // Правки Claude/внешние идут мимо файлового API — синк знаний узнаёт о них отсюда.
            // Передаём полный накопленный список (а не клиентскую вырезку): SyncAsync и так
            // делает полный проход по отслеживаемым файлам, а hints нужны лишь для детекта
            // переноса — резать их значило бы деградировать перенос до delete+create.
            _knowledgeSync.QueueSync(entry.Root, allPaths);
        }
        // Реактивный триггер CodeGraph: .cs-правки планируют инкрементальное перестроение графа
        // (дебаунс 15с живёт в CodeGraphService — серия правок схлопывается в один rebuild).
        NotifyCodeGraph(entry.Root, allPaths);
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
            entry.Recreate?.Dispose();
            entry.Recreate = null;
            if (!IsLive(key, entry)) return; // entry снят, пока ждали паузу
            entry.LastRecreateUtc = DateTime.UtcNow;
            _recreateCount++;
            try { entry.Watcher?.Dispose(); } catch { }
            entry.Watcher = null;
            StartWatcher(key, entry);
        }
        // За время сбоя watcher'а события ФС потеряны — списку файлов в UI нечем
        // компенсироваться. Клиенту уходит сигнал полной пересинхронизации (пути неизвестны),
        // синку знаний — полный проход без hints: перенос вне файлового API задетектится
        // как delete+create вместо миграции, это приемлемая цена редкого сбоя.
        if (entry.ProjectId is string projectId)
        {
            _ = _hub.Clients.Group(Composition.SessionHubBroadcaster.ProjectGroup(projectId))
                .SendAsync("filesChanged", new { projectId, paths = Array.Empty<string>(), full = true });
            _knowledgeSync.QueueSync(entry.Root);
        }
    }

    // internal для тестов (InternalsVisibleTo): пересоздание watcher'а по ключу —
    // воспроизводит путь Error → RecreateWatcher без реального сбоя ФС.
    internal void RecreateWatcher(string key)
    {
        if (_entries.TryGetValue(key, out var entry)) RecreateWatcher(key, entry);
    }

    private void DisposeEntry(string key, Entry entry)
    {
        try { entry.Watcher?.Dispose(); } catch { }
        entry.Watcher = null;
        entry.Poll?.Dispose();
        entry.Debounce?.Dispose();
        entry.Recreate?.Dispose();
        entry.Recreate = null;
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
            e.Recreate?.Dispose();
        }
        _entries.Clear();
    }
}
