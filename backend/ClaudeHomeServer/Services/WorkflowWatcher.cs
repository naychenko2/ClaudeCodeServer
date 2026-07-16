using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Следит за папкой wf_* и шлёт workflow_progress по мере завершения агентов.
// УБЕРИ САМО-ЗАВЕРШЕНИЕ: между волнами агентов раннер делает паузы, и если
// ватчер умрёт — новые агенты не попали бы в workflow_progress (см. реальный
// кейс: Yuliya появилась через 45+ секунд после Sergey — ватчер уже диспознулся).
// Вместо этого живём, пока жива сессия — чистимся в ClaudeSession.DisposeAsync.
// PollInterval 5с — копеечная операция.
public sealed class WorkflowWatcher : IDisposable
{
    private readonly string _wfPath;
    private readonly string _toolUseId;
    private readonly Func<ServerMessage, Task> _onMessage;

    private FileSystemWatcher? _fsWatcher;
    private Timer? _debounceTimer;
    private Timer? _timeoutTimer;
    private DateTime _lastChange = DateTime.UtcNow;
    // Момент ближайшего запланированного тика (MaxValue — не взведён); под _gate
    private DateTime _nextFire = DateTime.MaxValue;
    private readonly object _gate = new();
    private volatile bool _disposed;

    // Ватчер задиспозился — можно убирать из списков
    public bool IsDisposed => _disposed;

    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    // Аварийный потолок: длинные workflow (после снятия 600с-лимита) могут идти долго.
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(60);

    public WorkflowWatcher(string wfPath, string toolUseId, Func<ServerMessage, Task> onMessage)
    {
        _wfPath = wfPath;
        _toolUseId = toolUseId;
        _onMessage = onMessage;
    }

    public void Start()
    {
        if (!WorkflowAgentParser.IsPathAllowed(_wfPath)) return;

        // Таймаут ставим сразу — независимо от того, существует ли директория
        _timeoutTimer = new Timer(_ => _ = ForceFinishAsync(), null, MaxDuration, Timeout.InfiniteTimeSpan);

        if (Directory.Exists(_wfPath))
            AttachFsWatcher();

        // Первое чтение — через 500мс; если директория ещё не появилась — будем поллить
        ScheduleDebounce(TimeSpan.FromMilliseconds(500));
    }

    private void AttachFsWatcher()
    {
        if (_fsWatcher != null || !Directory.Exists(_wfPath)) return;
        _fsWatcher = new FileSystemWatcher(_wfPath)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
            IncludeSubdirectories = false
        };
        _fsWatcher.Filters.Add("agent-*.jsonl");
        _fsWatcher.Filters.Add("journal.jsonl");
        _fsWatcher.Changed += OnFileChanged;
        _fsWatcher.Created += OnFileChanged;
    }

    private void OnFileChanged(object _, FileSystemEventArgs e)
    {
        _lastChange = DateTime.UtcNow;
        ScheduleDebounce(DebounceDelay);
    }

    // Троттлинг, а не дебаунс: тик можно только ПРИБЛИЗИТЬ, но не отложить. Иначе
    // непрерывно пишущий транскрипт агента переносил бы таймер бесконечно, и апдейты
    // уходили бы только в паузах между «этапами» — лента переставала быть потоком.
    private void ScheduleDebounce(TimeSpan delay)
    {
        if (_disposed) return;
        lock (_gate)
        {
            var due = DateTime.UtcNow + delay;
            if (_nextFire <= due) return; // уже взведён раньше — не переносим
            _nextFire = due;
            if (_debounceTimer is null)
                _debounceTimer = new Timer(async _ => await SendUpdateAsync(), null, delay, Timeout.InfiniteTimeSpan);
            else
                _debounceTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task SendUpdateAsync()
    {
        if (_disposed) return;
        lock (_gate) _nextFire = DateTime.MaxValue; // тик отработал — можно взводить новый
        try
        {
            AttachFsWatcher();

            if (!Directory.Exists(_wfPath))
            {
                ScheduleDebounce(TimeSpan.FromSeconds(2));
                return;
            }

            var agents = WorkflowAgentParser.ParseDirectory(_wfPath);
            var stable = agents.Count > 0 && agents.All(a => a.IsDone)
                && (DateTime.UtcNow - _lastChange) >= TimeSpan.FromSeconds(45);

            Console.WriteLine($"[WorkflowWatcher] SendUpdate: agents={agents.Count} done={agents.Count(a => a.IsDone)} types={string.Join(",", agents.Select(a => a.AgentType ?? "null"))} stable={stable}");

            // isDone на фронте считается по result хода + done агентов, stable только для кэша.
            // НЕ завершаемся — ждём новые волны. Диспозимся при ForceFinishAsync (таймаут)
            // или из ClaudeSession.DisposeAsync.
            await _onMessage(new WorkflowProgressMessage(_toolUseId, agents, stable));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WorkflowWatcher] ошибка SendUpdate: {ex.Message}");
        }

        if (!_disposed)
            ScheduleDebounce(PollInterval);
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
