using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.DeviceAgent.Sidecar;
using ClaudeHomeServer.Protocol;
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

    private static async Task<(SidecarHost Host, TurnGrants Grants, string Key)> StartAsync(FakeGateway gateway, DeviceExecGateway? grant)
    {
        var grants = new TurnGrants();
        var key = grants.Register(grant);
        var host = await SidecarHost.StartAsync(grants, TestDevice, NullLoggerFactory.Instance, gatewayHandler: gateway);
        return (host, grants, key);
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

    [Fact]
    public async Task CONNECT_туннелирует_HTTPS_PROXY_насквозь()
    {
        using var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        var echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var c = await echo.AcceptTcpClientAsync();
            var s = c.GetStream();
            var buf = new byte[64];
            var n = await s.ReadAsync(buf);
            await s.WriteAsync(Encoding.ASCII.GetBytes("pong:" + Encoding.ASCII.GetString(buf, 0, n)));
        });

        var (host, _, _) = await StartAsync(new FakeGateway(_ => Task.FromResult(new HttpResponseMessage())), null);
        await using var _h = host;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, host.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{echoPort} HTTP/1.1\r\nHost: 127.0.0.1:{echoPort}\r\n\r\n"));
        var head = await ReadAsync(stream, "\r\n\r\n");
        head.Should().StartWith("HTTP/1.1 200");

        await stream.WriteAsync("ping"u8.ToArray());
        (await ReadAsync(stream, "ping")).Should().Be("pong:ping");
    }

    [Fact]
    public async Task CONNECT_на_сам_сайдкар_отвергается()
    {
        var (host, _, _) = await StartAsync(new FakeGateway(_ => Task.FromResult(new HttpResponseMessage())), null);
        await using var _h = host;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, host.Port);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{host.Port} HTTP/1.1\r\n\r\n"));
        (await ReadAsync(client.GetStream(), "\r\n\r\n")).Should().StartWith("HTTP/1.1 403");
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
