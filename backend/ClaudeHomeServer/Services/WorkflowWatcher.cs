using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Следит за папкой wf_* и шлёт workflow_progress по мере завершения агентов.
// Завершается сам когда данные стабилизировались (все агенты done + StableDelay тишины)
// или по таймауту MaxDuration.
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

    // Ватчер задиспозился сам (workflow стабилизировался или таймаут) — можно убирать из списков
    public bool IsDisposed => _disposed;

    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2);
    // Тишина после завершения ВСЕХ агентов, после которой считаем workflow стабильным.
    // ВАЖНО: между волнами агентов раннер делает паузы (барьер parallel(), спавн следующего
    // агента, setTimeout в скрипте) — в этот момент все СУЩЕСТВУЮЩИЕ агенты done, и короткая
    // тишина ложно выглядела как завершение workflow: ватчер слал isDone и умирал, дальше
    // блок в чате не обновлялся до рефреша. 45с закрывают реальные межволновые зазоры;
    // финальный ✓ в UI от этого не задерживается (isSettled на фронте считается по result
    // хода + done всех агентов), stable нужен только для самозавершения ватчера и чистки кэша.
    private static readonly TimeSpan StableDelay = TimeSpan.FromSeconds(45);
    // Периодический перечёт директории, пока агенты работают (они могут долго молчать
    // в файлах, выполняя длинную команду — FileSystemWatcher тогда не сработает).
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
        // journal.jsonl тоже считается активностью: раннер пишет в него started/result
        // между волнами агентов — иначе тишина в agent-файлах ложно выглядит как конец
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
            // Директория может появиться после Start() — подключаем FSWatcher как только она создана
            AttachFsWatcher();

            if (!Directory.Exists(_wfPath))
            {
                // Директория ещё не создана — поллим каждые 2с
                ScheduleDebounce(TimeSpan.FromSeconds(2));
                return;
            }

            var agents = WorkflowAgentParser.ParseDirectory(_wfPath);
            var allDone = agents.Count > 0 && agents.All(a => a.IsDone);
            // Завершаем (stable=true) ТОЛЬКО когда все агенты done и файлы дописаны (тишина ≥5с).
            // Пока хоть один агент работает — НЕ завершаемся, даже если файлы молчат: агент может
            // выполнять длинную команду без вывода в jsonl (иначе рвали статус на промежуточном).
            var stable = allDone && (DateTime.UtcNow - _lastChange) >= StableDelay;
            await _onMessage(new WorkflowProgressMessage(_toolUseId, agents, stable));
            if (stable) { Dispose(); return; }
            // Продолжаем периодически перечитывать директорию — даже если FileSystemWatcher молчит
            // (агент в долгой команде), чтобы поймать финальные result и не зависнуть на спиннере.
            ScheduleDebounce(PollInterval);
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
