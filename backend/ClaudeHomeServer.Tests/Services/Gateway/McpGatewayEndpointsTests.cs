using System.Collections.Concurrent;
using System.Net;
using System.Text;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Tests.Services.Gateway;

/// <summary>
/// Шлюз MCP (ADR-016, задача 1.3): вход по токену хода, JWT владельца и чат-вызыватель
/// ставит шлюз, хвост сверяется с чатом токена, любые методы и стрим — насквозь.
/// Бэкенд — фейковый TestServer, который записывает, что до него доехало.
/// </summary>
public sealed class McpGatewayEndpointsTests : IAsyncLifetime
{
    private sealed record Seen(string Method, string Path, string? Authorization, string? Caller,
        string? TurnToken, string? Cookie, string Body, string? Fingerprint = null);

    private sealed class FakeBackend : IMcpBackendAccess
    {
        public string ApiUrl => "http://backend";
        public string ServiceTokenFor(string ownerId) => "jwt-of-" + ownerId;
    }

    private readonly ConcurrentQueue<Seen> _seen = new();
    private readonly TaskCompletionSource _releaseStream = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TurnEventBus _bus = new();
    private readonly GatewayTestDevice _device = new();
    private TurnTokenService _tokens = null!;
    private WebApplication _upstream = null!;
    private WebApplication _gateway = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var up = WebApplication.CreateBuilder();
        up.WebHost.UseTestServer();
        _upstream = up.Build();
        // Чат «stream» — бэкенд отвечает SSE в два приёма и держит второе событие до сигнала теста
        _upstream.Map("/mcp/tasks/stream", async ctx =>
        {
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync("data: первое\n\n");
            await ctx.Response.Body.FlushAsync();
            await _releaseStream.Task;
            await ctx.Response.WriteAsync("data: второе\n\n");
        });
        // Чат «creds» — бэкенд пытается выставить куки и затеять аутентификацию
        _upstream.Map("/mcp/tasks/creds", async ctx =>
        {
            foreach (var name in LlmGatewayEndpointTests.CredentialResponseHeaders)
                ctx.Response.Headers[name] = "backend-value";
            ctx.Response.Headers["Mcp-Session-Id"] = "s-1";
            await ctx.Response.WriteAsync("{}");
        });
        _upstream.Map("/{**path}", async ctx =>
        {
            var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            _seen.Enqueue(new Seen(ctx.Request.Method, ctx.Request.Path.Value!,
                Header(ctx, "Authorization"), Header(ctx, McpEndpoints.CallerSessionHeader),
                Header(ctx, TurnTokenEndpointFilter.HeaderName), Header(ctx, "Cookie"), body,
                Header(ctx, TurnTokenEndpointFilter.DeviceFingerprintHeader)));
            if (ctx.Request.Method == "GET")
            {
                ctx.Response.StatusCode = 405;
                return;
            }
            ctx.Response.ContentType = "application/json";
            ctx.Response.Headers["Mcp-Session-Id"] = "s-1";
            await ctx.Response.WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}");
        });
        await _upstream.StartAsync();

        var gw = WebApplication.CreateBuilder();
        gw.WebHost.UseTestServer();
        _tokens = new TurnTokenService(_bus);
        gw.Services.AddSingleton(_tokens);
        gw.Services.AddSingleton<IMcpBackendAccess, FakeBackend>();
        gw.Services.AddHttpClient(McpGatewayEndpoints.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => _upstream.GetTestServer().CreateHandler());
        _device.AddTo(gw.Services);
        _gateway = gw.Build();
        _gateway.MapMcpGateway();
        await _gateway.StartAsync();
        _client = _gateway.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _releaseStream.TrySetResult();
        await _gateway.DisposeAsync();
        await _upstream.DisposeAsync();
        _device.Dispose();
    }

    private static string? Header(HttpContext ctx, string name) =>
        ctx.Request.Headers.TryGetValue(name, out var v) ? v.ToString() : null;

    private HttpRequestMessage Req(HttpMethod method, string path, string? token, string? body = null, bool device = true)
    {
        var req = new HttpRequestMessage(method, path);
        if (device) _device.Sign(req);
        if (token is not null) req.Headers.Add(TurnTokenEndpointFilter.HeaderName, token);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return req;
    }

    private const string InitBody = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}";

    // --- Негативные проверки спайка (spike-2026-09 §2, последний абзац) ---

    [Fact]
    public async Task БезТокенаХода_401_ИБэкендНеВызван()
    {
        var t = _tokens.Issue("owner-1", "chat-1", _device.Id);

        var resp = await _client.SendAsync(Req(HttpMethod.Post, $"/gw/t/{t.Grant.TurnId}/mcp/tasks/chat-1", null, InitBody));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _seen.Should().BeEmpty();
    }

    // ADR-016 §2: токен хода принимается только вместе с учёткой устройства, к которому он привязан
    [Fact]
    public async Task ВалидныйТокен_БезУчёткиЧужоеОтозванноеУстройство_401_ИБэкендНеВызван()
    {
        var t = _tokens.Issue("owner-1", "chat-1", _device.Id);
        var path = $"/gw/t/{t.Grant.TurnId}/mcp/tasks/chat-1";

        (await _client.SendAsync(Req(HttpMethod.Post, path, t.Token, InitBody, device: false)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "без учётки устройства");

        var otherFp = new string('e', 64);
        var (_, otherToken) = _device.Register("owner-1", "чужое", otherFp);
        var foreign = GatewayTestDevice.Sign(Req(HttpMethod.Post, path, t.Token, InitBody, device: false), otherToken, otherFp);
        (await _client.SendAsync(foreign)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "токен привязан к другому устройству");

        var unbound = _tokens.Issue("owner-1", "chat-1");
        (await _client.SendAsync(Req(HttpMethod.Post, $"/gw/t/{unbound.Grant.TurnId}/mcp/tasks/chat-1", unbound.Token, InitBody)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "токен без привязки к устройству");

        _device.Registry.Revoke("owner-1", _device.Id).Should().BeTrue();
        (await _client.SendAsync(Req(HttpMethod.Post, path, t.Token, InitBody)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "устройство отозвано");
        _seen.Should().BeEmpty();
    }

    [Fact]
    public async Task ВыдуманныйТокен_401_ИБэкендНеВызван()
    {
        var t = _tokens.Issue("owner-1", "chat-1", _device.Id);

        var resp = await _client.SendAsync(Req(HttpMethod.Post, $"/gw/t/{t.Grant.TurnId}/mcp/tasks/chat-1", "выдуманный", InitBody));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _seen.Should().BeEmpty();
    }

    [Fact]
    public async Task НеизвестныйХод_401()
    {
        var t = _tokens.Issue("owner-1", "chat-1", _device.Id);

        var resp = await _client.SendAsync(Req(HttpMethod.Post, "/gw/t/нет-такого/mcp/tasks/chat-1", t.Token, InitBody));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _seen.Should().BeEmpty();
    }

    [Fact]
    public async Task ОтозванныйТокен_401()
    {
        var t = _tokens.Issue("owner-1", "chat-1", _device.Id);
        await _bus.PublishAsync(new TurnCompleted(new TurnContext("chat-1", "owner-1", 1, 0), "success"));

        var resp = await _client.SendAsync(Req(HttpMethod.Post, $"/gw/t/{t.Grant.TurnId}/mcp/tasks/chat-1", t.Token, InitBody));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _seen.Should().BeEmpty();
    }

    [Theory]
    [InlineData("tasks")]
    [InlineData("notes")]
    [InlineData("personas")]
    [InlineData("notifications")]
    [InlineData("watch")]
    [InlineData("websearch")]
    [InlineData("codegraph")]
    [InlineData("dify")]
    [InlineData("higgsfield")]
    [InlineData("wsp")]
    public async Task ВалидныйТокенИЧужойХвост_403_ИБэкендНеВызван(string server)
    {
        var t = _tokens.Issue("owner-1", "chat-1", _device.Id);
        _tokens.Issue("owner-1", "chat-2", _device.Id);

        foreach (var tail in new[] { "/chat-2", "", "/chat-1/chat-2", "/chat-1%2F..%2Fchat-2" })
        {
            var resp = await _client.SendAsync(Req(HttpMethod.Post, $"/gw/t/{t.Grant.TurnId}/mcp/{server}{tail}", t.Token, InitBody));
            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"хвост «{tail}» у {server}");
        }
        _seen.Should().BeEmpty();
    }

    [Fact]
    public async Task НезнакомыйСервер_404_ИБэкендНеВызван()
    {
        var t = _tokens.Issue("owner-1", "chat-1", _device.Id);

        var resp = await _client.SendAsync(Req(HttpMethod.Post, $"/gw/t/{t.Grant.TurnId}/mcp/evil/chat-1", t.Token, InitBody));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _seen.Should().BeEmpty();
    }

    // --- Проксирование ---

    [Fact]
    public async Task ДоБэкендаЕдутJwtВладельцаИЧатТокена_КлиентскиеВыброшены()
    {
        var t = _tokens.Issue("owner-1", "chat-1", _device.Id);
        var req = Req(HttpMethod.Post, $"/gw/t/{t.Grant.TurnId}/mcp/tasks/chat-1", t.Token, InitBody);
        req.Headers.Add(McpEndpoints.CallerSessionHeader, "chat-2");
        req.Headers.Add("Cookie", "auth=секрет");
        req.Headers.Add("Mcp-Session-Id", "s-1");

        var resp = await _client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Be("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}");
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        resp.Headers.GetValues("Mcp-Session-Id").Should().Equal("s-1");

        _seen.Should().ContainSingle();
        var seen = _seen.Single();
        seen.Method.Should().Be("POST");
        seen.Path.Should().Be("/mcp/tasks/chat-1");
        seen.Authorization.Should().Be("Bearer jwt-of-owner-1");
        seen.Caller.Should().Be("chat-1");
        seen.TurnToken.Should().BeNull("токен хода дальше шлюза не едет");
        seen.Cookie.Should().BeNull();
        seen.Fingerprint.Should().BeNull("учётка устройства дальше шлюза не едет");
        seen.Body.Should().Be(InitBody);
    }

    [Fact]
    public async Task ВиджетыБезХвоста_Проходят_СХвостом_403()
    {
        var t = _tokens.Issue("owner-1", "chat-1", _device.Id);

        (await _client.SendAsync(Req(HttpMethod.Post, $"/gw/t/{t.Grant.TurnId}/mcp/widgets", t.Token, InitBody)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.SendAsync(Req(HttpMethod.Post, $"/gw/t/{t.Grant.TurnId}/mcp/widgets/chat-1", t.Token, InitBody)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        _seen.Select(s => s.Path).Should().Equal("/mcp/widgets");
    }

    [Fact]
    public async Task ПамятьСХвостомПерсонаПроект_Проходит_КриваяФорма_403()
    {
        var t = _tokens.Issue("owner-1", "chat-1", _device.Id);

        (await _client.SendAsync(Req(HttpMethod.Post, $"/gw/t/{t.Grant.TurnId}/mcp/memory/p-1/-", t.Token, InitBody)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var tail in new[] { "", "/p-1", "/p-1/-/x", "/p-1/.." })
            (await _client.SendAsync(Req(HttpMethod.Post, $"/gw/t/{t.Grant.TurnId}/mcp/memory{tail}", t.Token, InitBody)))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden, $"хвост памяти «{tail}»");

        _seen.Select(s => s.Path).Should().Equal("/mcp/memory/p-1/-");
    }

    [Fact]
    public async Task GetИПрочиеМетоды_ПроходятНасквозь()
    {
        var t = _tokens.Issue("owner-1", "chat-1", _device.Id);

        var get = await _client.SendAsync(Req(HttpMethod.Get, $"/gw/t/{t.Grant.TurnId}/mcp/tasks/chat-1", t.Token));
        var del = await _client.SendAsync(Req(HttpMethod.Delete, $"/gw/t/{t.Grant.TurnId}/mcp/tasks/chat-1", t.Token));

        get.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, "405 бэкенда доезжает до клиента как есть");
        del.StatusCode.Should().Be(HttpStatusCode.OK);
        _seen.Select(s => s.Method).Should().Equal("GET", "DELETE");
        _seen.Should().OnlyContain(s => s.Authorization == "Bearer jwt-of-owner-1" && s.Caller == "chat-1");
    }

    [Fact]
    public async Task ДругойВладелец_ПолучаетСвойJwt()
    {
        var t = _tokens.Issue("owner-2", "chat-9", _device.Id);

        await _client.SendAsync(Req(HttpMethod.Post, $"/gw/t/{t.Grant.TurnId}/mcp/notes/chat-9", t.Token, InitBody));

        _seen.Single().Authorization.Should().Be("Bearer jwt-of-owner-2");
    }

    [Fact]
    public async Task УчётныеЗаголовкиОтветаБэкенда_ДоКлиентаНеДоходят()
    {
        var t = _tokens.Issue("owner-1", "creds", _device.Id);

        var resp = await _client.SendAsync(Req(HttpMethod.Post, $"/gw/t/{t.Grant.TurnId}/mcp/tasks/creds", t.Token, InitBody));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Headers.GetValues("Mcp-Session-Id").Should().Equal("s-1");
        foreach (var name in LlmGatewayEndpointTests.CredentialResponseHeaders)
            resp.Headers.Contains(name).Should().BeFalse($"{name} не должен дойти до клиента");
    }

    [Fact]
    public async Task БэкендНеОтветил_502_ОшибкаВЛогеОднойСтрокойСПотолком()
    {
        var sink = new CollectingLogSink();
        var gw = WebApplication.CreateBuilder();
        gw.WebHost.UseTestServer();
        gw.Services.AddSingleton(_tokens);
        gw.Services.AddSingleton<IMcpBackendAccess, FakeBackend>();
        gw.Services.AddSingleton<ILoggerProvider>(sink);
        _device.AddTo(gw.Services);
        var message = "первая строка\r\nподдельная запись" + new string('x', 5000);
        gw.Services.AddHttpClient(McpGatewayEndpoints.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new ThrowingHandler(message));
        await using var app = gw.Build();
        app.MapMcpGateway();
        await app.StartAsync();
        var t = _tokens.Issue("owner-1", "chat-1", _device.Id);

        var resp = await app.GetTestClient().SendAsync(
            Req(HttpMethod.Post, $"/gw/t/{t.Grant.TurnId}/mcp/tasks/chat-1", t.Token, InitBody));

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var line = sink.Entries.Should().ContainSingle(e => e.Contains("бэкенд не ответил")).Subject;
        line.Should().NotContain("\r").And.NotContain("\n");
        line.Should().Contain("первая строка  поддельная запись");
        line.Length.Should().BeLessThan(1000, "текст ошибки обрезан");
    }

    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException(message);
    }

    private sealed class CollectingLogSink : ILoggerProvider
    {
        public ConcurrentQueue<string> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(CollectingLogSink sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => sink.Entries.Enqueue(formatter(state, exception));
        }
    }

    [Fact]
    public async Task Стрим_ДоходитПоМереПрихода_БезБуфера()
    {
        var t = _tokens.Issue("owner-1", "stream", _device.Id);
        var req = Req(HttpMethod.Post, $"/gw/t/{t.Grant.TurnId}/mcp/tasks/stream", t.Token, InitBody);

        var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
            .WaitAsync(TimeSpan.FromSeconds(10));
        var stream = await resp.Content.ReadAsStreamAsync();
        var buf = new byte[256];
        var read = await stream.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Encoding.UTF8.GetString(buf, 0, read).Should().Be("data: первое\n\n",
            "первое событие обязано дойти, пока бэкенд ещё не закончил ответ");
        _releaseStream.SetResult();
        (await new StreamReader(stream).ReadToEndAsync()).Should().Be("data: второе\n\n");
    }
}
