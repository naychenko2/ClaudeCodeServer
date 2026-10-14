using System.IO.Pipelines;
using System.Net;
using System.Text;
using ClaudeHomeServer.Services.Devices;
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
/// Шлюз LLM с наблюдателем метрик локальной модели (флаг local-model-stats, шаг 16):
/// побайтность (вход апстрима = выход клиенту), устойчивость к исключению наблюдателя,
/// условия создания наблюдателя (локальный маршрут + SSE + метрики включены) и
/// неизменность поведения с нормализатором.
/// </summary>
public sealed class LlmGatewayObserverTests : IAsyncDisposable
{
    // Приёмник метрик: запоминает моменты, чтобы тест проверил, когда наблюдатель создан.
    private sealed class SpySink : ILocalModelRequestSink
    {
        public int Started;
        public int FirstToken;
        public int Completed;
        public RequestUsage? LastUsage;
        public int TokenCountCalls;

        public void OnRequestStarted(string requestId, string turnId) => Interlocked.Increment(ref Started);
        public void OnFirstToken(string requestId, string turnId) => Interlocked.Increment(ref FirstToken);
        public void OnRequestCompleted(string requestId, string turnId, RequestUsage usage)
        {
            Interlocked.Increment(ref Completed);
            LastUsage = usage;
        }
        public void OnTokenCount(string requestId, string turnId, int cumulativeTokens)
            => Interlocked.Increment(ref TokenCountCalls);
    }

    // Приёмник, бросающий из OnFirstToken: шов для теста «наблюдатель упал» — тот же
    // try/catch в шлюзе, что ловит сбой Feed. Считает вызовы, чтобы проверить, что упавший
    // приёмник отключился (OnRequestCompleted не доставлен), а клиент всё равно получил всё.
    private sealed class ThrowingSink : ILocalModelRequestSink
    {
        public int Started;
        public int FirstToken;
        public int Completed;

        public void OnRequestStarted(string requestId, string turnId) => Interlocked.Increment(ref Started);
        public void OnFirstToken(string requestId, string turnId)
        {
            Interlocked.Increment(ref FirstToken);
            throw new InvalidOperationException("наблюдатель упал");
        }
        public void OnRequestCompleted(string requestId, string turnId, RequestUsage usage) => Interlocked.Increment(ref Completed);
        public void OnTokenCount(string requestId, string turnId, int cumulativeTokens) { /* не падаем */ }
    }

    // Приёмник, бросающий из OnTokenCount (M5): шов для теста «сбой колбэка токенов».
    // Колбэк живёт ВНУТРИ Feed наблюдателя, поэтому его сбой ловит try/catch наблюдателя
    // (SseUsageObserver), а не try/catch шлюза вокруг Feed. Считает вызовы, чтобы проверить,
    // что наблюдатель проглотил все дельты и остался жив (итог доставлен, клиент получил всё).
    private sealed class TokenThrowingSink : ILocalModelRequestSink
    {
        public int Started;
        public int FirstToken;
        public int Completed;
        public int TokenCountCalls;

        public void OnRequestStarted(string requestId, string turnId) => Interlocked.Increment(ref Started);
        public void OnFirstToken(string requestId, string turnId) => Interlocked.Increment(ref FirstToken);
        public void OnRequestCompleted(string requestId, string turnId, RequestUsage usage) => Interlocked.Increment(ref Completed);
        public void OnTokenCount(string requestId, string turnId, int cumulativeTokens)
        {
            Interlocked.Increment(ref TokenCountCalls);
            throw new InvalidOperationException("колбэк токенов упал");
        }
    }

    // Фейковый upstream (как в LlmGatewayEndpointTests): запоминает запросы, ответ строит тест.
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

    // Локальный провайдер: IsLocal=true, метрики включены — наблюдатель создаётся.
    private const string LocalModel = "qwen3.8-27b";

    private readonly GatewayTestKit _kit;
    private readonly FakeUpstream _upstream = new();
    private readonly TurnTokenService _tokens = new(new TurnEventBus());
    private readonly GatewayTestDevice _device = new("owner");
    private readonly SpySink _sink = new();
    private readonly MutableOptionsMonitor<LocalModelStatsOptions> _statsOptions;
    private readonly WebApplication _app;
    private readonly HttpClient _client;

    public LlmGatewayObserverTests()
    {
        _kit = new GatewayTestKit(("st1", "tok-st1", null), ("st2", "tok-st2", null), ("api", null, "sk-ant-api"));
        _statsOptions = new MutableOptionsMonitor<LocalModelStatsOptions>(new LocalModelStatsOptions { Enabled = true });
        (_app, _client) = BuildApp(_sink);
    }

