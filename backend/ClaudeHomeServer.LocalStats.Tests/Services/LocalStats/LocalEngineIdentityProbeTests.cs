using System.Net;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// Проба живой идентичности движка для панели (S2): /v1/models разобран независимо от id каталога;
/// однозначная активная запись → ModelLive + max_model_len (фолбэк — engine.max_context из JSON /metrics);
/// битый JSON/таймаут/HTTP-ошибка/несколько активных записей → пустой снимок, без исключений наружу;
/// кэш и single-flight по нормализованному BaseUrl.
/// </summary>
public class LocalEngineIdentityProbeTests
{
    // HttpMessageHandler-стаб: отдаёт заготовленный ответ, считает вызовы по пути.
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }
        public TimeSpan Delay { get; set; }
        public int ModelsCalls { get; private set; }
        public int MetricsCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/metrics", StringComparison.OrdinalIgnoreCase))
                MetricsCalls++;
            else
                ModelsCalls++;
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, ct);
            return Responder!.Invoke(req);
        }
    }

    private sealed class StubFactory(StubHandler handler) : IHttpClientFactory
    {
        public StubHandler Handler => handler;
        public HttpClient CreateClient(string name) => new HttpClient(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private const string BaseUrl = "http://127.0.0.1:18020";

    [Fact]
    public async Task Probe_ОднаМодельLoaded_МодельИОкноИзMaxModelLen()
    {
        var handler = new StubHandler
        {
            Responder = _ => Json(
                """{"object":"list","data":[{"id":"inkling-2-mini","object":"model","max_model_len":256000,"owned_by":"vllm"}]}"""),
        };
        var probe = new LocalEngineIdentityProbe(new StubFactory(handler));

        var identity = await probe.ProbeAsync(BaseUrl);

        identity.ModelLive.Should().Be("inkling-2-mini");
        identity.ContextWindowLive.Should().Be(256000);
        identity.EngineLive.Should().BeNull();
        handler.ModelsCalls.Should().Be(1);
    }

    // Окно нет в записи — запасной вариант engine.max_context из JSON /metrics (Strata-адаптер).
    [Fact]
    public async Task Probe_НетMaxModelLen_ФолбэкИзMetricsJson()
    {
        var handler = new StubHandler
        {
            Responder = r => r.RequestUri!.AbsolutePath == "/metrics"
                ? Json("""{"engine":{"max_context":131072}}""")
                : Json("""{"data":[{"id":"strata-model","status":{"value":"loaded","failed":false}}]}"""),
        };
        var probe = new LocalEngineIdentityProbe(new StubFactory(handler));

        var identity = await probe.ProbeAsync(BaseUrl);

        identity.ModelLive.Should().Be("strata-model");
        identity.ContextWindowLive.Should().Be(131072);
        handler.MetricsCalls.Should().Be(1);
    }

    // Bитет JSON → пустой снимок, никаких исключений наружу.
    [Fact]
    public async Task Probe_МусорВОтвете_Пусто()
    {
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not a json", System.Text.Encoding.UTF8, "application/json"),
            },
        };
        var probe = new LocalEngineIdentityProbe(new StubFactory(handler));

        var identity = await probe.ProbeAsync(BaseUrl);

        identity.ModelLive.Should().BeNull();
        identity.ContextWindowLive.Should().BeNull();
    }

    [Fact]
    public async Task Probe_HTTPОшибка_Пусто()
    {
        var handler = new StubHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
        var probe = new LocalEngineIdentityProbe(new StubFactory(handler));

        var identity = await probe.ProbeAsync(BaseUrl);

        identity.ModelLive.Should().BeNull();
    }

    [Fact]
    public async Task Probe_Таймаут_Пусто()
    {
        var handler = new StubHandler { Delay = TimeSpan.FromMilliseconds(500) };
        handler.Responder = _ => Json("""{"data":[{"id":"m","max_model_len":1}]}""");
        var probe = new LocalEngineIdentityProbe(new StubFactory(handler), timeout: TimeSpan.FromMilliseconds(50));

        var identity = await probe.ProbeAsync(BaseUrl);

        identity.ModelLive.Should().BeNull();
    }

    // Несколько loaded записей — не угадываем: live=null.
    [Fact]
    public async Task Probe_НесколькоLoaded_Пусто()
    {
        var handler = new StubHandler
        {
            Responder = _ => Json("""{"data":[{"id":"a","max_model_len":1000,"status":{"value":"loaded"}},{"id":"b","max_model_len":2000,"status":{"value":"loaded"}}]}"""),
        };
        var probe = new LocalEngineIdentityProbe(new StubFactory(handler));

        var identity = await probe.ProbeAsync(BaseUrl);

        identity.ModelLive.Should().BeNull();
        identity.ContextWindowLive.Should().BeNull();
    }

    // Unloaded единственная запись → live=null.
    [Fact]
    public async Task Probe_Unloaded_Пусто()
    {
        var handler = new StubHandler
        {
            Responder = _ => Json("""{"data":[{"id":"m","status":{"value":"unloaded","failed":true}}]}"""),
        };
        var probe = new LocalEngineIdentityProbe(new StubFactory(handler));

        var identity = await probe.ProbeAsync(BaseUrl);

        identity.ModelLive.Should().BeNull();
    }

    // Кэш: два последовательных чтения одного URL — один HTTP /v1/models.
    [Fact]
    public async Task Probe_Кеш_ДваВызова_ОдинЗапрос()
    {
        var handler = new StubHandler
        {
            Responder = _ => Json("""{"data":[{"id":"m","max_model_len":1000}]}"""),
        };
        var probe = new LocalEngineIdentityProbe(new StubFactory(handler));

        await probe.ProbeAsync(BaseUrl);
        await probe.ProbeAsync(BaseUrl);

        handler.ModelsCalls.Should().Be(1);
    }

    // Ответ сменившегося движка не попадает в кэш другого URL.
    [Fact]
    public async Task Probe_КэшОтдельныйПоUrl()
    {
        var handler = new StubHandler
        {
            Responder = r => Json(r.RequestUri!.ToString().Contains("18020")
                ? """{"data":[{"id":"m-a","max_model_len":1000}]}"""
                : """{"data":[{"id":"m-b","max_model_len":2000}]}"""),
        };
        var probe = new LocalEngineIdentityProbe(new StubFactory(handler));

        var a = await probe.ProbeAsync("http://127.0.0.1:18020");
        var b = await probe.ProbeAsync("http://127.0.0.1:18021");

        a.ModelLive.Should().Be("m-a");
        b.ModelLive.Should().Be("m-b");
        handler.ModelsCalls.Should().Be(2);
    }

    // Неудача снимает вчерашний кэш: live-имя не остаётся вчерашним свежее после отказа стенда.
    [Fact]
    public async Task Probe_НеудачаПослеУспеха_КешСнимается()
    {
        var ok = true;
        var handler = new StubHandler
        {
            Responder = _ => ok
                ? Json("""{"data":[{"id":"m","max_model_len":1000}]}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        // TTL нулевой: каждая проба снова идёт в сеть (кэш не пропускает HTTP).
        var probe = new LocalEngineIdentityProbe(new StubFactory(handler), cacheFor: TimeSpan.Zero);

        var first = await probe.ProbeAsync(BaseUrl);
        first.ModelLive.Should().Be("m");

        ok = false;
        var second = await probe.ProbeAsync(BaseUrl);
        second.ModelLive.Should().BeNull();

        var third = await probe.ProbeAsync(BaseUrl);
        third.ModelLive.Should().BeNull();
        third.ContextWindowLive.Should().BeNull();
    }

    // Concurrency: два параллельных чтения одного URL — один HTTP /v1/models, общий результат.
    // Мутация «снять single-flight» → тест красный.
    [Fact]
    public async Task Probe_ConcurrentReads_SingleModelsRequest()
    {
        var handler = new StubHandler { Delay = TimeSpan.FromMilliseconds(150) };
        handler.Responder = _ => Json("""{"data":[{"id":"m","max_model_len":1000}]}""");
        var probe = new LocalEngineIdentityProbe(new StubFactory(handler), cacheFor: TimeSpan.FromMinutes(5));

        var results = await Task.WhenAll(probe.ProbeAsync(BaseUrl), probe.ProbeAsync(BaseUrl));

        handler.ModelsCalls.Should().Be(1);
        results[0].ModelLive.Should().Be("m");
        results[1].ModelLive.Should().Be("m");
    }

    // /v1/models не содержит ключа data → пустой снимок.
    [Fact]
    public async Task Probe_НетData_Пусто()
    {
        var handler = new StubHandler { Responder = _ => Json("""{"object":"list"}""") };
        var probe = new LocalEngineIdentityProbe(new StubFactory(handler));

        var identity = await probe.ProbeAsync(BaseUrl);

        identity.ModelLive.Should().BeNull();
    }
}
