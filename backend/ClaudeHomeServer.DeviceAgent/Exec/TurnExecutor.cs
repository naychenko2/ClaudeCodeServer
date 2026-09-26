using System.Collections.Concurrent;
using System.Text;
using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.DeviceAgent.Processes;
using ClaudeHomeServer.DeviceAgent.Sidecar;
using ClaudeHomeServer.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Exec;

/// <summary>Настройки исполнения ходов на устройстве.</summary>
internal sealed record ExecOptions
{
    /// <summary>Корень временных каталогов ходов.</summary>
    public required string TurnsRoot { get; init; }

    /// <summary>Профиль CLI устройства (<c>CLAUDE_CONFIG_DIR</c>): транскрипты для --resume живут здесь.</summary>
    public required string ConfigDirectory { get; init; }

    /// <summary>Адрес сайдкара, <c>http://127.0.0.1:{порт}</c> (известен после его старта).</summary>
    public required Func<string> SidecarUrl { get; init; }

    /// <summary>
    /// Граница путей машины (ADR-016 §5): рабочий каталог хода — только под разрешёнными
    /// корнями, та же политика, что у localhost-API файлов и ретранслятора.
    /// </summary>
    public required AgentPathPolicy PathPolicy { get; init; }

    public bool IsWindows { get; init; } = OperatingSystem.IsWindows();

    /// <summary>Сколько ждать подтверждения хвоста вывода после конца хода.</summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Окружение агента — источник наследуемых по allow-list переменных.</summary>
    public Func<IReadOnlyDictionary<string, string>> InheritedEnvironment { get; init; } = CliEnvironment.CurrentProcess;

    public Func<TurnLaunch, TurnProcess> Launcher { get; init; } = TurnProcess.Start;
}

/// <summary>
/// Исполнение ходов на устройстве (ADR-016 §3): по кадру spawn запускает управляемую копию
/// CLI с окружением по allow-list, гоняет stdio кадрами связи, убивает дерево по kill и
/// прибирает за ходом. Живые ходы — в памяти и в журнале на диске.
/// </summary>
internal sealed class TurnExecutor
{
    /// <summary>Код выхода, когда ход не запустился на устройстве.</summary>
    public const int RefusedExitCode = 127;

    private readonly ExecOptions _options;
    private readonly ICliLeaseSource _cli;
    private readonly TurnGrants _grants;
    private readonly TurnJournal _journal;
    private readonly SessionFileJanitor _janitor;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, TurnProcess> _live = new(StringComparer.Ordinal);

    public TurnExecutor(ExecOptions options, ICliLeaseSource cli, TurnGrants grants, TurnJournal journal,
        ILogger<TurnExecutor>? log = null)
    {
        _options = options;
        _cli = cli;
        _grants = grants;
        _journal = journal;
        _janitor = new SessionFileJanitor(options.ConfigDirectory, log);
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    public int LiveCount => _live.Count;

    public SessionFileJanitor Janitor => _janitor;

    /// <summary>Остановка агента: живые ходы не переживают его.</summary>
    public void KillAll()
    {
        foreach (var (turnId, process) in _live)
        {
            _log.LogWarning("Агент останавливается — убиваю ход {TurnId}", turnId);
            process.KillTree();
        }
    }

    /// <summary>Обслуживает одно исполнение до конца: от кадра spawn до подтверждённого Exit.</summary>
    public async Task RunAsync(ExecLink link, CancellationToken ct)
    {
        await using var _ = link;
        DeviceExecControl? control;
        try
        {
            control = await ReadSpawnAsync(link, ct);
        }
        catch (ExecRefusedException e)
        {
            await RefuseAsync(link, null, e.Message);
            return;
        }
        if (control is null) return;

        TurnSetup setup;
        try
        {
            setup = Prepare(control);
        }
        catch (ExecRefusedException e)
        {
            _log.LogWarning("Ход {TurnId} не запущен: {Reason}", control.TurnId, e.Message);
            await RefuseAsync(link, control.TurnId, e.Message);
            return;
        }

        using (setup)
        {
            TurnProcess process;
            try
            {
                process = _options.Launcher(setup.Launch);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "Ход {TurnId}: CLI не запустился", control.TurnId);
                await RefuseAsync(link, control.TurnId, $"CLI не запустился: {e.Message}");
                return;
            }

            using (process)
                await DriveAsync(link, control, setup, process, ct);
        }
    }

