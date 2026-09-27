using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Hands;

/// <summary>Одни руки на машину (<see cref="HandsMachineLock"/>): null — руки уже у другого хода.</summary>
internal interface IHandsMachineLock
{
    IDisposable? TryAcquire();
}

/// <summary>
/// Windows: именованный семафор <c>Local\AiHome.Hands.Active</c> — общий для всех агентов
/// сеанса входа. Семафор, а не мьютекс: мьютекс привязан к потоку, а ход освобождает руки
/// из другого потока, чем взял.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NamedHandsMachineLock : IHandsMachineLock
{
    public IDisposable? TryAcquire()
    {
        var semaphore = new Semaphore(1, 1, HandsMachineLock.Name);
        if (semaphore.WaitOne(0)) return new Lease(semaphore);
        semaphore.Dispose();
        return null;
    }

    private sealed class Lease(Semaphore semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 1) return;
            semaphore.Release();
            semaphore.Dispose();
        }
    }
}

/// <summary>Замок внутри процесса: тесты и платформы, где рук нет.</summary>
internal sealed class InProcessHandsMachineLock : IHandsMachineLock
{
    private int _taken;

    public IDisposable? TryAcquire() =>
        Interlocked.CompareExchange(ref _taken, 1, 0) == 0 ? new Lease(this) : null;

    private sealed class Lease(InProcessHandsMachineLock owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) Volatile.Write(ref owner._taken, 0);
        }
    }
}

/// <summary>Донесения о руках серверу (боевое — канал управления); не доставлено — не беда, бейдж перечитает.</summary>
internal interface IHandsStatusSink
{
    Task ReportAsync(DeviceHandsReport report, CancellationToken ct);
}

/// <summary>
/// Ходы, к которым сейчас подключены руки. Через реестр «Стоп» трея гасит ход, не зная о канале
/// исполнения, а трей и hello видят, что руки заняты.
/// </summary>
internal sealed class HandsRegistry
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _active = new(StringComparer.Ordinal);
    // Причины у недавно отцепившихся ходов: подписчик Changed узнаёт их уже после снятия регистрации
    private readonly Dictionary<string, string?> _ended = new(StringComparer.Ordinal);

    /// <summary>Руки подключились к ходу или отцепились от него.</summary>
    public event Action? Changed;

    public IReadOnlyList<HandsActiveTurn> Active
    {
        get
        {
            lock (_lock) return _active.Values.Select(e => e.Turn).OrderBy(t => t.StartedAt).ToList();
        }
    }

    /// <summary>
    /// Подключить руки к ходу. <paramref name="stop"/> гасит дерево хода с причиной
    /// <see cref="HandsEndReason"/>; снятие регистрации — Dispose результата.
    /// </summary>
    public IDisposable Attach(string turnId, string? projectRoot, Action<string> stop)
    {
        var entry = new Entry(new HandsActiveTurn(turnId, projectRoot, DateTimeOffset.UtcNow), stop);
        lock (_lock) _active[turnId] = entry;
        Changed?.Invoke();
        return new Registration(this, turnId, entry);
    }

    /// <summary>Причина, по которой руки хода погашены снаружи; null — ход кончился сам.</summary>
    public string? StopReasonOf(string turnId)
    {
        lock (_lock)
            return _active.TryGetValue(turnId, out var e) ? e.StopReason : _ended.GetValueOrDefault(turnId);
    }

    /// <summary>
    /// Погасить ход с руками (<paramref name="turnId"/> null — все). Возвращает число погашенных:
    /// ход прерывается целиком, следующий ход снова может взять руки.
    /// </summary>
    public int Stop(string? turnId, string reason)
    {
        List<Entry> targets;
        lock (_lock)
        {
            targets = _active.Values.Where(e => turnId is null || e.Turn.TurnId == turnId).ToList();
            foreach (var t in targets) t.StopReason ??= reason;
        }
        foreach (var t in targets) t.StopAction(reason);
        return targets.Count;
    }

    private void Detach(string turnId, Entry entry)
    {
        lock (_lock)
        {
            if (!_active.TryGetValue(turnId, out var current) || !ReferenceEquals(current, entry)) return;
            _active.Remove(turnId);
            if (_ended.Count >= 32) _ended.Clear();
            _ended[turnId] = entry.StopReason;
        }
        Changed?.Invoke();
    }

    private sealed class Entry(HandsActiveTurn turn, Action<string> stop)
    {
        public HandsActiveTurn Turn { get; } = turn;
        public Action<string> StopAction { get; } = stop;
        public string? StopReason { get; set; }
    }

    private sealed class Registration(HandsRegistry owner, string turnId, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Detach(turnId, entry);
        }
    }
}

/// <summary>Всё, что нужно ходу для рук. Нет у агента — руки на этом устройстве не поддерживаются.</summary>
internal sealed record HandsRuntime(
    HandsComponent Component,
    IHandsMachineLock MachineLock,
    HandsRegistry Registry,
    IHandsStatusSink? Status = null);

