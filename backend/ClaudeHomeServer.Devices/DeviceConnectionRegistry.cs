using System.Collections.Concurrent;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Devices;

/// <summary>Живое соединение устройства. Ready — устройство представилось (Hello).</summary>
public sealed record DeviceConnection(
    string ConnectionId,
    string OwnerId,
    string DeviceId,
    DateTimeOffset ConnectedAt)
{
    public int ProtocolVersion { get; init; }
    public bool Ready { get; init; }
}

/// <summary>
/// Наблюдатель соединений устройств: выход в онлайн будит работу, ждавшую устройство
/// (ADR-016, <c>DeviceOnlineDispatcher</c>).
/// </summary>
public interface IDeviceConnectionObserver
{
    /// <summary>Устройство представилось и готово принимать команды.</summary>
    Task OnDeviceOnlineAsync(DeviceConnection connection, CancellationToken ct = default);

    /// <summary>Соединение с устройством разорвано.</summary>
    Task OnDeviceOfflineAsync(DeviceConnection connection, CancellationToken ct = default);
}

/// <summary>
/// Реестр соединений хаба устройств (ADR-016): кто на связи и в какое соединение слать
/// команду открытия канала исполнения. Одно устройство — одно соединение.
/// </summary>
public sealed class DeviceConnectionRegistry
{
    private readonly IEnumerable<IDeviceConnectionObserver> _observers;
    private readonly ILogger<DeviceConnectionRegistry> _log;
    private readonly TimeProvider _time;

    // connectionId → соединение; устройств у владельца может быть несколько, соединение одно
    private readonly ConcurrentDictionary<string, DeviceConnection> _connections = new();

    public DeviceConnectionRegistry(
        IEnumerable<IDeviceConnectionObserver> observers,
        ILogger<DeviceConnectionRegistry> log,
        TimeProvider? timeProvider = null)
    {
        _observers = observers;
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Соединение установлено; команды не идут, пока устройство не представилось.</summary>
    public void RegisterConnection(string connectionId, string ownerId, string deviceId) =>
        _connections[connectionId] = new DeviceConnection(connectionId, ownerId, deviceId, _time.GetUtcNow());

    /// <summary>
    /// Устройство представилось: фиксируем версию протокола, после чего устройство онлайн и
    /// о нём узнают наблюдатели.
    /// </summary>
    public async Task<DeviceHelloAck> HelloAsync(string connectionId, DeviceHello hello, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(connectionId, out var conn))
            throw new InvalidOperationException("Соединение устройства не зарегистрировано");

        var wasReady = conn.Ready;
        // Одно устройство — одно соединение: прежнее (например, зависшее после спящего режима)
        // убираем из реестра, чтобы push не уходил в мёртвый канал.
        foreach (var (id, other) in _connections)
        {
            if (id == connectionId || other.OwnerId != conn.OwnerId || other.DeviceId != conn.DeviceId) continue;
            _connections.TryRemove(id, out _);
        }

        var ready = conn with
        {
            ProtocolVersion = hello.ProtocolVersion,
            Ready = true
        };
        _connections[connectionId] = ready;

        if (!wasReady) await NotifyAsync(o => o.OnDeviceOnlineAsync(ready, ct));

        return new DeviceHelloAck(
            DesktopProtocol.Version,
            (int)DesktopProtocol.AckTimeout.TotalSeconds,
            DesktopProtocol.MaxResultBytes,
            DesktopProtocol.MaxBatchSteps);
    }

    /// <summary>Соединение разорвано: устройство уходит в офлайн.</summary>
    public async Task RemoveConnectionAsync(string connectionId, CancellationToken ct = default)
    {
        if (!_connections.TryRemove(connectionId, out var conn)) return;
        if (conn.Ready) await NotifyAsync(o => o.OnDeviceOfflineAsync(conn, ct));
    }

    /// <summary>Готовое к работе соединение устройства владельца, либо null.</summary>
    public DeviceConnection? Find(string ownerId, string deviceId) =>
        _connections.Values.FirstOrDefault(c =>
            c.Ready && c.OwnerId == ownerId && c.DeviceId == deviceId);

    public bool IsOnline(string ownerId, string deviceId) => Find(ownerId, deviceId) is not null;

    private async Task NotifyAsync(Func<IDeviceConnectionObserver, Task> action)
    {
        foreach (var observer in _observers)
        {
            try
            {
                await action(observer);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Наблюдатель соединений устройств упал");
            }
        }
    }
}
