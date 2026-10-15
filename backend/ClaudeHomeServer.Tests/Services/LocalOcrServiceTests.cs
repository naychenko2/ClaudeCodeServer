using System.Net;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Services;

// OCR страницы локальной Qwen (фича «руки без UIA», этап A, шаг A1): картинка → текст.
// Вызов vLLM в OpenAI-формате, адрес/модель из реестра (local-qwen), живость — проба.
// Отказы без исключений наружу: Disabled/LocalUnavailable/BusyTimeout/TooLarge/BadResponse.
public class LocalOcrServiceTests
{
    private const string OcrText = "UNIQUE_OCR_TEXT_MARKER_42";

    // --- фейки ---

    private sealed class FakeHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeProbe(LocalProbeOutcome outcome) : ILocalEndpointProbe
    {
        public Task<LocalProbeOutcome> CheckAsync(LlmProviderConfig provider, CancellationToken ct = default) =>
            Task.FromResult(outcome);
        public bool TryGetKnownContextWindow(string providerKey, out int window) { window = 0; return false; }
        public void Invalidate(string providerKey) { }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class MutableOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; set; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    // Фейковый vLLM: возвращает заданный JSON; при blockUntil — ждёт TCS (для BusyTimeout).
    private sealed class FakeVllm(string json, TaskCompletionSource<bool>? blockUntil = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (blockUntil is not null) await blockUntil.Task;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    // --- сборка ---

    private static (LocalOcrService Service, RecordingLogger<LocalOcrService> Log) Make(
        HttpMessageHandler handler,
        LocalProbeOutcome probe = LocalProbeOutcome.Alive,
        bool supportsImages = true,
        bool withProvider = true,
        OcrGatewayOptions? ocr = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(dir, "projects.json"),
        };
        if (withProvider)
        {
            dict["LlmProviders:local-qwen:DisplayName"] = "Local Qwen";
            dict["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:18020";
            dict["LlmProviders:local-qwen:ApiBaseUrl"] = "http://127.0.0.1:18020/v1";
            dict["LlmProviders:local-qwen:IsLocal"] = "true";
            dict["LlmProviders:local-qwen:SupportsImages"] = supportsImages ? "true" : "false";
            dict["LlmProviders:local-qwen:Models:0:Id"] = "qwen3.8-27b";
        }
        var config = TestConfig.Build(dict);
        var providers = new LlmProviderRegistry(config);
        var options = new LlmGatewayOptions { Ocr = ocr ?? new OcrGatewayOptions { Enabled = true } };
        var log = new RecordingLogger<LocalOcrService>();
        var service = new LocalOcrService(
            providers,
            new FakeProbe(probe),
            new FakeHttpFactory(handler),
            new MutableOptionsMonitor<LlmGatewayOptions>(options),
            log);
        return (service, log);
    }

    private static OcrRequestInfo Info() => new("turn-1");

    private static ReadOnlyMemory<byte> SmallImage() => new byte[] { 1, 2, 3, 4, 5 };

    private static string SuccessJson(string content, int prompt = 1234, int completion = 56) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { role = "assistant", content } },
            },
            usage = new { prompt_tokens = prompt, completion_tokens = completion },
        });

    // --- тесты ---

    [Fact]
    public async Task Успех_ПарситОтвет_СчитаетТокены()
    {
        var (service, _) = Make(new FakeVllm(SuccessJson(OcrText)));
        var result = await service.RecognizeAsync(SmallImage(), Info(), CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Text.Should().Be(OcrText);
        result.Model.Should().Be("qwen3.8-27b");
        result.PromptTokens.Should().Be(1234);
        result.CompletionTokens.Should().Be(56);
        result.Reason.Should().BeNull();
    }

    [Fact]
    public async Task Disabled_КогдаТумблерВыключен()
    {
        var (service, _) = Make(new FakeVllm(SuccessJson("x")),
            ocr: new OcrGatewayOptions { Enabled = false });
        var result = await service.RecognizeAsync(SmallImage(), Info(), CancellationToken.None);
        result.Reason.Should().Be(OcrFailureReason.Disabled);
    }

    [Fact]
    public async Task LocalUnavailable_КогдаПробаDown()
    {
        var (service, _) = Make(new FakeVllm(SuccessJson("x")), probe: LocalProbeOutcome.Down);
        var result = await service.RecognizeAsync(SmallImage(), Info(), CancellationToken.None);
        result.Reason.Should().Be(OcrFailureReason.LocalUnavailable);
    }

    [Fact]
    public async Task LocalUnavailable_КогдаНетЗрения()
    {
        var (service, _) = Make(new FakeVllm(SuccessJson("x")), supportsImages: false);
        var result = await service.RecognizeAsync(SmallImage(), Info(), CancellationToken.None);
        result.Reason.Should().Be(OcrFailureReason.LocalUnavailable);
    }

    [Fact]
    public async Task LocalUnavailable_КогдаПровайдерНеИзвестен()
    {
        var (service, _) = Make(new FakeVllm(SuccessJson("x")), withProvider: false);
        var result = await service.RecognizeAsync(SmallImage(), Info(), CancellationToken.None);
        result.Reason.Should().Be(OcrFailureReason.LocalUnavailable);
    }

    [Fact]
    public async Task TooLarge_КогдаКартинкаСверхПотолка()
    {
        var (service, _) = Make(new FakeVllm(SuccessJson("x")));
        var big = new byte[LocalOcrService.MaxImageBytes + 1];
        var result = await service.RecognizeAsync(big, Info(), CancellationToken.None);
        result.Reason.Should().Be(OcrFailureReason.TooLarge);
    }

    [Fact]
    public async Task BadResponse_МусорныйJson_БезИсключения()
    {
        var (service, _) = Make(new FakeVllm("это не json"));
        var result = await service.RecognizeAsync(SmallImage(), Info(), CancellationToken.None);
        result.Reason.Should().Be(OcrFailureReason.BadResponse);
    }

    [Fact]
    public async Task BadResponse_ПустыеChoices_БезИсключения()
    {
        var (service, _) = Make(new FakeVllm("""{"choices": []}"""));
        var result = await service.RecognizeAsync(SmallImage(), Info(), CancellationToken.None);
        result.Reason.Should().Be(OcrFailureReason.BadResponse);
    }

    [Fact]
    public async Task BadResponse_Не2xx_БезИсключения()
    {
        var handler = new HttpMessageHandlerStub(HttpStatusCode.BadRequest);
        var (service, _) = Make(handler);
        var result = await service.RecognizeAsync(SmallImage(), Info(), CancellationToken.None);
        result.Reason.Should().Be(OcrFailureReason.BadResponse);
    }

    [Fact]
    public async Task Отмена_НеГлотается()
    {
        var (service, _) = Make(new FakeVllm(SuccessJson("x")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => service.RecognizeAsync(SmallImage(), Info(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task BusyTimeout_ВторойВызов_ЖдётИОтваливается()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (service, _) = Make(new FakeVllm(SuccessJson("x"), tcs),
            ocr: new OcrGatewayOptions { Enabled = true, BusyWaitSec = 1 });

        // Первый вызов занимает слот и блокируется на HTTP-вызове (TCS ещё не завершён).
        var first = service.RecognizeAsync(SmallImage(), Info(), CancellationToken.None);
        // Второй вызов ждёт слот до потолка (1 с) и получает BusyTimeout.
        var second = await service.RecognizeAsync(SmallImage(), Info(), CancellationToken.None);

        second.Reason.Should().Be(OcrFailureReason.BusyTimeout);

        // Освобождаем первый вызов: он завершится успехом.
        tcs.SetResult(true);
        var firstResult = await first;
        firstResult.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task Лог_НеНесётТекстаИКартинки()
    {
        var image = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1 };
        var base64 = Convert.ToBase64String(image);
        var (service, log) = Make(new FakeVllm(SuccessJson(OcrText)));

        var result = await service.RecognizeAsync(image, Info(), CancellationToken.None);
        result.Ok.Should().BeTrue();

        // Ни распознанный текст, ни base64-картинка не должны попасть в лог.
        log.Entries.Should().NotContain(e => e.Message.Contains(OcrText));
        log.Entries.Should().NotContain(e => e.Message.Contains(base64));
        // Агрегаты присутствуют: размер, мс, токены.
        log.Entries.Should().Contain(e => e.Message.Contains("1234") && e.Message.Contains("56"));
    }

    // Тест мутации: temperature=0 уходит в тело запроса.
    [Fact]
    public async Task Запрос_НесётTemperatureZero_ИКартинку()
    {
        var handler = new CapturingHandler(SuccessJson("x"));
        var (service, _) = Make(handler);
        var result = await service.RecognizeAsync(SmallImage(), Info(), CancellationToken.None);
        result.Ok.Should().BeTrue();
        handler.CapturedBody.Should().Contain("\"temperature\":0");
        handler.CapturedBody.Should().Contain("data:image/png;base64,");
    }

    private sealed class CapturingHandler(string json) : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CapturedBody = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class HttpMessageHandlerStub(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("""{"error":"x"}""", Encoding.UTF8, "application/json"),
            });
    }
}
