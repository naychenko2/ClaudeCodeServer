using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Files;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Composition;

/// <summary>Куда уходят донесения ватчера. Боевой — метод хаба устройств: сервер → веб-морда.</summary>
internal interface IFilesChangedSink
{
    Task ReportAsync(DeviceFilesChanged report, CancellationToken ct);
}

/// <summary>
/// Ватчер деревьев локальных проектов (ADR-016, задача 4.2): тот же
/// <see cref="RecursiveDirectoryWatcher"/> и тот же список исключений
/// (<see cref="TreeExcludes"/>), что у серверного FileWatcherService. Проект наблюдается,
/// пока с машины его открывают: наблюдение поднимает принятый билет, гасит простой.
///
/// Сбои различаются, как на сервере: потеря событий — только полная пересинхронизация у
/// веб-морды, смерть наблюдения — пересоздание плюс пересинхронизация.
/// </summary>
internal sealed class AgentFileWatchers : IDisposable
{
    public static readonly TimeSpan DefaultIdle = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan RecreateDelay = TimeSpan.FromSeconds(2);

    private readonly IFilesChangedSink _sink;
    private readonly ILogger _log;
    private readonly TimeSpan _idle;
    private readonly Func<string, IEnumerable<string>, Action<string, DirectoryWatchKind>, Action<DirectoryWatchFailure>, RecursiveDirectoryWatcher> _factory;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Timer _sweep;
    private bool _disposed;

    private sealed class Entry(string projectId, string root)
    {
        public string ProjectId { get; } = projectId;
        public string Root { get; } = root;
        public RecursiveDirectoryWatcher? Watcher;
        public Timer? Flush;
        public readonly HashSet<string> Pending = new(StringComparer.Ordinal);
        public bool Full;
        public DateTimeOffset LastSeen = DateTimeOffset.UtcNow;
    }

    public AgentFileWatchers(IFilesChangedSink sink, ILogger? log = null, TimeSpan? idle = null)
    {
        _sink = sink;
        _log = log ?? NullLogger.Instance;
        _idle = idle ?? DefaultIdle;
        _factory = (root, excludes, onEvent, onFailure) => new RecursiveDirectoryWatcher(
            root, excludes, onEvent, includeDirectories: true, bufferBytes: 64 * 1024, onFailure: onFailure);
        _sweep = new Timer(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public IReadOnlyCollection<string> Watched
    {
        get { lock (_lock) return _entries.Keys.ToArray(); }
    }

    /// <summary>Проект открыт с машины: поднять наблюдение или продлить его.</summary>
    public void Touch(string projectId, string realRoot)
    {
        Entry entry;
        Entry? replaced;
        lock (_lock)
        {
            if (_disposed) return;
            if (_entries.TryGetValue(projectId, out var existing) && AgentPathPolicy.PathComparer.Equals(existing.Root, realRoot))
            {
                existing.LastSeen = DateTimeOffset.UtcNow;
                return;
            }
            replaced = existing;
            entry = new Entry(projectId, realRoot);
            _entries[projectId] = entry;
        }
        // Гашение вне замка: Dispose наблюдателя ждёт поток чтения, а тот на событии берёт замок
        if (replaced is not null) Close(replaced);
        Start(entry);
    }

    private void Start(Entry entry)
    {
        var watcher = _factory(entry.Root, TreeExcludes.Names,
            (path, _) => OnEvent(entry, path),
            failure => OnFailure(entry, failure));
        try { watcher.Start(); }
        catch (Exception e)
        {
            _log.LogWarning(e, "Ватчер проекта {ProjectId} не поднялся", entry.ProjectId);
            watcher.Dispose();
            return;
        }
        lock (_lock)
        {
            if (!IsLive(entry)) { watcher.Dispose(); return; }
            entry.Watcher = watcher;
        }
    }

    private void OnEvent(Entry entry, string fullPath)
    {
        var rel = Path.GetRelativePath(entry.Root, fullPath).Replace('\\', '/');
        if (rel.StartsWith("..", StringComparison.Ordinal) || rel == ".") return;
        // Ветка FileSystemWatcher (Windows/macOS) наблюдает всё дерево — служебные каталоги
        // отсекаются по сегментам, как у FileWatcherService
        if (rel.Split('/').Any(TreeExcludes.Contains)) return;
        lock (_lock)
        {
            if (!IsLive(entry)) return;
            entry.Pending.Add(rel);
            ScheduleFlush(entry);
        }
    }

    private void OnFailure(Entry entry, DirectoryWatchFailure failure)
    {
        lock (_lock)
        {
            if (!IsLive(entry)) return;
            entry.Full = true;
            ScheduleFlush(entry);
        }
        if (failure == DirectoryWatchFailure.Stopped)
            _ = Task.Delay(RecreateDelay).ContinueWith(_ => Recreate(entry), TaskScheduler.Default);
    }

    private void Recreate(Entry entry)
    {
        RecursiveDirectoryWatcher? old;
        lock (_lock)
        {
            if (!IsLive(entry)) return;
            old = entry.Watcher;
            entry.Watcher = null;
        }
        // Гашение вне замка: Dispose ждёт поток чтения, а тот на событии берёт замок
        old?.Dispose();
        Start(entry);
    }

    // Под _lock
    private void ScheduleFlush(Entry entry) =>
        (entry.Flush ??= new Timer(_ => FlushNow(entry), null, Timeout.Infinite, Timeout.Infinite))
            .Change(Debounce, Timeout.InfiniteTimeSpan);

    private void FlushNow(Entry entry)
    {
        DeviceFilesChanged report;
        lock (_lock)
        {
            if (!IsLive(entry) || (entry.Pending.Count == 0 && !entry.Full)) return;
            var full = entry.Full || entry.Pending.Count > DeviceAgentApi.MaxChangedPaths;
            report = new DeviceFilesChanged(entry.ProjectId, full ? [] : entry.Pending.ToArray(), full);
            entry.Pending.Clear();
            entry.Full = false;
        }
        _ = SendAsync(report);
    }

    private async Task SendAsync(DeviceFilesChanged report)
    {
        try { await _sink.ReportAsync(report, CancellationToken.None); }
        catch (Exception e) { _log.LogDebug(e, "Донесение ватчера проекта {ProjectId} не ушло", report.ProjectId); }
    }

    private void Sweep()
    {
        List<Entry> idle;
        lock (_lock)
        {
            var edge = DateTimeOffset.UtcNow - _idle;
            idle = _entries.Values.Where(e => e.LastSeen < edge).ToList();
            foreach (var e in idle) _entries.Remove(e.ProjectId);
        }
        foreach (var e in idle) Close(e);
    }

    private bool IsLive(Entry entry) => !_disposed && _entries.TryGetValue(entry.ProjectId, out var e) && ReferenceEquals(e, entry);

    private static void Close(Entry entry)
    {
        entry.Flush?.Dispose();
        entry.Watcher?.Dispose();
    }

    public void Dispose()
    {
        List<Entry> all;
        lock (_lock)
        {
            _disposed = true;
            all = _entries.Values.ToList();
            _entries.Clear();
        }
        _sweep.Dispose();
        foreach (var e in all) Close(e);
    }
}