/// <summary>Руки, подключённые к ходу: замок машины и имя Job хода живут до конца хода.</summary>
internal sealed class HandsTurnLease(IDisposable machineLock, string jobName, IReadOnlyList<DeviceExecFile> files) : IDisposable
{
    public string JobName { get; } = jobName;

    /// <summary>Файлы spec с узлом моста вместо маркера.</summary>
    public IReadOnlyList<DeviceExecFile> Files { get; } = files;

    public void Dispose() => machineLock.Dispose();
}

/// <summary>
/// Подключение рук к ходу (ADR-016 §7, решение 4 плана): сервер ставит в MCP-конфиг хода только
/// маркер <see cref="DeviceExecPlaceholders.Hands"/>, а узел <c>hands</c> → свой
/// <c>HandsBridge.exe</c> подставляет агент. Подставляет, только если компонент установлен и
/// сверен, режим прав хода не <c>bypassPermissions</c> и руки машины свободны; иначе ход
/// честно отказывает (<see cref="ExecRefusedException"/>), а не идёт молча без рук.
/// </summary>
internal static class HandsAttach
{
    public const string UnsupportedText = "Руки на этом устройстве не поддерживаются: они есть только у агента на Windows.";

    public const string MisplacedMarkerText = "Маркер рук стоит не на своём месте в MCP-конфиге хода — руки не подключены.";

    /// <summary>Инструмент моста, который выключается у провайдера без зрения.</summary>
    public const string ScreenshotTool = "screenshot_control";

    /// <summary>Есть ли в файлах хода маркер рук.</summary>
    public static bool Requested(IReadOnlyList<DeviceExecFile> files) =>
        files.Any(f => f.Content?.Contains(DeviceExecPlaceholders.Hands, StringComparison.Ordinal) == true);

    /// <summary>
    /// Подключить руки: проверки по порядку, затем замок машины, затем подмена маркера.
    /// null — рук ход не просил.
    /// </summary>
    public static HandsTurnLease? Prepare(DeviceExecSpawn spawn, string turnId, HandsRuntime? runtime)
    {
        var files = spawn.Files ?? [];
        if (!Requested(files)) return null;

        if (runtime is null) throw new ExecRefusedException(UnsupportedText);

        var check = runtime.Component.Check();
        if (!check.Ready) throw new ExecRefusedException(check.Problem ?? HandsComponent.NotInstalledText);

        if (HandsTurnRules.PermissionRefusal(spawn.Args) is { } permission)
            throw new ExecRefusedException($"Руки не подключены: {permission}.");

        var jobName = HandsBridgeArgs.TurnJobPrefix + turnId + "." + Guid.NewGuid().ToString("N")[..8];
        var rewritten = Rewrite(files, runtime.Component.BridgePath, jobName);

        var lease = runtime.MachineLock.TryAcquire() ?? throw new ExecRefusedException(HandsMachineLock.BusyText);
        return new HandsTurnLease(lease, jobName, rewritten);
    }

    /// <summary>
    /// Маркер допустим ровно в одном месте: <c>mcpServers.hands.type</c> одного JSON-файла spec.
    /// Любое другое вхождение — отказ, а не «подставим, где нашли».
    /// </summary>
    internal static IReadOnlyList<DeviceExecFile> Rewrite(IReadOnlyList<DeviceExecFile> files, string bridgePath, string jobName)
    {
        var result = new List<DeviceExecFile>(files.Count);
        var replaced = 0;
        foreach (var file in files)
        {
            if (file.Content?.Contains(DeviceExecPlaceholders.Hands, StringComparison.Ordinal) != true)
            {
                result.Add(file);
                continue;
            }

            JsonObject? root;
            try { root = JsonNode.Parse(file.Content) as JsonObject; }
            catch (JsonException) { throw new ExecRefusedException(MisplacedMarkerText); }

            if (root?["mcpServers"] is not JsonObject servers
                || servers[DeviceExecPlaceholders.HandsServerName] is not JsonObject node
                || (node["type"] as JsonValue)?.TryGetValue<string>(out var type) != true
                || type != DeviceExecPlaceholders.Hands)
                throw new ExecRefusedException(MisplacedMarkerText);

            var vision = (node[DeviceExecPlaceholders.HandsVisionField] as JsonValue)?.TryGetValue<bool>(out var v) == true && v;
            var args = new JsonArray(HandsBridgeArgs.TurnJob, jobName);
            if (!vision)
            {
                args.Add(HandsBridgeArgs.ExcludeTools);
                args.Add(ScreenshotTool);
            }
            servers[DeviceExecPlaceholders.HandsServerName] = new JsonObject
            {
                ["type"] = "stdio",
                ["command"] = bridgePath,
                ["args"] = args,
                ["env"] = new JsonObject(),
            };

            var content = root.ToJsonString();
            // Второй маркер где-то ещё в том же файле — тоже не на своём месте
            if (content.Contains(DeviceExecPlaceholders.Hands, StringComparison.Ordinal))
                throw new ExecRefusedException(MisplacedMarkerText);

            result.Add(file with { Content = content });
            replaced++;
        }

        if (replaced != 1) throw new ExecRefusedException(MisplacedMarkerText);
        return result;
    }
}
