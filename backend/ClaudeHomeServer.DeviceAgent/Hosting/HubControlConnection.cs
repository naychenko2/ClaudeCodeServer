using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.DeviceAgent.Sidecar;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.DeviceAgent.Hosting;

/// <summary>
/// Канал управления — тот же хаб /hubs/devices, что у клиента рук ADR-008: авторизация
/// только токеном устройства плюс отпечаток. Имена методов — часть протокола
/// (<c>IDesktopDeviceClient</c> на сервере).
/// </summary>
internal sealed class HubControlConnection : IControlConnection, IAgentTicketIntrospector, IFilesChangedSink, IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly ILogger _log;

    public HubControlConnection(IDeviceIdentity device, ILogger log)
    {
        _log = log;
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(device.ServerUri, "hubs/devices"), o =>
            {
                o.Headers["Authorization"] = SidecarProxy.DeviceAuthPrefix + device.DeviceToken;
                o.Headers[SidecarProxy.FingerprintHeader] = device.Fingerprint;
                o.Transports = HttpTransportType.WebSockets | HttpTransportType.LongPolling;
            })
            .WithAutomaticReconnect(new ForeverRetry())
            .Build();

        _connection.On<DeviceExecOpenCommand>("ExecOpen", command => ExecOpen?.Invoke(command) ?? Task.CompletedTask);
        // Команды рук ADR-008 агенту не адресованы: SupportedSteps пуст, сервер их не шлёт
        _connection.Reconnected += _ => Reconnected?.Invoke() ?? Task.CompletedTask;
    }

    public event Func<DeviceExecOpenCommand, Task>? ExecOpen;
    public event Func<Task>? Reconnected;

    /// <summary>Подключается, повторяя до успеха: сервер мог быть ещё не поднят.</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                await _connection.StartAsync(ct);
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning("Канал управления не поднялся: {Error}; повтор через {Delay} с", e.Message, (int)delay.TotalSeconds);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
        }
    }

    public Task<DeviceHelloAck> HelloAsync(DeviceHello hello, CancellationToken ct) =>
        _connection.InvokeAsync<DeviceHelloAck>("Hello", hello, ct);

    /// <summary>Интроспекция билета localhost-API; канал не поднят — билет не принят.</summary>
    public Task<AgentTicketIntrospection?> IntrospectAsync(string ticket, CancellationToken ct) =>
        _connection.State == HubConnectionState.Connected
            ? _connection.InvokeAsync<AgentTicketIntrospection?>(DeviceAgentApi.IntrospectMethod, ticket, ct)
            : Task.FromResult<AgentTicketIntrospection?>(null);

    /// <summary>Донесение ватчера; канал не поднят — теряется, веб-морда перечитает дерево при открытии.</summary>
    public Task ReportAsync(DeviceFilesChanged report, CancellationToken ct) =>
        _connection.State == HubConnectionState.Connected
            ? _connection.InvokeAsync(DeviceAgentApi.FilesChangedMethod, report, ct)
            : Task.CompletedTask;

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private sealed class ForeverRetry : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context) =>
            TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(context.PreviousRetryCount, 6))));
    }
}