    // Сборка тестового приложения шлюза с заданным приёмником (шов для теста сбоя приёмника).
    // LlmProviderRegistry/ILocalModelRequestSink — опциональные швы из DI (GetService в шлюзе):
    // в тесте без них наблюдатель не создаётся, поведение прежнее.
    private (WebApplication App, HttpClient Client) BuildApp(ILocalModelRequestSink sink)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddSingleton<IOptionsMonitor<LlmGatewayOptions>>(_kit.Options);
        builder.Services.AddSingleton(_tokens);
        builder.Services.AddSingleton(_kit.Selector);
        builder.Services.AddSingleton(_kit.Providers);
        builder.Services.AddSingleton(new SubscriptionLimitRecorder(_kit.Usage, _kit.Pool));
        builder.Services.AddSingleton<IOptionsMonitor<LocalModelStatsOptions>>(_statsOptions);
        builder.Services.AddSingleton<ILocalModelRequestSink>(sink);
        builder.Services.AddHttpClient(LlmGatewayEndpoints.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => _upstream);
        _device.AddTo(builder.Services);
        var app = builder.Build();
        app.MapLlmGateway();
        app.StartAsync().GetAwaiter().GetResult();
        return (app, app.GetTestClient());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _kit.Dispose();
        _device.Dispose();
    }

    private IssuedTurnToken StartLocal(string model)
    {
        var start = _kit.Selector.StartTurn(_tokens, "owner", "chat", _device.Id, model);
        start.Token.Should().NotBeNull(start.FailureText);
        return start.Token!;
    }

    private HttpRequestMessage Req(IssuedTurnToken t, string path, string? json = null)
    {
        var r = _device.Sign(new HttpRequestMessage(HttpMethod.Post, $"/gw/t/{t.Grant.TurnId}/llm/{path}"));
        r.Headers.TryAddWithoutValidation(TurnTokenEndpointFilter.HeaderName, t.Token);
        if (json is not null) r.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return r;
    }

    // SSE-ответ: upstream отдаёт поток из Pipe, тест пишет в него кусками.
    private (Pipe Pipe, Func<Task<HttpResponseMessage>> Send) SseUpstream(IssuedTurnToken t, HttpClient? client = null)
    {
        var c = client ?? _client;
        var pipe = new Pipe();
        _upstream.Respond = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(pipe.Reader.AsStream()) };
            r.Content.Headers.ContentType = new("text/event-stream");
            return r;
        };
        return (pipe, () => c.SendAsync(Req(t, "v1/messages", $"{{\"model\":\"{LocalModel}\"}}"),
            HttpCompletionOption.ResponseHeadersRead));
    }

    private static string ReadFixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LocalStats", "vllm-messages-stream.sse"));

    // Пишет фикстуру в pipe кусками заданного размера и возвращает, что клиент получил.
    private async Task<string> StreamFixtureAsync(IssuedTurnToken t, int chunk, HttpClient? client = null)
    {
        var (pipe, send) = SseUpstream(t, client);
        var resp = await send();
        var bytes = Encoding.UTF8.GetBytes(ReadFixture());
        for (var i = 0; i < bytes.Length; i += chunk)
            await pipe.Writer.WriteAsync(bytes.AsMemory(i, Math.Min(chunk, bytes.Length - i)));
        await pipe.Writer.CompleteAsync();
        return await new StreamReader(await resp.Content.ReadAsStreamAsync()).ReadToEndAsync();
    }

    // (а) ПОБАЙТНОСТЬ: вход апстрима = выход клиенту при включённом наблюдателе,
    //     на живой фикстуре и при делении потока на куски 1/7/4096 байт.
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task Побайтность_КлиентПолучилВесьПоток_НаблюдательСоздан(int chunk)
    {
        var t = StartLocal(LocalModel);
        var got = await StreamFixtureAsync(t, chunk);

        got.Should().Be(ReadFixture(), "клиент получил байты апстрима без изменений");
        _sink.Started.Should().Be(1, "наблюдатель создан: запрос начался");
        _sink.FirstToken.Should().Be(1, "наблюдатель увидел первый токен");
        _sink.Completed.Should().Be(1, "наблюдатель завершён: итог доставлен");
        _sink.LastUsage!.OutputTokens.Should().Be(4, "итог наблюдателя — из фикстуры");
    }

    // (б) Исключение наблюдателя/приёмника: клиент получил ВСЕ байты (поток не искажён),
    //     упавший приёмник отключился (OnRequestCompleted не доставлен). Шов: ThrowingSink
    //     бросает из OnFirstToken — тот же try/catch, что ловит сбой Feed наблюдателя.
    //     Мутация: убрать try/catch вокруг наблюдателя → исключение уходит клиенту, тест краснеет.
    [Fact]
    public async Task ИсключениеНаблюдателя_КлиентПолучилВсеБайты()
    {
        var sink = new ThrowingSink();
        var (app, client) = BuildApp(sink);
        try
        {
            var t = StartLocal(LocalModel);
            var got = await StreamFixtureAsync(t, 7, client);

            got.Should().Be(ReadFixture(), "исключение наблюдателя не искажает поток клиента");
            sink.FirstToken.Should().Be(1, "наблюдатель увидел первый токен и упал");
            sink.Completed.Should().Be(0, "упавший приёмник отключился: итог не доставлен");
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    // (б2) Исключение в колбэке OnTokenCount (M5): колбэк живёт ВНУТРИ Feed наблюдателя,
    //     поэтому его сбой глотает try/catch наблюдателя (SseUsageObserver.OnContentBlockDelta),
    //     а не try/catch шлюза вокруг Feed. Клиент получает ВСЕ байты (поток не искажён),
    //     наблюдатель проглатывает сбой и остаётся жив: все дельты дошли, итог доставлен.
    //     Мутация: убрать try/catch вокруг колбэка в наблюдателе И вокруг Feed в шлюзе →
    //     сбой уходит клиенту, поток обрывается, тест краснеет.
    [Fact]
    public async Task ИсключениеВКолбэкеТокенов_КлиентПолучилВсеБайты()
    {
        var sink = new TokenThrowingSink();
        var (app, client) = BuildApp(sink);
        try
        {
            var t = StartLocal(LocalModel);
            var got = await StreamFixtureAsync(t, 7, client);

            got.Should().Be(ReadFixture(), "сбой колбэка токенов не искажает поток клиента");
            sink.TokenCountCalls.Should().Be(3, "наблюдатель проглотил все 3 дельты фикстуры и остался жив");
            sink.Completed.Should().Be(1, "наблюдатель не отключился: итог хода доставлен");
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    // (в1) Метрики выключены — наблюдатель не создаётся (Started не растёт).
    [Fact]
    public async Task МетрикиВыключены_НаблюдательНеСоздан()
    {
        var t = StartLocal(LocalModel);
        await StreamFixtureAsync(t, 7);
        _sink.Started.Should().Be(1, "метрики включены: наблюдатель создан");

        _statsOptions.CurrentValue = new LocalModelStatsOptions { Enabled = false };
        var t2 = StartLocal(LocalModel);
        await StreamFixtureAsync(t2, 7);

        _sink.Started.Should().Be(1, "метрики выключены: второй наблюдатель не создан");
    }

    // (в2) Нелокальный маршрут (deepseek) — наблюдатель не создаётся, даже если метрики включены.
    [Fact]
    public async Task НелокальныйМаршрут_НаблюдательНеСоздан()
    {
        var t = _kit.Selector.StartTurn(_tokens, "owner", "chat", _device.Id, "deepseek-v4-pro");
        t.Token.Should().NotBeNull(t.FailureText);
        var (pipe, send) = SseUpstream(t.Token!);
        var resp = await send();
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(ReadFixture()));
        await pipe.Writer.CompleteAsync();
        await new StreamReader(await resp.Content.ReadAsStreamAsync()).ReadToEndAsync();

        _sink.Started.Should().Be(0, "deepseek — не локальный, наблюдатель не создан");
    }

    // (в3) Не-SSE ответ (JSON) — наблюдатель не создаётся.
    [Fact]
    public async Task НеSseОтвет_НаблюдательНеСоздан()
    {
        var t = StartLocal(LocalModel);
        _upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        var resp = await _client.SendAsync(Req(t, "v1/messages", $"{{\"model\":\"{LocalModel}\"}}"));
        await resp.Content.ReadAsStringAsync();

        _sink.Started.Should().Be(0, "ответ JSON — наблюдатель не создан");
    }

    // (г) С включённым нормализатором (mmx) поведение прежнее: SSE-байты как прежде,
    //     наблюдатель не создаётся (нормализатор исключает его).
    [Fact]
    public async Task НормализаторВключен_НаблюдательНеСоздан_БайтыКакПрежне()
    {
        var t = _kit.Selector.StartTurn(_tokens, "owner", "chat", _device.Id, "mmx-m3");
        t.Token.Should().NotBeNull(t.FailureText);
        var (pipe, send) = SseUpstream(t.Token!);
        var resp = await send();
        var body = ReadFixture();
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(body));
        await pipe.Writer.CompleteAsync();

        (await new StreamReader(await resp.Content.ReadAsStreamAsync()).ReadToEndAsync())
            .Should().Be(body, "с нормализатором (mmx) SSE-байты проходят как прежде");
        _sink.Started.Should().Be(0, "нормализатор исключает наблюдателя");
    }
}
