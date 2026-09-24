using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services.Gateway;

/// <summary>
/// Туннель выхода CLI устройства (ADR-016 §2, задача 2.9) на боевом Program: сервер — не
/// открытый прокси. Вход только с токеном хода и учёткой его устройства, SSRF-граница после
/// разрешения имени, порты из конфига, тумблер выключен — честный 403.
/// </summary>
public sealed class EgressGatewayTests : IDisposable
{
    private readonly List<IDisposable> _cleanup = [];

    public void Dispose()
    {
        foreach (var d in _cleanup) d.Dispose();
    }

    private TestWebApplicationFactory Factory(bool egress = true, EgressConnector? connector = null,
        Dictionary<string, string>? limits = null)
    {
        var factory = new TestWebApplicationFactory();
        factory.ExtraConfig["LlmGateway:Enabled"] = "true";
        factory.ExtraConfig["LlmGateway:Egress:Enabled"] = egress ? "true" : "false";
        foreach (var (key, value) in limits ?? []) factory.ExtraConfig["LlmGateway:Egress:" + key] = value;
        if (connector is not null) factory.ExtraServices = s => s.AddSingleton(connector);
        _cleanup.Add(factory);
        return factory;
    }

    private (GatewayTestDevice Device, IssuedTurnToken Turn) Turn(TestWebApplicationFactory factory)
    {
        var device = new GatewayTestDevice("owner-1", factory.Services.GetRequiredService<DeviceRegistry>());
        _cleanup.Add(device);
        return (device, factory.Services.GetRequiredService<TurnTokenService>().Issue("owner-1", "chat-1", device.Id));
    }

