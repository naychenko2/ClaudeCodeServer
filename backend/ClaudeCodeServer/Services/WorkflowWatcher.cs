using ClaudeCodeServer.Protocol;

namespace ClaudeCodeServer.Services;

// Следит за папкой wf_* и шлёт workflow_progress по мере завершения агентов.
// Завершается сам когда данные стабилизировались (30с без изменений) или по таймауту (20 мин).
public sealed class WorkflowWatcher : IDisposable
{
    private readonly string _wfPath;
    private readonly string _toolUseId;
    private readonly Func<ServerMessage, Task> _onMessage;

    private FileSystemWatcher? _fsWatcher;
    private Timer? _debounceTimer;
    private Timer? _timeoutTimer;
    private DateTime _lastChange = DateTime.UtcNow;
    private volatile bool _disposed;

    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StableDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(20);

    public WorkflowWatcher(string wfPath, string toolUseId, Func<ServerMessage, Task> onMessage)
    {
        _wfPath = wfPath;
        _toolUseId = toolUseId;
        _onMessage = onMessage;
    }

    public void Start()
    {
        if (!WorkflowAgentParser.IsPathAllowed(_wfPath)) return;
        if (!Directory.Exists(_wfPath)) return;

        _fsWatcher = new FileSystemWatcher(_wfPath, "agent-*.jsonl")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
            IncludeSubdirectories = false
        };
        _fsWatcher.Changed += OnFileChanged;
        _fsWatcher.Created += OnFileChanged;

        // Принудительно завершить через MaxDuration
        _timeoutTimer = new Timer(_ => _ = ForceFinishAsync(), null, MaxDuration, Timeout.InfiniteTimeSpan);

        // Первое чтение — через 500мс (транскрипт может заполняться прямо сейчас)
        ScheduleDebounce(TimeSpan.FromMilliseconds(500));
    }

    private void OnFileChanged(object _, FileSystemEventArgs e)
    {
        _lastChange = DateTime.UtcNow;
        ScheduleDebounce(DebounceDelay);
    }

    private void ScheduleDebounce(TimeSpan delay)
    {
        if (_disposed) return;
        if (_debounceTimer is null)
            _debounceTimer = new Timer(async _ => await SendUpdateAsync(), null, delay, Timeout.InfiniteTimeSpan);
        else
            _debounceTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private async Task SendUpdateAsync()
    {
        if (_disposed) return;
        try
        {
            var agents = WorkflowAgentParser.ParseDirectory(_wfPath);
            var stable = agents.Count > 0 && (DateTime.UtcNow - _lastChange) >= StableDelay;
            await _onMessage(new WorkflowProgressMessage(_toolUseId, agents, stable));
            if (stable) Dispose();
        }
        catch { /* не роняем */ }
    }

    private async Task ForceFinishAsync()
    {
        if (_disposed) return;
        try
        {
            var agents = WorkflowAgentParser.ParseDirectory(_wfPath);
            await _onMessage(new WorkflowProgressMessage(_toolUseId, agents, true));
        }
        catch { }
        finally { Dispose(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fsWatcher?.Dispose();
        _debounceTimer?.Dispose();
        _timeoutTimer?.Dispose();
    }
}
