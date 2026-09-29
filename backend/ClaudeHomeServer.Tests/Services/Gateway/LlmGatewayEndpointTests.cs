using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Desktop;
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
    private readonly GatewayTestDevice _device = new("owner");
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
        _device.AddTo(builder.Services);
        _app = builder.Build();
        _app.MapLlmGateway();
        _app.StartAsync().GetAwaiter().GetResult();
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _kit.Dispose();
        _device.Dispose();
    }

    private IssuedTurnToken Start(string model)
    {
        var start = _kit.Selector.StartTurn(_tokens, "owner", "chat", _device.Id, model);
        start.Token.Should().NotBeNull(start.FailureText);
        return start.Token!;
    }

    private HttpRequestMessage Req(HttpMethod method, IssuedTurnToken t, string path, string? json = null)
    {
        var r = _device.Sign(new HttpRequestMessage(method, $"/gw/t/{t.Grant.TurnId}/llm/{path}"));
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
        (await _client.SendAsync(_device.Sign(new HttpRequestMessage(HttpMethod.Get, $"/gw/t/{t.Grant.TurnId}/llm/v1/models"))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var r = _device.Sign(new HttpRequestMessage(HttpMethod.Get, $"/gw/t/{t.Grant.TurnId}/llm/v1/models"));
        r.Headers.TryAddWithoutValidation(TurnTokenEndpointFilter.HeaderName, "выдуманный");
        (await _client.SendAsync(r)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _upstream.Calls.Should().BeEmpty();
    }

    // ADR-016 §2: токен хода принимается только вместе с учёткой устройства, к которому он привязан
    [Fact]
    public async Task ВалидныйТокен_БезУчёткиУстройства_ЧужоеИОтозванноеУстройство_401()
    {
        var t = Start("sonnet");
        (await _client.SendAsync(Req(HttpMethod.Get, t, "api/hello"))).StatusCode.Should().Be(HttpStatusCode.OK);

        var bare = new HttpRequestMessage(HttpMethod.Get, $"/gw/t/{t.Grant.TurnId}/llm/api/hello");
        bare.Headers.TryAddWithoutValidation(TurnTokenEndpointFilter.HeaderName, t.Token);
        (await _client.SendAsync(bare)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "без учётки устройства");

        var noPrint = new HttpRequestMessage(HttpMethod.Get, $"/gw/t/{t.Grant.TurnId}/llm/api/hello");
        noPrint.Headers.TryAddWithoutValidation("Authorization", DesktopDeviceAuthHandler.TokenPrefix + _device.Token);
        noPrint.Headers.TryAddWithoutValidation(TurnTokenEndpointFilter.HeaderName, t.Token);
        (await _client.SendAsync(noPrint)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "без отпечатка машины");

        var otherFp = new string('f', 64);
        var (_, otherToken) = _device.Register("owner", "чужое", otherFp);
        var foreign = GatewayTestDevice.Sign(
            new HttpRequestMessage(HttpMethod.Get, $"/gw/t/{t.Grant.TurnId}/llm/api/hello"), otherToken, otherFp);
        foreign.Headers.TryAddWithoutValidation(TurnTokenEndpointFilter.HeaderName, t.Token);
        (await _client.SendAsync(foreign)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "токен привязан к другому устройству");

        _device.Registry.Revoke("owner", _device.Id).Should().BeTrue();
        (await _client.SendAsync(Req(HttpMethod.Get, t, "api/hello"))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "устройство отозвано");
        _upstream.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ТокенБезПривязкиКУстройству_СУчёткойУстройства_401()
    {
        var start = _kit.Selector.StartTurn(_tokens, "owner", "chat", null, "sonnet");

        (await _client.SendAsync(Req(HttpMethod.Get, start.Token!, "api/hello"))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "устройство предъявляет только токен, привязанный к нему");
    }

    // Серверный ход провайдера (ADR-016 §2): токен без устройства, учётки нет вовсе
    private HttpRequestMessage ServerReq(IssuedTurnToken t, string path, string? json = null)
    {
        var r = new HttpRequestMessage(json is null ? HttpMethod.Get : HttpMethod.Post, $"/gw/t/{t.Grant.TurnId}/llm/{path}");
        r.Headers.TryAddWithoutValidation(TurnTokenEndpointFilter.HeaderName, t.Token);
        if (json is not null) r.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return r;
    }

    [Fact]
    public async Task СерверныйХодПровайдера_БезУчёткиУстройства_Проходит_КлючСервера()
    {
        var t = _kit.Selector.StartTurn(_tokens, "owner", "chat", null, "mmx-m3", TurnTokenLifetime.Process).Token!;

        var resp = await _client.SendAsync(ServerReq(t, "v1/messages", """{"model":"mmx-m3","max_tokens":1}"""));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var (up, _) = _upstream.Calls.Single();
        up.RequestUri!.ToString().Should().Be("https://mmx.test/anthropic/v1/messages");
        up.Headers.GetValues("x-api-key").Should().Equal("mmx-key");
    }

    [Fact]
    public async Task СерверныйХод_ПодпискаИлиОтозванныйИлиВыдуманный_401()
    {
        var subscription = _kit.Selector.StartTurn(_tokens, "owner", "chat", null, "sonnet").Token!;
        (await _client.SendAsync(ServerReq(subscription, "api/hello"))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "подписки серверному ходу через шлюз не положены");

        var provider = _kit.Selector.StartTurn(_tokens, "owner", "chat", null, "mmx-m3", TurnTokenLifetime.Process).Token!;
        var forged = ServerReq(provider, "api/hello");
        forged.Headers.Remove(TurnTokenEndpointFilter.HeaderName);
        forged.Headers.TryAddWithoutValidation(TurnTokenEndpointFilter.HeaderName, "выдуманный");
        (await _client.SendAsync(forged)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        _tokens.RevokeTurn(provider.Grant.TurnId).Should().BeTrue();
        (await _client.SendAsync(ServerReq(provider, "api/hello"))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "токен отозван по выходу процесса");
        _upstream.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ТокенУстройства_БезУчётки_ПоПрежнему401()
    {
        var t = Start("mmx-m3");

        (await _client.SendAsync(ServerReq(t, "api/hello"))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "токен устройства без учётки устройства не проходит и с правом серверного хода");
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
        up.Headers.Contains(TurnTokenEndpointFilter.DeviceFingerprintHeader).Should().BeFalse("учётка устройства upstream не нужна");
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

    // Куки и вызовы аутентификации upstream до устройства не доезжают: у него нет ничего,
    // кроме токена хода, а Set-Cookie от чужого хоста осел бы в его клиенте.
    internal static readonly string[] CredentialResponseHeaders =
        ["Set-Cookie", "Set-Cookie2", "WWW-Authenticate", "Proxy-Authenticate", "Authentication-Info"];

    [Fact]
    public async Task УчётныеЗаголовкиОтветаUpstream_ДоКлиентаНеДоходят()
    {
        var t = Start("sonnet");
        _upstream.Respond = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
            foreach (var name in CredentialResponseHeaders)
                r.Headers.TryAddWithoutValidation(name, "upstream-value").Should().BeTrue(name);
            r.Headers.TryAddWithoutValidation("X-Upstream", "ok");
            return r;
        };

        var resp = await _client.SendAsync(Req(HttpMethod.Post, t, "v1/messages", "{}"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        resp.Headers.GetValues("X-Upstream").Should().Equal(["ok"], "обычные заголовки едут как есть");
        foreach (var name in CredentialResponseHeaders)
            resp.Headers.Contains(name).Should().BeFalse($"{name} не должен дойти до клиента");
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

    // Нормализатор tool_use.input (флаг провайдера NormalizeToolInputArrays, дефект MiniMax-M3)

    private const string ToolsRequest = """
        {"model":"mmx-m3","stream":true,"tools":[{"name":"set_urls","input_schema":
          {"type":"object","properties":{"image_urls":{"type":"array","items":{"type":"string"}}}}}]}
        """;

    private static string Sse(string type, string json) => $"event: {type}\ndata: {json}\n\n";

    private static readonly string TextStart = Sse("content_block_start",
        """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""");
    private static readonly string TextDelta = Sse("content_block_delta",
        """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Привет"}}""");
    private static readonly string ToolStart = Sse("content_block_start",
        """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"t1","name":"set_urls","input":{}}}""");
    private static string ToolDelta(string partial) => Sse("content_block_delta",
        JsonSerializer.Serialize(new { type = "content_block_delta", index = 1, delta = new { type = "input_json_delta", partial_json = partial } }));
    private static readonly string ToolStop = Sse("content_block_stop", """{"type":"content_block_stop","index":1}""");
    private static readonly string MessageStop = Sse("message_stop", """{"type":"message_stop"}""");

    private (Pipe Pipe, Func<Task<HttpResponseMessage>> Send) SseUpstream(IssuedTurnToken t)
    {
        var pipe = new Pipe();
        _upstream.Respond = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(pipe.Reader.AsStream()) };
            r.Content.Headers.ContentType = new("text/event-stream");
            return r;
        };
        return (pipe, () => _client.SendAsync(Req(HttpMethod.Post, t, "v1/messages", ToolsRequest),
            HttpCompletionOption.ResponseHeadersRead));
    }

    // Склейка input_json_delta блока index из потока SSE — так его собирает CLI
    private static string ToolInput(string stream, int index) => string.Concat(stream
        .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
        .Select(e => e.Split('\n').FirstOrDefault(l => l.StartsWith("data: ", StringComparison.Ordinal)))
        .Where(d => d is not null)
        .Select(d => JsonNode.Parse(d!["data: ".Length..])!)
        .Where(n => (string?)n["type"] == "content_block_delta" && (int?)n["index"] == index
            && (string?)n["delta"]!["type"] == "input_json_delta")
        .Select(n => (string?)n["delta"]!["partial_json"]));

    [Fact]
    public async Task Sse_ОбёрткаItemРазрезанаМеждуКусками_НаВыходеМассив_ТекстБезБуферизации()
    {
        var t = Start("mmx-m3");
        var (pipe, send) = SseUpstream(t);
        var resp = await send();
        await using var stream = await resp.Content.ReadAsStreamAsync();

        // Текст доходит до клиента, пока upstream ещё не прислал ни одного куска tool_use
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(TextStart + TextDelta));
        var head = TextStart + TextDelta;
        (await ReadAtLeastAsync(stream, Encoding.UTF8.GetByteCount(head)).WaitAsync(TimeSpan.FromSeconds(10)))
            .Should().Be(head);

        // Обёртка разрезана между дельтами, а событие — между кусками TCP
        var tail = ToolStart + ToolDelta("""{"image_urls":{"item":[""") + ToolDelta(""" "https://a.png","https://b.png"]}}""") + ToolStop + MessageStop;
        var bytes = Encoding.UTF8.GetBytes(tail);
        await pipe.Writer.WriteAsync(bytes.AsMemory(0, bytes.Length / 2));
        await pipe.Writer.WriteAsync(bytes.AsMemory(bytes.Length / 2));
        await pipe.Writer.CompleteAsync();
        var rest = await new StreamReader(stream).ReadToEndAsync();

        JsonNode.Parse(ToolInput(rest, 1))!.ToJsonString().Should().Be("""{"image_urls":["https://a.png","https://b.png"]}""");
        rest.Should().Contain(ToolStart).And.EndWith(ToolStop + MessageStop, "остальные события идут как пришли");
    }

    [Fact]
    public async Task Sse_БезОбёрток_ДельтыИдутИсходнымиБайтами()
    {
        var t = Start("mmx-m3");
        var (pipe, send) = SseUpstream(t);
        var resp = await send();
        var body = ToolStart + ToolDelta("""{"image_urls":[""") + ToolDelta(""" "a"]}""") + ToolStop + MessageStop;

        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(body));
        await pipe.Writer.CompleteAsync();

        (await resp.Content.ReadAsStringAsync()).Should().Be(body);
    }

    [Fact]
    public async Task Sse_ФлагВыключен_БайтыИдентичныВходу()
    {
        var t = Start("deepseek-v4-pro");
        var (pipe, send) = SseUpstream(t);
        var resp = await send();
        var body = TextStart + TextDelta + ToolStart + ToolDelta("""{"image_urls":{"item":["a"]}}""") + ToolStop + MessageStop;

        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(body));
        await pipe.Writer.CompleteAsync();

        (await resp.Content.ReadAsStringAsync()).Should().Be(body, "без флага провайдера шлюз ответ не трогает");
    }

    [Fact]
    public async Task Json_ОбёрткаРазвёрнута_ContentLengthПересчитан_ДампЗапросаЗаписан()
    {
        var dumpDir = Path.Combine(_kit.TempDir, "dumps");
        // Посторонние json в каталоге дампа чистка не трогает — только файлы с именем дампа
        Directory.CreateDirectory(dumpDir);
        File.WriteAllText(Path.Combine(dumpDir, "appsettings.Local.json"), "{}");
        File.WriteAllText(Path.Combine(dumpDir, "x.json"), "{}");
        _kit.Options.CurrentValue = new LlmGatewayOptions
        {
            Enabled = true, AllowSubscriptions = true, AnthropicBaseUrl = "https://anthropic.test",
            DumpNormalizedRequestsDir = dumpDir,
            DumpKeep = 1,
        };
        var t = Start("mmx-m3");
        _upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"m1","content":[{"type":"tool_use","id":"t1","name":"set_urls","input":{"image_urls":{"item":"https://a.png"}}}]}""",
                Encoding.UTF8, "application/json"),
        };
        var req = Req(HttpMethod.Post, t, "v1/messages", ToolsRequest.Replace("\"stream\":true", "\"stream\":false"));
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");

        var resp = await _client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();

        JsonNode.Parse(text)!["content"]![0]!["input"]!.ToJsonString().Should().Be("""{"image_urls":["https://a.png"]}""");
        resp.Content.Headers.ContentLength.Should().Be(Encoding.UTF8.GetByteCount(text));
        _upstream.Calls.Single().Request.Headers.Contains("Accept-Encoding").Should().BeFalse("ответ под нормализатор не сжимается");
        var dump = Directory.GetFiles(dumpDir, LlmGatewayEndpoints.DumpFilePrefix + "*.json").Should().ContainSingle().Subject;
        File.ReadAllText(dump).Should().Contain("\"set_urls\"").And.NotContain("mmx-key", "ключей в дампе нет");
        File.Exists(Path.Combine(dumpDir, "appsettings.Local.json")).Should().BeTrue("чистка удаляет только свои дампы");
        File.Exists(Path.Combine(dumpDir, "x.json")).Should().BeTrue();
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
