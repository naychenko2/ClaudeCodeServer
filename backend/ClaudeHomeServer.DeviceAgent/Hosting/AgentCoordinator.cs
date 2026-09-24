using System.Net;
using System.Net.WebSockets;
using ClaudeHomeServer.DeviceAgent.Cli;
using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.DeviceAgent.Sidecar;
using ClaudeHomeServer.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Hosting;

/// <summary>Канал управления: hello и команды сервера. Боевой — хаб /hubs/devices.</summary>
internal interface IControlConnection
{
    Task<DeviceHelloAck> HelloAsync(DeviceHello hello, CancellationToken ct);

    /// <summary>Сервер просит открыть канал исполнения.</summary>
    event Func<DeviceExecOpenCommand, Task>? ExecOpen;

    /// <summary>Соединение поднялось заново — сервер забыл hello.</summary>
    event Func<Task>? Reconnected;
}

/// <summary>Управляемая копия CLI глазами координатора (боевая — <see cref="ManagedCli"/>).</summary>
internal interface IHarness
{
    string? ActiveVersion { get; }
    void SetRequiredVersion(string? version);
    event Action<HarnessStatus>? Changed;
}

internal sealed class ManagedCliHarness(ManagedCli cli) : IHarness
{
    public string? ActiveVersion => cli.ActiveVersion;
    public void SetRequiredVersion(string? version) => cli.SetRequiredVersion(version);

    public event Action<HarnessStatus>? Changed
    {
        add => cli.Changed += value;
        remove => cli.Changed -= value;
    }
}

/// <summary>
/// Жизнь агента в канале управления: hello с возможностью <c>exec</c> и версией управляемой
/// копии CLI, требуемая версия из ответа — в <see cref="IHarness"/>. Поменялась активная
/// копия (<c>ManagedCli.Changed</c>) — hello повторяется, чтобы сервер пересчитал вердикт
/// «харнес готов». Команда открытия исполнения — связь <see cref="ExecLink"/> и ход.
/// </summary>
internal sealed class AgentCoordinator : IAsyncDisposable
{
    public static readonly TimeSpan DefaultMaxOutage = TimeSpan.FromMinutes(10);

    private readonly IControlConnection _control;
    private readonly IHarness _harness;
    private readonly IExecSocketConnector _connector;
    private readonly Func<ExecLink, CancellationToken, Task> _runTurn;
    private readonly string _agentVersion;
    private readonly ILogger _log;
    private readonly TimeSpan _maxOutage;
    private readonly SemaphoreSlim _helloLock = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _turns = [];
    private string? _announcedCli;
    private bool _announced;

    public AgentCoordinator(IControlConnection control, IHarness harness, IExecSocketConnector connector,
        Func<ExecLink, CancellationToken, Task> runTurn, string agentVersion, ILogger? log = null, TimeSpan? maxOutage = null)
    {
        _control = control;
        _harness = harness;
        _connector = connector;
        _runTurn = runTurn;
        _agentVersion = agentVersion;
        _log = log ?? NullLogger.Instance;
        _maxOutage = maxOutage ?? DefaultMaxOutage;

        _control.ExecOpen += OnExecOpenAsync;
        _control.Reconnected += () => HelloAsync(force: true);
        _harness.Changed += OnHarnessChanged;
    }

    public static string PlatformName =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    public DeviceHello BuildHello() => new(
        DesktopProtocol.Version,
        SupportedSteps: [],
        ClientVersion: _agentVersion,
        Platform: PlatformName,
        AgentVersion: _agentVersion,
        CliVersion: _harness.ActiveVersion,
        Capabilities: [DeviceCapabilities.Exec]);

    /// <summary>Hello; force = false — только если активная копия поменялась с прошлого раза.</summary>
    public async Task HelloAsync(bool force = true)
    {
        await _helloLock.WaitAsync(_stopping.Token);
        try
        {
            var hello = BuildHello();
            if (!force && _announced && hello.CliVersion == _announcedCli) return;

            var ack = await _control.HelloAsync(hello, _stopping.Token);
            _announced = true;
            _announcedCli = hello.CliVersion;
            if (ack.HarnessReady) _log.LogInformation("Сервер принял агента: харнес готов (CLI {Version})", hello.CliVersion);
            else _log.LogInformation("Сервер принял агента: {Problem}", ack.HarnessProblem ?? "харнес не готов");
            _harness.SetRequiredVersion(ack.RequiredCliVersion);
        }
        finally
        {
            _helloLock.Release();
        }
    }

    private void OnHarnessChanged(HarnessStatus status)
    {
        if (_stopping.IsCancellationRequested) return;
        _ = Task.Run(async () =>
        {
            try { await HelloAsync(force: false); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "Повторный hello после смены копии CLI не ушёл");
            }
        });
    }

    private async Task OnExecOpenAsync(DeviceExecOpenCommand command)
    {
        if (!DeviceExecProtocol.IsSupportedClientVersion(command.ExecProtocolVersion))
        {
            _log.LogWarning("Сервер просит канал исполнения версии {Version}, агент говорит на {Own}",
                command.ExecProtocolVersion, DeviceExecProtocol.Version);
            return;
        }

        var link = new ExecLink(command.ExecId, _connector, _maxOutage, _log);
        try
        {
            await link.StartAsync(_stopping.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "Канал исполнения {ExecId} не открылся", command.ExecId);
            await link.DisposeAsync();
            return;
        }

        var turn = Task.Run(async () =>
        {
            try { await _runTurn(link, _stopping.Token); }
            catch (Exception e) { _log.LogError(e, "Исполнение {ExecId} упало", command.ExecId); }
        });
        lock (_turns)
        {
            _turns.RemoveAll(t => t.IsCompleted);
            _turns.Add(turn);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _harness.Changed -= OnHarnessChanged;
        await _stopping.CancelAsync();
        Task[] turns;
        lock (_turns) turns = _turns.ToArray();
        try { await Task.WhenAll(turns).WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException) { }
    }
}

/// <summary>Подключение к /api/devices/exec с токеном устройства и отпечатком.</summary>
internal sealed class ExecSocketConnector(IDeviceIdentity device) : IExecSocketConnector
{
    public async Task<WebSocket> ConnectAsync(string execId, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Authorization", SidecarProxy.DeviceAuthPrefix + device.DeviceToken);
        socket.Options.SetRequestHeader(SidecarProxy.FingerprintHeader, device.Fingerprint);
        socket.Options.SetRequestHeader(DeviceExecProtocol.VersionHeader,
            DeviceExecProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        var builder = new UriBuilder(new Uri(device.ServerUri, DeviceExecProtocol.Path.TrimStart('/')))
        {
            Query = "execId=" + Uri.EscapeDataString(execId),
        };
        builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";

        try
        {
            await socket.ConnectAsync(builder.Uri, ct);
            return socket;
        }
        catch (WebSocketException) when (socket.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden
                                              or HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            var status = socket.HttpStatusCode;
            socket.Dispose();
            throw new ExecLinkRefusedException($"сервер отказал в канале исполнения ({(int)status})");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