    private async Task DriveAsync(ExecLink link, DeviceExecControl control, TurnSetup setup, TurnProcess process, CancellationToken ct)
    {
        var turnId = control.TurnId;
        _live[turnId] = process;
        _journal.Add(new TurnJournal.Entry(turnId, process.Id, process.StartTimeUtc, setup.Workspace.Directory));
        _log.LogInformation("Ход {TurnId} запущен: pid {Pid}, CLI {Version}", turnId, process.Id, setup.Cli.Version);

        var killed = 0;
        void Kill(string reason)
        {
            if (Interlocked.Exchange(ref killed, 1) == 1) return;
            _log.LogInformation("Ход {TurnId}: убиваю дерево процессов ({Reason})", turnId, reason);
            process.KillTree();
        }

        try
        {
            await link.SendAsync(DeviceExecFrameChannel.Info,
                DeviceExecJson.Serialize(new { pid = process.Id, cliVersion = setup.Cli.Version }), ct);

            var stdout = PumpAsync(process.StandardOutput, DeviceExecFrameChannel.Stdout, link);
            var stderr = PumpAsync(process.StandardError, DeviceExecFrameChannel.Stderr, link);
            if (!control.Spawn!.RedirectStdin) process.StandardInput.Close();
            var input = PumpInputAsync(link, process, turnId, Kill);

            using var stop = ct.Register(() => Kill("агент останавливается"));
            var exited = process.WaitForExitAsync();
            var first = await Task.WhenAny(exited, link.Finished);
            if (first != exited)
            {
                Kill(link.FailureReason ?? "сервер закрыл исполнение");
                await exited;
            }

            // Потомки, которых CLI оставил после себя, держат трубы вывода открытыми —
            // без этого чтение stdout не дождалось бы конца. Ход кончился — дерево тоже.
            process.KillTree();
            await Task.WhenAll(stdout, stderr);

            var exit = Volatile.Read(ref killed) == 1
                ? new DeviceExecExit(process.ExitCode, "SIGKILL")
                : new DeviceExecExit(process.ExitCode);
            if (!link.Finished.IsCompleted)
            {
                await link.SendAsync(DeviceExecFrameChannel.Exit, DeviceExecJson.Serialize(exit), CancellationToken.None);
                if (!await link.DrainAsync(_options.DrainTimeout))
                    _log.LogWarning("Ход {TurnId}: сервер не подтвердил конец вывода за {Timeout}", turnId, _options.DrainTimeout);
            }

            _ = input;
            _log.LogInformation("Ход {TurnId} завершён: код {Code}{Killed}", turnId, exit.Code,
                exit.Signal is null ? "" : ", убит");
        }
        catch (OperationCanceledException) when (link.Finished.IsCompleted || ct.IsCancellationRequested)
        {
            Kill("связь закрыта");
        }
        finally
        {
            process.KillTree();
            _live.TryRemove(turnId, out _);
            // Убитый CLI не успел убрать свои файлы сессии в профиле — за него это делает агент
            if (Volatile.Read(ref killed) == 1) _janitor.CleanUp(process.Id);
            _journal.Remove(turnId);
        }
    }

    private async Task<DeviceExecControl?> ReadSpawnAsync(ExecLink link, CancellationToken ct)
    {
        await foreach (var frame in link.ReadAllAsync(ct))
        {
            if (frame.Channel != DeviceExecFrameChannel.Control)
                throw new ExecRefusedException("первым кадром исполнения обязан быть spawn");

            DeviceExecControl? control;
            try { control = DeviceExecJson.Deserialize<DeviceExecControl>(frame.Payload.Span); }
            catch (System.Text.Json.JsonException) { throw new ExecRefusedException("кадр spawn не разобран"); }

            if (control is null || control.Op != DeviceExecControlOps.Spawn || control.Spawn is null)
                throw new ExecRefusedException("первым кадром исполнения обязан быть spawn");
            if (!TurnWorkspace.IsSafeSegment(control.TurnId))
                throw new ExecRefusedException("недопустимый идентификатор хода");
            return control;
        }
        return null;
    }

    private TurnSetup Prepare(DeviceExecControl control)
    {
        var spawn = control.Spawn!;
        if (!string.Equals(spawn.FileName, DeviceExecCli.Name, StringComparison.Ordinal))
            throw new ExecRefusedException($"агент запускает только claude, а не «{spawn.FileName}»");
        if (_live.ContainsKey(control.TurnId))
            throw new ExecRefusedException($"ход {control.TurnId} уже идёт на устройстве");

        var workingDirectory = spawn.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Path.IsPathFullyQualified(workingDirectory)
            || !Directory.Exists(workingDirectory))
            throw new ExecRefusedException($"рабочий каталог хода не найден на устройстве: {workingDirectory}");
        // Серверу агент не доверяет: каталог сверяется с корнями машины по реальному пути
        try
        {
            _options.PathPolicy.ProjectRoot(workingDirectory);
        }
        catch (AgentPathRefusedException e)
        {
            throw new ExecRefusedException(e.Message);
        }
        catch (IOException)
        {
            throw new ExecRefusedException($"рабочий каталог хода не найден на устройстве: {workingDirectory}");
        }

