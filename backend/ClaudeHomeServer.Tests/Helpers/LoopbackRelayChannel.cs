using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Ретранслятор без хаба: настоящий серверный <see cref="DeviceExecStream"/> и настоящая связь
/// агента <see cref="ExecLink"/> соединены WebSocket поверх loopback TCP, команду «открой канал
/// relay» агент получает сразу. <see cref="Agent"/> — обработчик агента (боевой — RelayHandler).
/// </summary>
internal sealed class LoopbackRelayChannel : IDeviceRelayChannel
{
    private int _opened;

    public Func<ExecLink, CancellationToken, Task>? Agent { get; set; }
    public DeviceExecRefusedException? Refuse { get; set; }
    public int Opened => _opened;

    public async Task<IDeviceExecStream> OpenRelayAsync(string ownerId, string deviceId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _opened);
        if (Refuse is not null) throw Refuse;
        var execId = Guid.NewGuid().ToString("N");
        var stream = new DeviceExecStream(execId, ownerId, deviceId, _ => { }, RelayProtocol.MaxOutage);
        var link = new ExecLink(execId, new Connector(stream), RelayProtocol.MaxOutage);
        await link.StartAsync(ct);
        var agent = Agent ?? throw new InvalidOperationException("Агент ретранслятора не задан");
        _ = Task.Run(() => agent(link, CancellationToken.None));
        return stream;
    }

    private sealed class Connector(DeviceExecStream stream) : IExecSocketConnector
    {
        public async Task<WebSocket> ConnectAsync(string execId, CancellationToken ct)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            var accept = listener.AcceptTcpClientAsync(ct).AsTask();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
            var serverSide = await accept;

            var serverSocket = WebSocket.CreateFromStream(serverSide.GetStream(), isServer: true, null, Timeout.InfiniteTimeSpan);
            _ = Task.Run(() => stream.RunAsync(serverSocket, CancellationToken.None));
            return WebSocket.CreateFromStream(client.GetStream(), isServer: false, null, Timeout.InfiniteTimeSpan);
        }
    }
}

/// <summary>Состояние устройств для матрицы: словарь, который тест меняет на ходу.</summary>
internal sealed class FakeDeviceStatus : IDeviceExecChannel
{
    public Dictionary<string, DeviceExecStatus> Devices { get; } = [];

    public DeviceExecStatus? GetStatus(string ownerId, string deviceId) => Devices.GetValueOrDefault(deviceId);

    public Task<IDeviceExecStream> OpenAsync(string ownerId, string deviceId, CancellationToken ct = default) =>
        throw new NotSupportedException("Ходы в этих тестах не запускаются");

    public static DeviceExecStatus Status(string id, bool online, params string[] caps) =>
        new(id, "home", online, "linux-x64", "1.0.0", "2.0.0", "2.0.0", caps, true, null);
}
