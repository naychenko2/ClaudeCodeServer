using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Services.Gateway;

/// <summary>
/// Шлюз LLM целиком (ADR-016, задача 1.2) на TestServer с фейковым upstream: hello, любые
/// методы, anthropic-beta подписки, SSE без буферизации, учёт лимитов через рекордер, тумблеры.
/// </summary>
public sealed class LlmGatewayEndpointTests : IAsyncDisposable
{
    // Фейковый upstream: запоминает запросы, ответ строит тест.
    private sealed class FakeUpstream : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Request, string Body)> Calls = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Calls) Calls.Add((request, body));
            return Respond(request);
        }
    }

    private readonly GatewayTestKit _kit;
    private readonly FakeUpstream _upstream = new();
    private readonly TurnTokenService _tokens = new(new TurnEventBus());
    private readonly WebApplication _app;
    private readonly HttpClient _client;

    public LlmGatewayEndpointTests()
    {
        _kit = new GatewayTestKit(("st1", "tok-st1", null), ("st2", "tok-st2", null), ("api", null, "sk-ant-api"));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddSingleton<IOptionsMonitor<LlmGatewayOptions>>(_kit.Options);
        builder.Services.AddSingleton(_tokens);
        builder.Services.AddSingleton(_kit.Selector);
        builder.Services.AddSingleton(new SubscriptionLimitRecorder(_kit.Usage, _kit.Pool));
        builder.Services.AddHttpClient(LlmGatewayEndpoints.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => _upstream);
        _app = builder.Build();
        _app.MapLlmGateway();
        _app.StartAsync().GetAwaiter().GetResult();
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _kit.Dispose();
    }

    private IssuedTurnToken Start(string model)
    {
        var start = _kit.Selector.StartTurn(_tokens, "owner", "chat", null, model);
        start.Token.Should().NotBeNull(start.FailureText);
        return start.Token!;
    }

    private static HttpRequestMessage Req(HttpMethod method, IssuedTurnToken t, string path, string? json = null)
    {
        var r = new HttpRequestMessage(method, $"/gw/t/{t.Grant.TurnId}/llm/{path}");
        r.Headers.TryAddWithoutValidation(TurnTokenEndpointFilter.HeaderName, t.Token);
        if (json is not null) r.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return r;
    }

    [Fact]
    public async Task Hello_ШлюзОтвечаетСам()
    {
        var t = Start("sonnet");

        var resp = await _client.SendAsync(Req(HttpMethod.Get, t, "api/hello"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _upstream.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task БезТокена_401_ВыдуманныйТокен_401()
    {
        var t = Start("sonnet");
        (await _client.GetAsync($"/gw/t/{t.Grant.TurnId}/llm/v1/models")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var r = new HttpRequestMessage(HttpMethod.Get, $"/gw/t/{t.Grant.TurnId}/llm/v1/models");
        r.Headers.TryAddWithoutValidation(TurnTokenEndpointFilter.HeaderName, "выдуманный");
        (await _client.SendAsync(r)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _upstream.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ШлюзВыключен_404()
    {
        var t = Start("sonnet");
        _kit.Options.CurrentValue = new LlmGatewayOptions { Enabled = false, AllowSubscriptions = true };

        (await _client.SendAsync(Req(HttpMethod.Get, t, "api/hello"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task ЛюбойМетод_Проходит_ПодпискаСOAuthBeta_АвторизацияКлиентаВыброшена(string method)
    {
        var t = Start("claude-opus-5-5");
        var req = Req(new HttpMethod(method), t, "v1/messages?beta=true",
            method is "GET" or "DELETE" ? null : """{"model":"claude-opus-5-5","max_tokens":1}""");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer device-placeholder");
        req.Headers.TryAddWithoutValidation("x-api-key", "device-placeholder");
        req.Headers.TryAddWithoutValidation("anthropic-beta", "fine-grained-tool-streaming-2025-05-14");

        var resp = await _client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var (up, _) = _upstream.Calls.Single();
        up.Method.Method.Should().Be(method);
        up.RequestUri!.ToString().Should().Be("https://anthropic.test/v1/messages?beta=true");
        var key = t.Grant.Route!.SubscriptionKey!;
        up.Headers.GetValues("Authorization").Should().Equal($"Bearer tok-{key}");
        up.Headers.Contains("x-api-key").Should().BeFalse();
        up.Headers.Contains(TurnTokenEndpointFilter.HeaderName).Should().BeFalse("токен хода upstream не нужен");
        string.Join(",", up.Headers.GetValues("anthropic-beta")).Split(',')
            .Should().Contain([LlmGatewayEndpoints.OAuthBeta, "fine-grained-tool-streaming-2025-05-14"]);
    }

    [Fact]
    public async Task СтороннийUpstream_КлючСервера_ФоновыйHaikuПереписан_БезOAuthBeta()
    {
        var t = Start("deepseek-v4-pro");

        await _client.SendAsync(Req(HttpMethod.Post, t, "v1/messages",
            """{"model":"claude-haiku-4-5-20251001","max_tokens":1,"stream":true}"""));

        var (up, body) = _upstream.Calls.Single();
        up.RequestUri!.ToString().Should().Be("https://deepseek.test/anthropic/v1/messages");
        up.Headers.GetValues("x-api-key").Should().Equal("ds-key");
        up.Headers.Contains("anthropic-beta").Should().BeFalse();
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("model").GetString().Should().Be("deepseek-v4-flash");
        json.GetProperty("stream").GetBoolean().Should().BeTrue("остальное тело не тронуто");
    }

    [Fact]
    public async Task Sse_БезБуферизации_КусокДоходитДоКлиентаРаньшеОтправкиСледующего()
    {
        var t = Start("sonnet");
        var pipe = new Pipe();
        _upstream.Respond = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(pipe.Reader.AsStream()) };
            r.Content.Headers.ContentType = new("text/event-stream");
            return r;
        };
        var clock = Stopwatch.StartNew();

        var resp = await _client.SendAsync(Req(HttpMethod.Post, t, "v1/messages", """{"model":"sonnet"}"""),
            HttpCompletionOption.ResponseHeadersRead);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        await using var stream = await resp.Content.ReadAsStreamAsync();

        const string chunk1 = "event: content_block_delta\ndata: {\"n\":1}\n\n";
        const string chunk2 = "event: content_block_delta\ndata: {\"n\":2}\n\n";
        var sent1 = clock.Elapsed;
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(chunk1));
        // Второй кусок upstream ещё не отправил: если шлюз копит ответ, первый не дойдёт никогда.
        var got1 = await ReadAtLeastAsync(stream, chunk1.Length).WaitAsync(TimeSpan.FromSeconds(10));
        var recv1 = clock.Elapsed;
        got1.Should().Be(chunk1);

        var sent2 = clock.Elapsed;
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(chunk2));
        await pipe.Writer.CompleteAsync();
        (await new StreamReader(stream).ReadToEndAsync()).Should().Be(chunk2);

        sent1.Should().BeLessThan(recv1);
        recv1.Should().BeLessThan(sent2, "первая дельта у клиента раньше, чем upstream прислал вторую");
    }

    [Fact]
    public async Task ЗаголовкиЛимитов_ПишутсяВПулЧерезРекордер_СледующийЗапросУходитНаДругойSetupToken()
    {
        var t = Start("sonnet");
        var first = t.Grant.Route!.SubscriptionKey!;
        var second = first == "st1" ? "st2" : "st1";
        _upstream.Respond = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
            r.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-status", "rejected");
            r.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-status", "rejected");
            r.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "1.0");
            r.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-reset",
                DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds().ToString());
            return r;
        };

        (await _client.SendAsync(Req(HttpMethod.Post, t, "v1/messages", "{}"))).StatusCode
            .Should().Be(HttpStatusCode.TooManyRequests);

        _kit.Pool.IsExhausted(first).Should().BeTrue();
        _kit.Usage.GetAllBySubscription()[first].Should()
            .ContainSingle(s => s.Source == "gateway" && s.LimitType == "five_hour" && s.Status == "rejected");

        _upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        await _client.SendAsync(Req(HttpMethod.Post, t, "v1/messages", "{}"));
        _upstream.Calls[^1].Request.Headers.GetValues("Authorization").Should().Equal($"Bearer tok-{second}");
        _kit.Pool.PickCalls.Should().Be(0);
    }

    [Fact]
    public async Task ПодпискиВыключеныПосредиХода_ЯвныйОтказ_UpstreamНеВызван_ПулНеСпрошен()
    {
        var t = Start("sonnet");
        _kit.Pool.PickSetupTokenCalls = 0;
        _kit.Options.CurrentValue = new LlmGatewayOptions { Enabled = true, AllowSubscriptions = false };

        var resp = await _client.SendAsync(Req(HttpMethod.Post, t, "v1/messages", "{}"));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("выключены");
        _upstream.Calls.Should().BeEmpty();
        _kit.Pool.PickCalls.Should().Be(0);
        _kit.Pool.PickSetupTokenCalls.Should().Be(0);
    }

    private static async Task<string> ReadAtLeastAsync(Stream stream, int bytes)
    {
        var buffer = new byte[bytes];
        var total = 0;
        while (total < bytes)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total));
            if (n == 0) break;
            total += n;
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }
}