        // Аренда — первой: не готов харнес — не создаём ни каталогов, ни выдач
        var cli = _cli.TryAcquire(out var problem)
            ?? throw new ExecRefusedException(problem ?? $"{Cli.ManagedCli.NotReadyPrefix}: копии CLI нет");

        TurnWorkspace? workspace = null;
        string? grantKey = null;
        try
        {
            workspace = TurnWorkspace.Create(_options.TurnsRoot, control.TurnId, _log);
            grantKey = _grants.Register(control.Gateway);
            var sidecarUrl = _options.SidecarUrl();
            var sidecarTurnUrl = DeviceSidecarRoutes.TurnUrl(sidecarUrl, grantKey);

            workspace.Materialize(spawn.Files ?? [], sidecarTurnUrl);
            var args = workspace.ResolveArgs(spawn.Args);
            Directory.CreateDirectory(_options.ConfigDirectory);
            var env = CliEnvironment.Build(_options.IsWindows, _options.InheritedEnvironment(),
                _options.ConfigDirectory, DeviceEgressRoutes.ProxyUrl(sidecarUrl, grantKey), sidecarTurnUrl, spawn.Env);

            return new TurnSetup(cli, workspace, _grants, grantKey,
                new TurnLaunch(cli.ExecutablePath, args, workingDirectory, env));
        }
        catch
        {
            if (grantKey is not null) _grants.Remove(grantKey);
            workspace?.Dispose();
            cli.Dispose();
            throw;
        }
    }

    private static async Task RefuseAsync(ExecLink link, string? turnId, string reason)
    {
        try
        {
            await link.SendAsync(DeviceExecFrameChannel.Stderr, Encoding.UTF8.GetBytes(reason + "\n"));
            await link.SendAsync(DeviceExecFrameChannel.Exit, DeviceExecJson.Serialize(new DeviceExecExit(RefusedExitCode, Error: reason)));
            await link.DrainAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException) { }
    }

    private static async Task PumpAsync(Stream source, DeviceExecFrameChannel channel, ExecLink link)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int n;
            while ((n = await source.ReadAsync(buffer)) > 0)
                await link.SendAsync(channel, buffer.AsMemory(0, n));
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Связь закрыта насовсем или процесс убит — дочитывать некуда
        }
    }

    private async Task PumpInputAsync(ExecLink link, TurnProcess process, string turnId, Action<string> kill)
    {
        try
        {
            await foreach (var frame in link.ReadAllAsync())
            {
                switch (frame.Channel)
                {
                    case DeviceExecFrameChannel.Stdin:
                        await process.StandardInput.WriteAsync(frame.Payload);
                        await process.StandardInput.FlushAsync();
                        break;
                    case DeviceExecFrameChannel.StdinEof:
                        process.StandardInput.Close();
                        break;
                    case DeviceExecFrameChannel.Control:
                        var control = DeviceExecJson.Deserialize<DeviceExecControl>(frame.Payload.Span);
                        if (control?.Op == DeviceExecControlOps.Kill && control.TurnId == turnId) kill("kill от сервера");
                        else _log.LogWarning("Ход {TurnId}: непонятный кадр управления отброшен", turnId);
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException
                                      or System.Text.Json.JsonException)
        {
            // stdin закрыт (CLI вышел) — дальнейший ввод некуда писать
            _log.LogDebug(e, "Ход {TurnId}: ввод остановлен", turnId);
        }
    }

    private sealed class TurnSetup(ICliHandle cli, TurnWorkspace workspace, TurnGrants grants, string grantKey, TurnLaunch launch)
        : IDisposable
    {
        public ICliHandle Cli { get; } = cli;
        public TurnWorkspace Workspace { get; } = workspace;
        public TurnLaunch Launch { get; } = launch;

        // Аренда держится весь ход и закрывается по его концу; выдача шлюза — тоже
        public void Dispose()
        {
            grants.Remove(grantKey);
            Workspace.Dispose();
            Cli.Dispose();
        }
    }
}
