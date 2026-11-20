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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Services.Gateway;

/// <summary>
/// Проводка «скорости ответа» в шлюзе LLM (фича «Скорость ответа», шаг 1 бэкенда): момент старта
/// снимается ДО отправки запроса апстриму — полное время запроса включает и его ответ; первый
/// токен и конец — по потоку; токены — из usage ответа. В учёт идут только локальные маршруты
/// при включённых метриках; дорожка — StatsTrackOf провайдера маршрута.
/// </summary>
public sealed class LlmGatewayAnswerSpeedTests : IAsyncDisposable
{
    private const string LocalModel = "qwen3.8-27b";

    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-20T12:00:00+00:00");

    // «Апстрим ответил через 5 с»: эти 5 с обязаны войти в полное время запроса — именно для
    // этого момент старта снимается до SendAsync. Если снять его после заголовков — длительность
    // выйдет 0, сквозной скорости не будет, тест краснеет.
    private static readonly TimeSpan UpstreamDelay = TimeSpan.FromSeconds(5);

    // Ответ с usage: 40 выходных токенов (от порога медианы 32), одна content_block_delta.
    private const string SseBody =
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":10,\"output_tokens\":0}}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"привет\"}}\n\n" +
        "event: message_delta\ndata: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":40}}\n\n";

    // Приёмник хода: в бою он зарегистрирован всегда (LlmSubsystem), и без него наблюдатель SSE
    // не создаётся (CreateUsageObserver) — значит, не будет ни токенов, ни момента конца.
    // Тесту он нужен только чтобы поток дошёл до завершения.
    private sealed class NullSink : ILocalModelRequestSink
    {
        public void OnRequestStarted(string requestId, string turnId, string? providerKey = null) { }
        public void OnFirstToken(string requestId, string turnId) { }
        public void OnRequestCompleted(string requestId, string turnId, RequestUsage usage) { }
        public void OnTokenCount(string requestId, string turnId, int cumulativeTokens) { }
    }

    // Шпион сборщика: считает вызовы и помнит дорожки, делегируя настоящему. Без него «не
    // учтено» и «начато, но не завершено» на агрегатах неотличимы — гейт локальности надо
    // видеть по самому вызу OnRequestStarted.
    private sealed class SpyAnswerSpeed(LocalModelAnswerSpeed inner) : ILocalModelAnswerSpeed
    {
        public int Started;
        public int FirstToken;
        public int Completed;
        public List<string> Tracks { get; } = [];

        public void OnRequestStarted(string requestId, string track, DateTimeOffset startedAt)
        {
            Interlocked.Increment(ref Started);
            lock (Tracks) Tracks.Add(track);
            inner.OnRequestStarted(requestId, track, startedAt);
        }

        public void OnFirstToken(string requestId, DateTimeOffset at)
        {
            Interlocked.Increment(ref FirstToken);
            inner.OnFirstToken(requestId, at);
        }

        public void OnRequestCompleted(string requestId, long? outputTokens, DateTimeOffset completedAt)
        {
            Interlocked.Increment(ref Completed);
            inner.OnRequestCompleted(requestId, outputTokens, completedAt);
        }

        public AnswerSpeedSnapshot SnapshotOf(string track) => inner.SnapshotOf(track);
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    // Фейковый upstream: отвечает через UpstreamDelay (двигая тестовые часы) и отдаёт SSE-поток.
    private sealed class FakeUpstream(ManualTime time) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            time.Advance(UpstreamDelay);
            return Task.FromResult(Respond(request));
        }
    }

    private readonly ManualTime _time = new(T0);
    private readonly GatewayTestKit _kit;
    private readonly FakeUpstream _upstream;
    private readonly TurnTokenService _tokens = new(new TurnEventBus());
    private readonly GatewayTestDevice _device = new("owner");
    private readonly LocalModelAnswerSpeed _collector;
    private readonly SpyAnswerSpeed _speed;
    private readonly MutableOptionsMonitor<LocalModelStatsOptions> _statsOptions;
    private readonly WebApplication _app;
    private readonly HttpClient _client;

    public LlmGatewayAnswerSpeedTests()
    {
        _kit = new GatewayTestKit(("st1", "tok-st1", null), ("api", null, "sk-ant-api"));
        _upstream = new FakeUpstream(_time);
        _collector = new LocalModelAnswerSpeed(_time);
        _speed = new SpyAnswerSpeed(_collector);
        _statsOptions = new MutableOptionsMonitor<LocalModelStatsOptions>(new LocalModelStatsOptions { Enabled = true });
        (_app, _client) = BuildApp(_kit.Selector, _kit.Providers);
    }

    private (WebApplication App, HttpClient Client) BuildApp(UpstreamSelector selector, LlmProviderRegistry providers)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddSingleton<IOptionsMonitor<LlmGatewayOptions>>(_kit.Options);
        builder.Services.AddSingleton(_tokens);
        builder.Services.AddSingleton(selector);
        builder.Services.AddSingleton(providers);
        builder.Services.AddSingleton(new SubscriptionLimitRecorder(_kit.Usage, _kit.Pool));
        builder.Services.AddSingleton<IOptionsMonitor<LocalModelStatsOptions>>(_statsOptions);
        // Часы теста — те же, что у сборщика: моменты шлюза проверяются на них.
        builder.Services.AddSingleton<TimeProvider>(_time);
        builder.Services.AddSingleton<ILocalModelAnswerSpeed>(_speed);
        builder.Services.AddSingleton<ILocalModelRequestSink>(new NullSink());
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

    // Локальный SSE-запрос через шлюз: upstream отвечает через UpstreamDelay, поток пишется в pipe.
    private async Task StreamAsync(IssuedTurnToken t, HttpClient client, string model)
    {
        var pipe = new Pipe();
        _upstream.Respond = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(pipe.Reader.AsStream()) };
            r.Content.Headers.ContentType = new("text/event-stream");
            return r;
        };
        var req = _device.Sign(new HttpRequestMessage(HttpMethod.Post, $"/gw/t/{t.Grant.TurnId}/llm/v1/messages"));
        req.Headers.TryAddWithoutValidation(TurnTokenEndpointFilter.HeaderName, t.Token);
        req.Content = new StringContent($"{{\"model\":\"{model}\"}}", Encoding.UTF8, "application/json");

        var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(SseBody));
        await pipe.Writer.CompleteAsync();
        _ = await new StreamReader(await resp.Content.ReadAsStreamAsync()).ReadToEndAsync();
    }

    // (1) Локальный маршрут: запрос попал в учёт целиком. 40 токенов / 5 с ответа апстрима = 8.
    //     Сквозная скорость есть именно потому, что старт снят ДО отправки: после заголовков
    //     длительность была бы 0 (и EndToEndTps — null).
    [Fact]
    public async Task ЛокальныйМаршрут_УчтёнЦеликом_ВремяАпстримаВнутриДлительности()
    {
        var t = StartLocal(LocalModel);
        await StreamAsync(t, _client, LocalModel);

        _speed.Started.Should().Be(1, "локальный маршрут — запрос начат в учёте");
        _speed.FirstToken.Should().Be(1);
        _speed.Completed.Should().Be(1);
        lock (_speed.Tracks) _speed.Tracks.Should().BeEquivalentTo(new[] { LocalModelStatsPoller.MainTrackName },
            "дорожка основного локального провайдера — main");

        var snap = _collector.SnapshotOf(LocalModelStatsPoller.MainTrackName);
        var w = snap.Windows.Single(x => x.Window == "5m");
        w.Requests.Should().Be(1);
        w.EndToEndTps.Should().BeApproximately(40.0 / UpstreamDelay.TotalSeconds, 1e-6,
            "полное время запроса включает ответ апстрима");
        w.MedianTps.Should().BeApproximately(8.0, 1e-6, "40 токенов ≥ порога 32 — запрос в медиане");
        w.MedianTtftSec.Should().NotBeNull("первый токен был");
        w.P90TtftSec.Should().NotBeNull();
        snap.MinTokensForMedian.Should().Be(32);
    }

    // (2) Облачный маршрут (подписка, модель не локальная): в учёт не входит — он не про скорость
    //     локальной модели. Мутация: снять гейт IsLocal в шлюзе — Requests станет 1, тест краснеет.
    [Fact]
    public async Task ОблачныйМаршрут_НеУчитывается()
    {
        var sub = _tokens.Issue("owner", "chat", _device.Id,
            new GatewayRoute(GatewayUpstreamKind.Subscription, "claude-sonnet-4-5", SubscriptionKey: "st1"));
        await StreamAsync(sub, _client, "claude-sonnet-4-5");

        // Гейт локальности виден по самому началу: запрос в учёт не идёт даже открытым.
        // Мутация «учитывать любой маршрут» (без ResolveByModel().IsLocal) → Started = 1, краснеет.
        _speed.Started.Should().Be(0, "облачный запрос не про скорость локальной модели");
        _collector.SnapshotOf(LocalModelStatsPoller.MainTrackName)
            .Windows.All(w => w.Requests == 0).Should().BeTrue();
    }

    // (3) Метрики выключены (LocalModelStats:Enabled=false) — ни одной записи, поведение шлюза прежнее.
    [Fact]
    public async Task МетрикиВыключены_УчётаНет()
    {
        _statsOptions.CurrentValue = new LocalModelStatsOptions { Enabled = false };
        var t = StartLocal(LocalModel);
        await StreamAsync(t, _client, LocalModel);

        _speed.Started.Should().Be(0, "метрики выключены — ни одного вызова сборщика");
        _collector.SnapshotOf(LocalModelStatsPoller.MainTrackName)
            .Windows.All(w => w.Requests == 0).Should().BeTrue();
    }

    // (4) Дорожка — по провайдеру маршрута (StatsTrackOf): провайдер с явным StatsEngine ведёт
    //     свою дорожку, в main его запроса нет. Без явного StatsEngine основной локальный — main.
    [Fact]
    public async Task ДорожкаПоПровайдеруМаршрута_ЯвныйStatsEngine_SвояДорожка()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LlmProviders:local-gemma:DisplayName"] = "Local Gemma",
            ["LlmProviders:local-gemma:AnthropicBaseUrl"] = "http://127.0.0.1:8081",
            ["LlmProviders:local-gemma:IsLocal"] = "true",
            ["LlmProviders:local-gemma:StatsEngine"] = "gemma",
            ["LlmProviders:local-gemma:Models:0:Id"] = "gemma-4-26b-a4b",
        }).Build();
        var providers = new LlmProviderRegistry(config);
        var selector = new UpstreamSelector(_kit.Pool, providers, _kit.Options);
        var (app, client) = BuildApp(selector, providers);
        try
        {
            var start = selector.StartTurn(_tokens, "owner", "chat", _device.Id, "gemma-4-26b-a4b");
            start.Token.Should().NotBeNull(start.FailureText);
            await StreamAsync(start.Token!, client, "gemma-4-26b-a4b");

            lock (_speed.Tracks) _speed.Tracks.Should().BeEquivalentTo(new[] { "gemma" },
                "дорожка — StatsTrackOf провайдера маршрута");
            _collector.SnapshotOf("gemma").Windows.Single(w => w.Window == "5m").Requests.Should().Be(1);
            _collector.SnapshotOf(LocalModelStatsPoller.MainTrackName)
                .Windows.All(w => w.Requests == 0).Should().BeTrue("чужая дорожка в main не пишется");
        }
        finally
        {
            await app.DisposeAsync();
        }
    }
}
