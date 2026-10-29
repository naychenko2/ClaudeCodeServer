using ClaudeHomeServer.Services.Execution;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services.Devices;

/// <summary>Отправка push <see cref="IDeviceClient.RequestHello"/> в конкретное соединение; шов ради тестов без SignalR.</summary>
public interface IDeviceHelloRequestSender
{
    Task SendRequestHelloAsync(string connectionId, CancellationToken ct = default);
}

public sealed class DeviceHubHelloRequestSender(IHubContext<DeviceHub, IDeviceClient> hub) : IDeviceHelloRequestSender
{
    public Task SendRequestHelloAsync(string connectionId, CancellationToken ct = default)
        => hub.Clients.Client(connectionId).RequestHello();
}

/// <summary>
/// Смена версии CLI хоста (автообновление) меняет требуемую версию устройств, а агент узнаёт её
/// только из ack на Hello. Рассылаем всем подключённым устройствам просьбу повторить Hello.
/// Прибитая в конфиге версия (<see cref="DeviceHarnessPolicy.CliVersionKey"/>) от хоста не
/// зависит — тогда ничего не рассылаем.
/// </summary>
public sealed class DeviceHelloRequestBroadcaster(
    IHostCliVersion hostCli,
    DeviceConnectionRegistry connections,
    IDeviceHelloRequestSender sender,
    IConfiguration config,
    ILogger<DeviceHelloRequestBroadcaster> log) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        hostCli.Changed += OnHostCliChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        hostCli.Changed -= OnHostCliChanged;
        return Task.CompletedTask;
    }

    private void OnHostCliChanged() => _ = BroadcastAsync();

    internal async Task BroadcastAsync()
    {
        try
        {
            if (DeviceHarnessPolicy.Normalize(config[DeviceHarnessPolicy.CliVersionKey]) is not null) return;
            foreach (var conn in connections.ReadyConnections())
            {
                try { await sender.SendRequestHelloAsync(conn.ConnectionId); }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "RequestHello устройству {DeviceId} не ушёл", conn.DeviceId);
                }
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Рассылка RequestHello после смены версии CLI хоста не удалась");
        }
    }
}
