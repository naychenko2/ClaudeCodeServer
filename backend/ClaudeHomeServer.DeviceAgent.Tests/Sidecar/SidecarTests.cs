using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.DeviceAgent.Sidecar;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Tests.Sidecar;

public class SidecarTests
{
    private const string DeviceToken = "device-token-SECRET-77";
    private const string TurnToken = "turn-token-SECRET-88";

    private sealed record Device(Uri ServerUri, string DeviceToken, string Fingerprint) : IDeviceIdentity;

    private static readonly Device TestDevice = new(new Uri("https://server.example/"), DeviceToken, "fp-abc");

    /// <summary>Фейковый шлюз: запоминает запросы, ответ — по функции.</summary>
    private sealed class FakeGateway(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            lock (Seen) Seen.Add((request, body));
            return await respond(request);
        }
    }

    private static async Task<(SidecarHost Host, TurnGrants Grants, string Key)> StartAsync(
        FakeGateway gateway, DeviceExecGateway? grant, IEgressTunnelOpener? egress = null, IDeviceIdentity? device = null)
    {
        var grants = new TurnGrants();
        var key = grants.Register(grant);
        var host = await SidecarHost.StartAsync(grants, device ?? TestDevice, NullLoggerFactory.Instance,
            gatewayHandler: gateway, egress: egress ?? new FakeEgress((_, _, _) => Task.FromResult(new EgressTunnel(null, 502))));
        return (host, grants, key);
    }

    /// <summary>Фейковый сервер выхода: запоминает, что просили, туннель — по функции.</summary>
    private sealed class FakeEgress(Func<DeviceExecGateway, string, int, Task<EgressTunnel>> open) : IEgressTunnelOpener
    {
        public List<(DeviceExecGateway Grant, string Host, int Port)> Calls { get; } = [];

        public Task<EgressTunnel> OpenAsync(DeviceExecGateway grant, string host, int port, CancellationToken ct)
        {
            lock (Calls) Calls.Add((grant, host, port));
            return open(grant, host, port);
        }
    }

    private static HttpClient Client() => new(new SocketsHttpHandler { UseProxy = false });

    [Fact]
    public async Task Сайдкар_слушает_только_loopback()
    {
        var (host, _, _) = await StartAsync(new FakeGateway(_ => Task.FromResult(new HttpResponseMessage())), null);
        await using var _h = host;
        new Uri(host.Url).Host.Should().Be("127.0.0.1");
        host.Port.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task LLM_уходит_в_шлюз_хода_с_токеном_устройства_и_хода_а_авторизация_CLI_выбрасывается()
    {
        var gateway = new FakeGateway(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
        }));
        var (host, _, key) = await StartAsync(gateway, new DeviceExecGateway("gw-turn-9", TurnToken));
        await using var _h = host;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{host.Url}/t/{key}/llm/v1/messages?beta=true")
        {
            Content = new StringContent("""{"model":"x"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CliEnvironment.AuthPlaceholder);
        request.Headers.Add("x-api-key", "cli-key");
        request.Headers.Add("X-Turn-Token", "forged-by-cli");
        request.Headers.Add("X-Caller-Session-Id", "foreign-chat");
        request.Headers.Add("anthropic-version", "2023-06-01");
        using var response = await Client().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("""{"ok":true}""");

        var (sent, body) = gateway.Seen.Single();
        sent.RequestUri!.ToString().Should().Be("https://server.example/gw/t/gw-turn-9/llm/v1/messages?beta=true");
        sent.Method.Should().Be(HttpMethod.Post);
        body.Should().Be("""{"model":"x"}""");
        sent.Headers.GetValues("Authorization").Should().Equal("Device " + DeviceToken);
        sent.Headers.GetValues("X-Turn-Token").Should().Equal(TurnToken);
        sent.Headers.GetValues("X-Device-Fingerprint").Should().Equal("fp-abc");
        sent.Headers.GetValues("anthropic-version").Should().Equal("2023-06-01");
        sent.Headers.Contains("x-api-key").Should().BeFalse();
        sent.Headers.Contains("X-Caller-Session-Id").Should().BeFalse("контекст MCP ставит шлюз по привязке токена");
        sent.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("OPTIONS")]
    public async Task MCP_пропускает_любой_метод(string method)
    {
        var gateway = new FakeGateway(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)));
        var (host, _, key) = await StartAsync(gateway, new DeviceExecGateway("gw-1", TurnToken));
        await using var _h = host;

        using var request = new HttpRequestMessage(new HttpMethod(method), $"{host.Url}/t/{key}/mcp/tasks/s1");
        if (method is "POST" or "PUT" or "PATCH") request.Content = new StringContent("{}");
        using var response = await Client().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        gateway.Seen.Single().Request.RequestUri!.AbsolutePath.Should().Be("/gw/t/gw-1/mcp/tasks/s1");
        gateway.Seen.Single().Request.Method.Method.Should().Be(method);
    }

    [Fact]
    public async Task Неизвестный_ход_и_ход_без_выдачи_шлюза_получают_отказ()
    {
        var gateway = new FakeGateway(_ => Task.FromResult(new HttpResponseMessage()));
        var (host, grants, _) = await StartAsync(gateway, new DeviceExecGateway("gw-1", TurnToken));
        await using var _h = host;
        var noGrant = grants.Register(null);

        using var unknown = await Client().GetAsync($"{host.Url}/t/{new string('0', 32)}/llm/v1/models");
        using var bad = await Client().GetAsync($"{host.Url}/v1/messages");
        using var missing = await Client().GetAsync($"{host.Url}/t/{noGrant}/llm/v1/models");

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        bad.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        gateway.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task Ответ_шлюза_идёт_потоком_без_буферизации()
    {
        var release = new TaskCompletionSource();
        var gateway = new FakeGateway(_ =>
        {
            var content = new PushStreamContent(async stream =>
            {
                await stream.WriteAsync("event: delta\ndata: 1\n\n"u8.ToArray());
                await stream.FlushAsync();
                await release.Task;
                await stream.WriteAsync("event: delta\ndata: 2\n\n"u8.ToArray());
            });
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        var (host, _, key) = await StartAsync(gateway, new DeviceExecGateway("gw-1", TurnToken));
        await using var _h = host;

        try
        {
            // Первая дельта приходит, пока шлюз ещё держит вторую: буферизующий сайдкар не
            // отдал бы даже заголовков
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await Client().SendAsync(
                new HttpRequestMessage(HttpMethod.Post, $"{host.Url}/t/{key}/llm/v1/messages") { Content = new StringContent("{}") },
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[256];
            var first = await body.ReadAsync(buffer, timeout.Token);
            Encoding.UTF8.GetString(buffer, 0, first).Should().Contain("data: 1");

            release.SetResult();
            var rest = new StringBuilder();
            int n;
            while ((n = await body.ReadAsync(buffer)) > 0) rest.Append(Encoding.UTF8.GetString(buffer, 0, n));
            rest.ToString().Should().Contain("data: 2");
        }
        finally
        {
            // Иначе при провале фейковый шлюз держит запрос, и остановка сайдкара ждёт вечно
            release.TrySetResult();
        }
    }

    // Эхо-сервер на loopback: отвечает префиксом, по которому видно, кто на том конце
    private static (TcpListener Listener, int Port) Echo(string prefix)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                using var c = await listener.AcceptTcpClientAsync();
                var s = c.GetStream();
                var buf = new byte[64];
                var n = await s.ReadAsync(buf);
                await s.WriteAsync(Encoding.ASCII.GetBytes(prefix + Encoding.ASCII.GetString(buf, 0, n)));
                await Task.Delay(500);
            }
            catch (Exception) { /* слушатель остановлен тестом */ }
        });
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    private static string ProxyAuth(string key) =>
        "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes($"turn:{key}")) + "\r\n";

    private static async Task<TcpClient> ConnectAsync(SidecarHost host, string target, string extraHeaders)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, host.Port);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n{extraHeaders}\r\n"));
        return client;
    }

    [Fact]
    public async Task CONNECT_едет_через_сервер_а_не_напрямую_с_машины()
    {
        // Цель CONNECT слушает на этой же машине — прямой туннель сайдкара дошёл бы до неё
        var (direct, directPort) = Echo("direct:");
        var (server, serverPort) = Echo("via-server:");
        using var _d = direct.Server;
        using var _s = server.Server;
        var egress = new FakeEgress(async (_, _, _) =>
        {
            var toServer = new TcpClient();
            await toServer.ConnectAsync(IPAddress.Loopback, serverPort);
            return new EgressTunnel(toServer.GetStream(), 0);
        });
        var (host, _, key) = await StartAsync(new FakeGateway(_ => Task.FromResult(new HttpResponseMessage())),
            new DeviceExecGateway("gw-turn-5", TurnToken), egress);
        await using var _h = host;

        using var client = await ConnectAsync(host, $"127.0.0.1:{directPort}", ProxyAuth(key));
        var stream = client.GetStream();
        (await ReadAsync(stream, "\r\n\r\n")).Should().StartWith("HTTP/1.1 200");
        await stream.WriteAsync("ping"u8.ToArray());
        (await ReadAsync(stream, "ping")).Should().Be("via-server:ping");

        egress.Calls.Should().ContainSingle().Which.Should().Be((new DeviceExecGateway("gw-turn-5", TurnToken), "127.0.0.1", directPort));
        direct.Pending().Should().BeFalse("с машины наружу сайдкар сам не ходит");
    }

    [Fact]
    public async Task CONNECT_без_учётки_хода_или_с_чужой_отвергается_407_без_похода_на_сервер()
    {
        var egress = new FakeEgress((_, _, _) => Task.FromResult(new EgressTunnel(null, 502)));
        var (host, _, _) = await StartAsync(new FakeGateway(_ => Task.FromResult(new HttpResponseMessage())),
            new DeviceExecGateway("gw-1", TurnToken), egress);
        await using var _h = host;

        foreach (var headers in new[] { "", ProxyAuth("0123456789abcdef0123456789abcdef"), "Proxy-Authorization: Bearer x\r\n" })
        {
            using var client = await ConnectAsync(host, "example.com:443", headers);
            (await ReadAsync(client.GetStream(), "\r\n\r\n")).Should().StartWith("HTTP/1.1 407");
        }
        egress.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task CONNECT_хода_без_выдачи_шлюза_получает_503()
    {
        var egress = new FakeEgress((_, _, _) => Task.FromResult(new EgressTunnel(null, 502)));
        var (host, _, key) = await StartAsync(new FakeGateway(_ => Task.FromResult(new HttpResponseMessage())), null, egress);
        await using var _h = host;

        using var client = await ConnectAsync(host, "example.com:443", ProxyAuth(key));
        (await ReadAsync(client.GetStream(), "\r\n\r\n")).Should().StartWith("HTTP/1.1 503");
        egress.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(403, "HTTP/1.1 403")]
    [InlineData(401, "HTTP/1.1 502")]
    [InlineData(502, "HTTP/1.1 502")]
    public async Task Отказ_сервера_доходит_до_CLI_кодом_CONNECT(int serverStatus, string expected)
    {
        var egress = new FakeEgress((_, _, _) => Task.FromResult(new EgressTunnel(null, serverStatus)));
        var (host, _, key) = await StartAsync(new FakeGateway(_ => Task.FromResult(new HttpResponseMessage())),
            new DeviceExecGateway("gw-1", TurnToken), egress);
        await using var _h = host;

        using var client = await ConnectAsync(host, "example.com:443", ProxyAuth(key));
        (await ReadAsync(client.GetStream(), "\r\n\r\n")).Should().StartWith(expected);
    }

    [Fact]
    public async Task Туннель_уходит_на_сервер_WebSocketом_с_токенами_устройства_и_хода()
    {
        var seen = new TaskCompletionSource<HttpRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var fakeServer = builder.Build();
        fakeServer.UseWebSockets();
        fakeServer.Map("/gw/t/{turnId}/egress", async (HttpContext http) =>
        {
            seen.TrySetResult(http.Request);
            if (http.Request.Query["host"] == "denied.example")
            {
                http.Response.StatusCode = 403;
                return;
            }
            using var ws = await http.WebSockets.AcceptWebSocketAsync();
            var buf = new byte[64];
            var got = await ws.ReceiveAsync(buf, CancellationToken.None);
            await ws.SendAsync(Encoding.ASCII.GetBytes("ws:" + Encoding.ASCII.GetString(buf, 0, got.Count)),
                System.Net.WebSockets.WebSocketMessageType.Binary, true, CancellationToken.None);
            try { await ws.ReceiveAsync(buf, CancellationToken.None); }
            catch (System.Net.WebSockets.WebSocketException) { /* сайдкар закрыл туннель */ }
        });
        await fakeServer.StartAsync();
        var serverUri = new Uri(fakeServer.Urls.First() + "/");

        var device = new Device(serverUri, DeviceToken, "fp-abc");
        var (host, _, key) = await StartAsync(new FakeGateway(_ => Task.FromResult(new HttpResponseMessage())),
            new DeviceExecGateway("gw-turn-7", TurnToken), new ServerEgressTunnelOpener(device), device);
        await using var _h = host;

        using var client = await ConnectAsync(host, "api.example.com:443", ProxyAuth(key));
        var stream = client.GetStream();
        (await ReadAsync(stream, "\r\n\r\n")).Should().StartWith("HTTP/1.1 200");
        await stream.WriteAsync("ping"u8.ToArray());
        (await ReadAsync(stream, "ping")).Should().Be("ws:ping");

        var request = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        request.RouteValues["turnId"].Should().Be("gw-turn-7");
        request.Query["host"].ToString().Should().Be("api.example.com");
        request.Query["port"].ToString().Should().Be("443");
        request.Headers.Authorization.ToString().Should().Be(SidecarProxy.DeviceAuthPrefix + DeviceToken);
        request.Headers[SidecarProxy.FingerprintHeader].ToString().Should().Be("fp-abc");
        request.Headers[SidecarProxy.TurnTokenHeader].ToString().Should().Be(TurnToken);
        request.Headers.ContainsKey("Proxy-Authorization").Should().BeFalse();

        using var denied = await ConnectAsync(host, "denied.example:443", ProxyAuth(key));
        (await ReadAsync(denied.GetStream(), "\r\n\r\n")).Should().StartWith("HTTP/1.1 403");
    }

    [Fact]
    public void Выдача_шлюза_не_печатает_токен()
    {
        new DeviceExecGateway("gw-1", TurnToken).ToString().Should().NotContain(TurnToken);
        new DeviceExecControl(DeviceExecControlOps.Spawn, "t", null, new DeviceExecGateway("gw-1", TurnToken)).ToString()
            .Should().NotContain(TurnToken);
    }

    private static async Task<string> ReadAsync(NetworkStream stream, string until)
    {
        var sb = new StringBuilder();
        var buf = new byte[256];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!sb.ToString().Contains(until))
        {
            var n = await stream.ReadAsync(buf, cts.Token);
            if (n == 0) break;
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
        }
        return sb.ToString();
    }

    private sealed class PushStreamContent(Func<Stream, Task> write) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => write(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            // Поток с писателем в фоне: ReadAsStreamAsync отдаёт данные по мере записи
            var pipe = new System.IO.Pipelines.Pipe();
            _ = Task.Run(async () =>
            {
                await using var writer = pipe.Writer.AsStream();
                await write(new FlushingStream(writer));
            });
            return Task.FromResult(pipe.Reader.AsStream());
        }
    }

    private sealed class FlushingStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct);
            await inner.FlushAsync(ct);
        }
    }
}
