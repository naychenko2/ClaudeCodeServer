using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Следит за папкой wf_* и шлёт workflow_progress по мере завершения агентов.
// УБЕРИ САМО-ЗАВЕРШЕНИЕ: между волнами агентов раннер делает паузы, и если
// ватчер умрёт — новые агенты не попали бы в workflow_progress (см. реальный
// кейс: Yuliya появилась через 45+ секунд после Sergey — ватчер уже диспознулся).
// Вместо этого живём, пока жива сессия: при Interrupt/смерти процесса ClaudeSession
// зовёт AbortAsync (финальный isDone=true), при закрытии сессии — Dispose.
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
    // _gate — таймеры и Dispose (быстрые секции без await): Change на задиспозенном
    // таймере кидал бы ObjectDisposedException на threadpool и ронял процесс
    private readonly object _gate = new();
    // _sendGate — сериализация отправок: финальный isDone=true уходит строго ПОСЛЕ
    // in-flight тика, и после него не уходит ничего (иначе опоздавший тик
    // перезаписал бы done и карточка зависла бы в running)
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private volatile bool _disposed;
    // Финальный isDone=true уже отправлен (AbortAsync/форс-финиш) — больше не шлём
    private volatile bool _finished;

    // Последний удачный снапшот: кэш per agent-id (агент с нечитаемым в этом тике
    // файлом не выпадает из выдачи) и payload на случай ошибки финального парса
    private IReadOnlyList<WorkflowAgentDto> _lastAgents = [];
    // Сериализованный последний отправленный payload — подавление неизменных апдейтов
    private string? _lastPayload;
    // Сигнатура файлов папки на прошлый тик — детект активности без FSW
    private string? _lastSignature;

    // Ватчер задиспозился — можно убирать из списков
    public bool IsDisposed => _disposed;

    // Прогон CLI, в котором запущен этот workflow (CliRun — приватный тип ClaudeSession,
    // поэтому object). Финализация прогона абортит только СВОИ ватчеры: при опоздавшей
    // финализации (>15с) замещённого прогона в списке уже могут быть ватчеры нового
    public object? Owner { get; init; }

    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    // Аварийный потолок. Живой workflow не обрубаем: при активности за последние
    // ActivityGrace дедлайн продлевается ещё на MaxDuration (см. ForceFinishAsync)
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan ActivityGrace = TimeSpan.FromMinutes(10);

    public WorkflowWatcher(string wfPath, string toolUseId, Func<ServerMessage, Task> onMessage)
    {
        _wfPath = wfPath;
        _toolUseId = toolUseId;
        _onMessage = onMessage;
    }

    public void Start()
    {
        if (!WorkflowAgentParser.IsPathAllowed(_wfPath)) return;

        lock (_gate)
        {
            if (_disposed) return;
            // Таймаут ставим сразу — независимо от того, существует ли директория
            _timeoutTimer = new Timer(_ => _ = ForceFinishAsync(), null, MaxDuration, Timeout.InfiniteTimeSpan);
        }

        AttachFsWatcher();

        // Первое чтение — через 500мс; если директория ещё не появилась — будем поллить
        ScheduleDebounce(TimeSpan.FromMilliseconds(500));
    }

    private void AttachFsWatcher()
    {
        lock (_gate)
        {
            if (_disposed || _fsWatcher != null || !Directory.Exists(_wfPath)) return;
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
        lock (_gate)
        {
            if (_disposed || _finished) return;
            var due = DateTime.UtcNow + delay;
            if (_nextFire <= due) return; // уже взведён раньше — не переносим
            _nextFire = due;
            try
            {
                if (_debounceTimer is null)
                    _debounceTimer = new Timer(async _ => await SendUpdateAsync(), null, delay, Timeout.InfiniteTimeSpan);
                else
                    _debounceTimer.Change(delay, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException) { /* гонка с Dispose — тик уже не нужен */ }
        }
    }

    private async Task SendUpdateAsync()
    {
        lock (_gate)
        {
            if (_disposed || _finished) return;
            _nextFire = DateTime.MaxValue; // тик отработал — можно взводить новый
        }
        try
        {
            AttachFsWatcher();

            if (!Directory.Exists(_wfPath))
            {
                ScheduleDebounce(TimeSpan.FromSeconds(2));
                return;
            }

            // Активность и без FSW: изменился состав/размер/mtime файлов — фиксируем
            // (по _lastChange продлевается аварийный дедлайн и считается stable)
            var signature = ComputeDirSignature();
            if (signature != _lastSignature)
            {
                _lastSignature = signature;
                _lastChange = DateTime.UtcNow;
            }

            var agents = MergeWithLastKnown(WorkflowAgentParser.ParseDirectory(_wfPath));
            var stable = agents.Count > 0 && agents.All(a => a.IsDone)
                && (DateTime.UtcNow - _lastChange) >= TimeSpan.FromSeconds(45);

            // isDone на фронте считается по result хода + done агентов, stable только для кэша.
            // НЕ завершаемся — ждём новые волны. Диспозимся из AbortAsync/ForceFinishAsync
            // или из ClaudeSession.DisposeAsync.
            var payload = SerializePayload(agents, stable);
            await _sendGate.WaitAsync();
            try
            {
                if (_disposed || _finished) return;
                _lastAgents = agents;
                // Ничего не изменилось с прошлого апдейта — не спамим полный payload
                if (payload != _lastPayload)
                {
                    _lastPayload = payload;
                    Console.WriteLine($"[WorkflowWatcher] SendUpdate: agents={agents.Count} done={agents.Count(a => a.IsDone)} types={string.Join(",", agents.Select(a => a.AgentType ?? "null"))} stable={stable}");
                    await _onMessage(new WorkflowProgressMessage(_toolUseId, agents, stable));
                }
            }
            finally { _sendGate.Release(); }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WorkflowWatcher] ошибка SendUpdate: {ex.Message}");
        }

        ScheduleDebounce(PollInterval);
    }

    // Агент, чей файл существует, но в этом тике не распарсился (jsonl дописывается
    // на лету), не должен выпадать из выдачи — подставляем последний удачный снапшот
    private IReadOnlyList<WorkflowAgentDto> MergeWithLastKnown(IReadOnlyList<WorkflowAgentDto> parsed)
    {
        var last = _lastAgents;
        if (last.Count == 0) return parsed;

        var byId = new Dictionary<string, WorkflowAgentDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in parsed) byId[a.Id] = a;

        var lostIds = last.Where(p => !byId.ContainsKey(p.Id) && AgentFileExists(p.Id))
            .Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (lostIds.Count == 0) return parsed;

        // Прежняя хронология для известных агентов, новые — в конец
        var result = new List<WorkflowAgentDto>(parsed.Count + lostIds.Count);
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in last)
        {
            if (byId.TryGetValue(p.Id, out var cur)) { if (added.Add(p.Id)) result.Add(cur); }
            else if (lostIds.Contains(p.Id) && added.Add(p.Id)) result.Add(p);
        }
        foreach (var a in parsed)
            if (added.Add(a.Id)) result.Add(a);
        return result;
    }

    private bool AgentFileExists(string agentId) =>
        File.Exists(Path.Combine(_wfPath, $"agent-{agentId}.jsonl"));

    // Сериализованный снапшот для сравнения «изменилось ли» между тиками
    private static string SerializePayload(IReadOnlyList<WorkflowAgentDto> agents, bool stable) =>
        (stable ? "1|" : "0|") + JsonSerializer.Serialize(agents);

    // Сигнатура содержимого папки (имя+размер+mtime *.jsonl) — детект активности,
    // даже когда FSW не навесился или его события потерялись
    private string ComputeDirSignature()
    {
        var sb = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(_wfPath, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
        {
            var fi = new FileInfo(file);
            sb.Append(fi.Name).Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append(';');
        }
        return sb.ToString();
    }

    // Аварийный потолок: живой workflow не обрубаем — при недавней активности
    // продлеваем дедлайн ещё на MaxDuration, форс-финишим только по-настоящему зависшие
    private async Task ForceFinishAsync()
    {
        if (_disposed || _finished) return;
        if (DateTime.UtcNow - _lastChange < ActivityGrace)
        {
            lock (_gate)
            {
                if (_disposed) return;
                try { _timeoutTimer?.Change(MaxDuration, Timeout.InfiniteTimeSpan); }
                catch (ObjectDisposedException) { /* гонка с Dispose */ }
            }
            return;
        }
        await FinishAsync();
    }

    // Процесс сессии убит вместе с workflow-раннерами — агенты уже не завершатся:
    // шлём финальный isDone=true и диспозимся. Повторные вызовы — no-op.
    public Task AbortAsync() => FinishAsync();

    // Финальный workflow_progress (isDone=true) + Dispose — единственная точка завершения
    private async Task FinishAsync()
    {
        if (_disposed || _finished) return;

        // Свежий парс с фолбэком на последний удачный снапшот — финал не должен пропасть
        IReadOnlyList<WorkflowAgentDto> agents;
        try { agents = MergeWithLastKnown(WorkflowAgentParser.ParseDirectory(_wfPath)); }
        catch { agents = _lastAgents; }
        if (agents.Count == 0) agents = _lastAgents;

        await _sendGate.WaitAsync();
        try
        {
            if (_disposed || _finished) return;
            _finished = true;
            await _onMessage(new WorkflowProgressMessage(_toolUseId, agents, true));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WorkflowWatcher] ошибка финального апдейта: {ex.Message}");
        }
        finally
        {
            _sendGate.Release();
            Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _fsWatcher?.Dispose();
            _debounceTimer?.Dispose();
            _timeoutTimer?.Dispose();
        }
    }
}