    private static HttpRequestMessage Request(IssuedTurnToken turn, string host, int port, GatewayTestDevice? device)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/{DeviceEgressRoutes.GatewayPath(turn.Grant.TurnId)}?host={Uri.EscapeDataString(host)}&port={port}");
        request.Headers.Add(TurnTokenEndpointFilter.HeaderName, turn.Token);
        device?.Sign(request);
        return request;
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.8.8.8")]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:c0a8:101::1")]
    [InlineData("2001::1")]
    [InlineData("2001:0:53aa:64c::1")]
    public void Внутренние_адреса_и_метаданные_облака_запрещены(string address) =>
        new EgressAddressPolicy().IsForbidden(IPAddress.Parse(address)).Should().BeTrue();

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2001:1::1")]
    [InlineData("2001:db8::1")]
    public void Публичные_адреса_разрешены(string address) =>
        EgressAddressPolicy.IsForbiddenRange(IPAddress.Parse(address)).Should().BeFalse();

    [Fact]
    public async Task Имя_проверяется_после_разрешения_localhost_запрещён()
    {
        var connector = new EgressConnector(null, new EgressAddressPolicy());

        var resolved = await connector.ResolveAsync("localhost", CancellationToken.None);

        resolved.Addresses.Should().BeNull();
        resolved.Status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Тумблер_выключен_честный_отказ_403_с_причиной()
    {
        var factory = Factory(egress: false);
        var (device, turn) = Turn(factory);

        var response = await factory.CreateClient().SendAsync(Request(turn, "example.com", 443, device));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Egress:Enabled");
    }

    [Fact]
    public async Task Без_учётки_устройства_или_с_чужим_устройством_401()
    {
        var factory = Factory();
        var (device, turn) = Turn(factory);
        var client = factory.CreateClient();

        (await client.SendAsync(Request(turn, "example.com", 443, null))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var (_, foreignToken) = device.Register("owner-1", "other", new string('f', 64));
        var foreign = Request(turn, "example.com", 443, null);
        GatewayTestDevice.Sign(foreign, foreignToken, new string('f', 64));
        (await client.SendAsync(foreign)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("127.0.0.1", 443)]
    [InlineData("localhost", 443)]
    [InlineData("169.254.169.254", 443)]
    [InlineData("10.0.0.5", 443)]
    [InlineData("8.8.8.8", 22)]
    public async Task Запрещённый_адрес_или_порт_403_до_соединения(string host, int port)
    {
        var factory = Factory();
        var (device, turn) = Turn(factory);

        var response = await factory.CreateClient().SendAsync(Request(turn, host, port, device));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Туннель_идёт_через_прокси_сервера_на_проверенный_IP_и_несёт_байты()
    {
        using var proxy = new FakeHttpProxy("HTTP/1.1 200 Connection established");
        var factory = Factory(connector: new EgressConnector(new Uri($"http://127.0.0.1:{proxy.Port}"), new EgressAddressPolicy()));
        var (device, turn) = Turn(factory);

        var ws = ((TestServer)factory.Server).CreateWebSocketClient();
        ws.ConfigureRequest = r =>
        {
            r.Headers["Authorization"] = DesktopDeviceAuthHandler.TokenPrefix + device.Token;
            r.Headers[TurnTokenEndpointFilter.DeviceFingerprintHeader] = GatewayTestDevice.Fingerprint;
            r.Headers[TurnTokenEndpointFilter.HeaderName] = turn.Token;
        };
        using var socket = await ws.ConnectAsync(
            new Uri($"ws://localhost/{DeviceEgressRoutes.GatewayPath(turn.Grant.TurnId)}?host=93.184.216.34&port=443"),
            CancellationToken.None);

        await socket.SendAsync("ping"u8.ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
        var buffer = new byte[64];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var got = await socket.ReceiveAsync(buffer, timeout.Token);

        Encoding.ASCII.GetString(buffer, 0, got.Count).Should().Be("proxy:ping");
        (await proxy.Head.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().StartWith("CONNECT 93.184.216.34:443 HTTP/1.1");
    }

    [Fact]
    public async Task Отказ_прокси_сервера_502_а_не_прямой_выход()
    {
        using var proxy = new FakeHttpProxy("HTTP/1.1 403 Forbidden");
        var connector = new EgressConnector(new Uri($"http://127.0.0.1:{proxy.Port}"), new EgressAddressPolicy());

        var opened = await connector.ConnectAsync([IPAddress.Parse("93.184.216.34")], 443, CancellationToken.None);

        opened.Stream.Should().BeNull();
        opened.Status.Should().Be(StatusCodes.Status502BadGateway);
        opened.Outcome.Should().Contain("403");
    }

    // ---- G12: потолки туннеля ----

    [Fact]
    public async Task G12_Туннель_без_активности_закрывается_по_бездействию()
    {
        var factory = Factory(connector: new SinkConnector(), limits: new() { ["InactivityTimeout"] = "00:00:00.500" });
        var (device, turn) = Turn(factory);
        using var socket = await OpenTunnelAsync(factory, device, turn);

        (await ClosedWithinAsync(socket, TimeSpan.FromSeconds(10))).Should().BeTrue("сервер обязан закрыть туннель без байт");
    }

    [Fact]
    public async Task G12_Туннель_с_потоком_данных_закрывается_по_потолку_жизни()
    {
        var factory = Factory(connector: new SinkConnector(), limits: new()
        {
            ["InactivityTimeout"] = "00:00:05",
            ["MaxLifetime"] = "00:00:01",
        });
        var (device, turn) = Turn(factory);
        using var socket = await OpenTunnelAsync(factory, device, turn);
        using var stop = new CancellationTokenSource();
        var started = DateTime.UtcNow;
        var feeder = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                try { await socket.SendAsync("x"u8.ToArray(), WebSocketMessageType.Binary, true, stop.Token); }
                catch (Exception) { return; }
                await Task.Delay(50, CancellationToken.None);
            }
        });

        var closed = await ClosedWithinAsync(socket, TimeSpan.FromSeconds(10));
        await stop.CancelAsync();
        await feeder;

        closed.Should().BeTrue("поток данных не продлевает жизнь туннеля сверх потолка");
        (DateTime.UtcNow - started).Should().BeGreaterThan(TimeSpan.FromMilliseconds(800));
    }

    [Fact]
    public async Task G12_Туннель_закрывается_по_потолку_байтов()
    {
        var factory = Factory(connector: new SinkConnector(), limits: new() { ["MaxBytesPerTunnel"] = "1024" });
        var (device, turn) = Turn(factory);
        using var socket = await OpenTunnelAsync(factory, device, turn);

        await socket.SendAsync(new byte[2048], WebSocketMessageType.Binary, true, CancellationToken.None);

        (await ClosedWithinAsync(socket, TimeSpan.FromSeconds(10))).Should().BeTrue();
    }

    [Fact]
    public async Task G12_Девятый_одновременный_туннель_хода_429()
    {
        var factory = Factory(connector: new SinkConnector());
        var (device, turn) = Turn(factory);
        var open = new List<WebSocket>();
        for (var i = 0; i < 8; i++) open.Add(await OpenTunnelAsync(factory, device, turn));

        var response = await factory.CreateClient().SendAsync(Request(turn, "93.184.216.34", 443, device));

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        foreach (var s in open) s.Dispose();
    }

    [Fact]
    public async Task G12_Семнадцатый_одновременный_туннель_устройства_429()
    {
        var factory = Factory(connector: new SinkConnector());
        var (device, first) = Turn(factory);
        var tokens = factory.Services.GetRequiredService<TurnTokenService>();
        var second = tokens.Issue("owner-1", "chat-2", device.Id);
        var third = tokens.Issue("owner-1", "chat-3", device.Id);
        var open = new List<WebSocket>();
        for (var i = 0; i < 8; i++) open.Add(await OpenTunnelAsync(factory, device, first));
        for (var i = 0; i < 8; i++) open.Add(await OpenTunnelAsync(factory, device, second));

        var response = await factory.CreateClient().SendAsync(Request(third, "93.184.216.34", 443, device));

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        foreach (var s in open) s.Dispose();
    }

    [Fact]
    public void G12_Место_возвращается_по_концу_туннеля()
    {
        var limiter = new EgressTunnelLimiter();
        var leases = Enumerable.Range(0, 2).Select(_ => limiter.TryAcquire("t", "d", 2, 16)).ToList();
        limiter.TryAcquire("t", "d", 2, 16).Should().BeNull();

        leases[0]!.Dispose();
        leases[0]!.Dispose();

        limiter.TryAcquire("t", "d", 2, 16).Should().NotBeNull();
        limiter.TryAcquire("t", "d", 2, 16).Should().BeNull("повторный Dispose не освобождает место дважды");
    }

    private static async Task<WebSocket> OpenTunnelAsync(TestWebApplicationFactory factory, GatewayTestDevice device, IssuedTurnToken turn)
    {
        var ws = ((TestServer)factory.Server).CreateWebSocketClient();
        ws.ConfigureRequest = r =>
        {
            r.Headers["Authorization"] = DesktopDeviceAuthHandler.TokenPrefix + device.Token;
            r.Headers[TurnTokenEndpointFilter.DeviceFingerprintHeader] = GatewayTestDevice.Fingerprint;
            r.Headers[TurnTokenEndpointFilter.HeaderName] = turn.Token;
        };
        return await ws.ConnectAsync(
            new Uri($"ws://localhost/{DeviceEgressRoutes.GatewayPath(turn.Grant.TurnId)}?host=93.184.216.34&port=443"),
            CancellationToken.None);
    }

    // Закрытие сервером: пришёл Close или сокет оборван. false — туннель жив дольше потолка ожидания
    private static async Task<bool> ClosedWithinAsync(WebSocket socket, TimeSpan limit)
    {
        using var timeout = new CancellationTokenSource(limit);
        var buffer = new byte[256];
        try
        {
            while (true)
            {
                var got = await socket.ReceiveAsync(buffer, timeout.Token);
                if (got.MessageType == WebSocketMessageType.Close) return true;
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return false; }
        catch (Exception) { return true; }
    }

    // Соединение наружу без сети: пишет в никуда и молчит до отмены
    private sealed class SinkConnector() : EgressConnector(null, new EgressAddressPolicy())
    {
        public override Task<Opened> ConnectAsync(IPAddress[] addresses, int port, CancellationToken ct) =>
            Task.FromResult(new Opened(new SinkStream(), 0, ""));
    }

    private sealed class SinkStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => ValueTask.CompletedTask;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }

    // HTTP-прокси на loopback: запоминает заголовок CONNECT, отвечает заданной строкой и при
    // успехе отдаёт эхо с префиксом
    private sealed class FakeHttpProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public TaskCompletionSource<string> Head { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Port { get; }

        public FakeHttpProxy(string statusLine)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    var head = new StringBuilder();
                    var one = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one) > 0)
                        head.Append((char)one[0]);
                    Head.TrySetResult(head.ToString());
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(statusLine + "\r\n\r\n"));
                    if (!statusLine.Contains(" 200 ")) return;
                    var buffer = new byte[64];
                    var n = await stream.ReadAsync(buffer);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("proxy:" + Encoding.ASCII.GetString(buffer, 0, n)));
                    await Task.Delay(1000);
                }
                catch (Exception) { /* тест закончился раньше */ }
            });
        }

        public void Dispose() => _listener.Stop();
    }
}
