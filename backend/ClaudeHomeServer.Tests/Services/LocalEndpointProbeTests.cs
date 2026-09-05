using System.Net;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Pre-flight проба локального эндпоинта: отличает «локальный llama.cpp/vLLM выключен» от
/// «облачный провайдер упал». При Down → FailLocalDownAsync без шагов цепочки; Alive →
/// ход идёт штатно; NotApplicable → проба не нужна (провайдер не локальный или нет каталога).
/// </summary>
public class LocalEndpointProbeTests
{
    // HttpMessageHandler-стаб: отдаёт заготовленный ответ, считает вызовы.
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }
        public TimeSpan? Hang { get; set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls++;
            if (Hang is { } h)
            {
                // ct отменится по таймауту внутри пробы — это часть проверки «таймаут → Down»
                await Task.Delay(h, ct);
            }
            return Responder!.Invoke(req);
        }
    }

    // HttpClient с нашим handler. IHttpClientFactory.CreateClient(HttpClientName) возвращает
    // именованный клиент, но в тесте мы подменяем фабрику — handler общий на все вызовы.
    private sealed class StubFactory(StubHandler handler) : IHttpClientFactory
    {
        public StubHandler Handler => handler;
        public HttpClient CreateClient(string name) => new HttpClient(handler, disposeHandler: false);
    }

    private static LlmProviderConfig LocalProvider(string baseUrl = "http://127.0.0.1:8080",
        string modelId = "qwen38-27b", string key = "local-qwen")
    {
        var p = new LlmProviderConfig
        {
            Key = key,
            DisplayName = "Локальная Qwen3.8-27B",
            AnthropicBaseUrl = baseUrl,
            ApiBaseUrl = baseUrl + "/v1",
            IsLocal = true,
        };
        p.Models.Add(new LlmModelConfig { Id = modelId, DisplayName = "Qwen3.8-27B", ContextWindow = 57344 });
        return p;
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task CheckAsync_LoadedОтвет_Alive()
    {
        var handler = new StubHandler
        {
            Responder = _ => Json("""{"data":[{"id":"qwen38-27b","status":{"value":"loaded","failed":false}}]}"""),
        };
        var probe = new LocalEndpointProbe(new StubFactory(handler));

        var outcome = await probe.CheckAsync(LocalProvider());

        outcome.Should().Be(LocalProbeOutcome.Alive);
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task CheckAsync_UnloadedОтвет_Down()
    {
        // Реальный ответ llama.cpp на выключенную модель: status.value="unloaded".
        var handler = new StubHandler
        {
            Responder = _ => Json("""{"data":[{"id":"qwen38-27b","status":{"value":"unloaded","exit_code":1,"failed":true}}]}"""),
        };
        var probe = new LocalEndpointProbe(new StubFactory(handler));

        var outcome = await probe.CheckAsync(LocalProvider());

        outcome.Should().Be(LocalProbeOutcome.Down);
    }

    [Fact]
    public async Task CheckAsync_FailedTrue_Down()
    {
        // Случай «loaded без failed» считаем живым. «loaded + failed:true» — Down.
        var handler = new StubHandler
        {
            Responder = _ => Json("""{"data":[{"id":"qwen38-27b","status":{"value":"loaded","failed":true}}]}"""),
        };
        var probe = new LocalEndpointProbe(new StubFactory(handler));

        var outcome = await probe.CheckAsync(LocalProvider());

        outcome.Should().Be(LocalProbeOutcome.Down);
    }

    [Fact]
    public async Task CheckAsync_404Http_Down()
    {
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var probe = new LocalEndpointProbe(new StubFactory(handler));

        var outcome = await probe.CheckAsync(LocalProvider());

        outcome.Should().Be(LocalProbeOutcome.Down);
    }

    [Fact]
    public async Task CheckAsync_НевалидныйJson_Down()
    {
        // llama.cpp под нагрузкой может отдать битый JSON — для пробы это Down.
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not a json", System.Text.Encoding.UTF8, "application/json"),
            },
        };
        var probe = new LocalEndpointProbe(new StubFactory(handler));

        var outcome = await probe.CheckAsync(LocalProvider());

        outcome.Should().Be(LocalProbeOutcome.Down);
    }

    [Fact]
    public async Task CheckAsync_Таймаут_Down()
    {
        // Handler висит дольше таймаута пробы (200 мс) → проба отменяет и возвращает Down.
        var handler = new StubHandler
        {
            Responder = _ => Json("ok"),
            Hang = TimeSpan.FromMilliseconds(2000),
        };
        var probe = new LocalEndpointProbe(new StubFactory(handler), timeout: TimeSpan.FromMilliseconds(100));

        var outcome = await probe.CheckAsync(LocalProvider());

        outcome.Should().Be(LocalProbeOutcome.Down);
    }

    [Fact]
    public async Task CheckAsync_КешНа5Секунд_НеДолбитЭндпоинт()
    {
        // Два вызова подряд в течение кеша — handler срабатывает один раз, второй ответ
        // берётся из кеша. Поведение «пачка шагов секунда в секунду» (см. комментарий
        // LocalEndpointProbe.cs).
        var handler = new StubHandler
        {
            Responder = _ => Json("""{"data":[{"id":"qwen38-27b","status":{"value":"loaded","failed":false}}]}"""),
        };
        var probe = new LocalEndpointProbe(new StubFactory(handler));

        await probe.CheckAsync(LocalProvider());
        await probe.CheckAsync(LocalProvider());

        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task CheckAsync_InvalidateСбрасываетКеш()
    {
        var handler = new StubHandler
        {
            Responder = _ => Json("""{"data":[{"id":"qwen38-27b","status":{"value":"loaded","failed":false}}]}"""),
        };
        var probe = new LocalEndpointProbe(new StubFactory(handler));

        await probe.CheckAsync(LocalProvider());
        probe.Invalidate("local-qwen");
        await probe.CheckAsync(LocalProvider());

        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task CheckAsync_НеЛокальныйПровайдер_NotApplicable()
    {
        // Провайдер не помечен IsLocal — проба не должна ходить в сеть вообще.
        var handler = new StubHandler
        {
            Responder = _ => Json("ok"),
        };
        var probe = new LocalEndpointProbe(new StubFactory(handler));
        var cloud = new LlmProviderConfig
        {
            Key = "glm-cloud-test",
            DisplayName = "GLM",
            AnthropicBaseUrl = "https://api.z.ai/api/anthropic",
            IsLocal = false,
        };
        cloud.Models.Add(new LlmModelConfig { Id = "glm-5.2", ContextWindow = 200000 });

        var outcome = await probe.CheckAsync(cloud);

        outcome.Should().Be(LocalProbeOutcome.NotApplicable);
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task CheckAsync_ПустойКаталогМоделей_NotApplicable()
    {
        // Каталог пуст: проба не знает, какую модель искать в /v1/models. Не выдумываем —
        // возвращаем NotApplicable, вызывающий отдаст ошибку конфигурации провайдера.
        // Уникальный ключ, чтобы не пересечься с кешем прошлых тестов под local-qwen.
        var handler = new StubHandler
        {
            Responder = _ => Json("ok"),
        };
        var probe = new LocalEndpointProbe(new StubFactory(handler));
        var p = LocalProvider(key: "local-qwen-empty-catalog");
        p.Models.Clear();

        var outcome = await probe.CheckAsync(p);

        outcome.Should().Be(LocalProbeOutcome.NotApplicable);
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public void BuildModelsUrl_ОтрезаетХвостV1()
    {
        // AnthropicBaseUrl уже с /v1 — не должно получиться /v1/v1/models.
        LocalEndpointProbe.BuildModelsUrl("http://127.0.0.1:8080/v1")
            .Should().Be("http://127.0.0.1:8080/v1/models");
    }

    [Fact]
    public void BuildModelsUrl_БезV1Добавляет()
    {
        LocalEndpointProbe.BuildModelsUrl("http://127.0.0.1:8080")
            .Should().Be("http://127.0.0.1:8080/v1/models");
    }

    [Fact]
    public void BuildModelsUrl_ТреугольныйХвостОтрезает()
    {
        // Слеш в конце — отрезаем, не делаем /v1/models/.
        LocalEndpointProbe.BuildModelsUrl("http://127.0.0.1:8080/")
            .Should().Be("http://127.0.0.1:8080/v1/models");
    }
}
