using System.Net;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Тесты на LlamaServerClient и LocalLlmOptions: OpenAI-совместимый диалект
// /v1/chat/completions, JSON-schema через response_format, SSE-стрим для голосового
// хода, фолбэк null при любой ошибке. По образцу SpendReviewMinorTests — фейковый
// HttpMessageHandler, отвечающий заготовленным JSON/SSE.
public class LlamaServerClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "llama-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static IConfiguration Config(Dictionary<string, string?> d) => TestConfig.Build(d);

    private static LlamaServerClient NewClient(IHttpClientFactory http, IConfiguration config,
        CollectingSpend? spend = null) =>
        new(http, config, NullLogger<LlamaServerClient>.Instance, spend);

    private static IConfiguration EnabledConfig() => Config(new()
    {
        ["LocalLlm:Provider"] = "llama-server",
        ["LocalLlm:BaseUrl"] = "http://localhost:8080",
        ["LocalLlm:Model"] = "qwen3",
        ["LocalLlm:TextModel"] = "qwen3",
        ["LocalLlm:TimeoutMs"] = "4000",
    });

    // --- ChatJsonAsync: schema → response_format json_schema, разбор ответа ---

    [Fact]
    public async Task ChatJsonAsync_ШлётJsonSchema_ВозвращаетКонтент()
    {
        // Перехватываем тело запроса: должны увидеть response_format={type:json_schema,…}
        string? seenBody = null;
        var handler = new CaptureHandler(
            """{"choices":[{"message":{"content":"{\"ok\":1}"}}],"usage":{"prompt_tokens":5,"completion_tokens":3}}""",
            req => seenBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult());
        var client = NewClient(new FakeHttpFactory(handler), EnabledConfig());

        var schema = new { type = "object", properties = new { ok = new { type = "integer" } } };
        var raw = await client.ChatJsonAsync("sys", "user", schema);

        raw.Should().Be("{\"ok\":1}");
        seenBody.Should().NotBeNull();
        seenBody!.Should().Contain("\"response_format\"");
        seenBody.Should().Contain("\"type\":\"json_schema\"");
        seenBody.Should().Contain("\"strict\":true");
    }

    [Fact]
    public async Task ChatJsonAsync_СтроковыйJson_ИспользуетJsonObject()
    {
        string? seenBody = null;
        var handler = new CaptureHandler(
            """{"choices":[{"message":{"content":"{}"}}]}""",
            req => seenBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult());
        var client = NewClient(new FakeHttpFactory(handler), EnabledConfig());

        var raw = await client.ChatJsonAsync("sys", "user", "json");

        raw.Should().Be("{}");
        seenBody.Should().Contain("\"response_format\"");
        seenBody.Should().Contain("\"type\":\"json_object\"");
        seenBody.Should().NotContain("json_schema");
    }

    [Fact]
    public async Task ChatJsonAsync_DisableThinking_ПрокидываетШаблон()
    {
        string? seenBody = null;
        var handler = new CaptureHandler(
            """{"choices":[{"message":{"content":"ok"}}]}""",
            req => seenBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult());
        var client = NewClient(new FakeHttpFactory(handler), EnabledConfig());

        await client.ChatJsonAsync("sys", "user", "json");

        seenBody.Should().Contain("\"chat_template_kwargs\"");
        seenBody.Should().Contain("\"enable_thinking\":false");
        seenBody.Should().Contain("\"reasoning_format\":\"none\"");
    }

    // Сторож найденного на живом сервере дефекта: reasoning_format:"none" ВМЕСТЕ с
    // грамматикой json_schema роняет llama-server (b10666, Qwen3 14B) в 400
    // «Unexpected empty grammar stack after accepting piece: <think>» — то есть КАЖДОЕ
    // фоновое действие со строгой схемой уходило бы в платный фолбэк на claude.
    // enable_thinking:false при этом остаётся: он и подавляет размышления.
    [Fact]
    public async Task ChatJsonAsync_СоСхемой_НеШлётReasoningFormat()
    {
        string? seenBody = null;
        var handler = new CaptureHandler(
            """{"choices":[{"message":{"content":"{\"ok\":1}"}}]}""",
            req => seenBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult());
        var client = NewClient(new FakeHttpFactory(handler), EnabledConfig());

        var schema = new { type = "object", properties = new { ok = new { type = "integer" } } };
        await client.ChatJsonAsync("sys", "user", schema);

        seenBody.Should().Contain("\"type\":\"json_schema\"");
        seenBody.Should().Contain("\"enable_thinking\":false");
        seenBody.Should().NotContain("reasoning_format");
    }

    // --- GenerateTextAsync: max_tokens, температура 0 ---

    [Fact]
    public async Task GenerateTextAsync_ШлётMaxTokensИзПрофиля()
    {
        string? seenBody = null;
        var handler = new CaptureHandler(
            """{"choices":[{"message":{"content":"hello"}}],"usage":{"prompt_tokens":2,"completion_tokens":1}}""",
            req => seenBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult());
        var client = NewClient(new FakeHttpFactory(handler), EnabledConfig());

        var text = await client.GenerateTextAsync("p", model: null, TimeSpan.FromSeconds(5),
            numPredict: 256, numCtx: 4096);

        text.Should().Be("hello");
        seenBody.Should().Contain("\"max_tokens\":256");
        seenBody.Should().Contain("\"temperature\":0");
    }

    [Fact]
    public async Task GenerateTextAsync_ЗаписываетSpend()
    {
        var spend = new CollectingSpend();
        var handler = new FakeHandler("""{"choices":[{"message":{"content":"x"}}],"usage":{"prompt_tokens":7,"completion_tokens":4}}""");
        var client = NewClient(new FakeHttpFactory(handler), EnabledConfig(), spend);

        await client.GenerateTextAsync("p", null, TimeSpan.FromSeconds(5), 100, 4096,
            ownerId: "u1", label: "notes.tags");

        var rec = Assert.Single(spend.Records);
        rec.Provider.Should().Be("llama-server");
        rec.Source.Should().Be(SpendSources.Free);
        rec.InputTokens.Should().Be(7);
        rec.OutputTokens.Should().Be(4);
        rec.OwnerId.Should().Be("u1");
        rec.Label.Should().Be("notes.tags");
    }

    // --- ChatTurnAsync: SSE-стрим, куски по границе предложения, usage ---

    [Fact]
    public async Task ChatTurnAsync_Sse_КускиПоГраницеПредложения_ИUsageИзФинальногоЧанка()
    {
        // SSE: данные разбиты на чанки, финальный чанк несёт usage и пустой choices.
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"Привет, \"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"как дела? \"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"Всё хорошо.\"}}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":7}}\n\n" +
            "data: [DONE]\n\n";
        var handler = new FakeHandler(sse, "text/event-stream");
        var client = NewClient(new FakeHttpFactory(handler), EnabledConfig());

        var chunks = new List<string>();
        var result = await client.ChatTurnAsync(
            new[] { new ChatMsg("user", "hi") },
            model: null,
            timeout: TimeSpan.FromSeconds(5),
            numPredict: 200,
            numCtx: 4096,
            ownerId: null,
            onDelta: async piece => { chunks.Add(piece); await Task.CompletedTask; });

        result.Text.Should().Be("Привет, как дела? Всё хорошо.");
        result.Usage.Should().NotBeNull();
        result.Usage!.InputTokens.Should().Be(11);
        result.Usage.OutputTokens.Should().Be(7);

        // Границы предложений после «?» и «.» должны были вытолкнуть куски.
        chunks.Should().Contain("Привет, как дела? ");
        chunks.Should().Contain("Всё хорошо.");
    }

    [Fact]
    public async Task ChatTurnAsync_БезПотока_ОдинОтветЦеликом()
    {
        var handler = new FakeHandler("""{"choices":[{"message":{"content":"раз"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""");
        var client = NewClient(new FakeHttpFactory(handler), EnabledConfig());

        var result = await client.ChatTurnAsync(
            new[] { new ChatMsg("user", "hi") },
            model: null,
            timeout: TimeSpan.FromSeconds(5),
            numPredict: 100,
            numCtx: 4096,
            ownerId: null,
            onDelta: null);

        result.Text.Should().Be("раз");
        result.Usage!.InputTokens.Should().Be(1);
    }

    // --- отказ: 400 / таймаут → null (фолбэк не ломается) ---

    [Fact]
    public async Task ChatJsonAsync_400_ВозвращаетNull()
    {
        var handler = new FakeHandler("""{"error":"bad schema"}""", status: HttpStatusCode.BadRequest);
        var client = NewClient(new FakeHttpFactory(handler), EnabledConfig());

        var raw = await client.ChatJsonAsync("sys", "user", "json");

        raw.Should().BeNull();
    }

    [Fact]
    public async Task GenerateTextAsync_500_ВозвращаетNull()
    {
        var handler = new FakeHandler("oops", status: HttpStatusCode.InternalServerError);
        var client = NewClient(new FakeHttpFactory(handler), EnabledConfig());

        var raw = await client.GenerateTextAsync("p", null, TimeSpan.FromSeconds(5), 100, 4096);

        raw.Should().BeNull();
    }

    // --- LocalLlmOptions.Read: фолбэк LocalLlm:* → Ollama:* ---

    [Fact]
    public void LocalLlmOptions_ProviderИзЛокалки()
    {
        var cfg = Config(new()
        {
            ["LocalLlm:Provider"] = "llama-server",
            ["LocalLlm:BaseUrl"] = "http://x:8080",
            ["LocalLlm:Model"] = "m",
        });
        var opts = LocalLlmOptions.Read(cfg);
        opts.Provider.Should().Be("llama-server");
        opts.BaseUrl.Should().Be("http://x:8080");
        opts.Model.Should().Be("m");
    }

    [Fact]
    public void LocalLlmOptions_БезЛокалки_ФолбэкНаOllama()
    {
        var cfg = Config(new()
        {
            ["Ollama:BaseUrl"] = "http://ollama:11434",
            ["Ollama:Model"] = "q",
            ["Ollama:TimeoutMs"] = "7777",
        });
        var opts = LocalLlmOptions.Read(cfg);
        opts.Provider.Should().Be("ollama");
        opts.BaseUrl.Should().Be("http://ollama:11434");
        opts.Model.Should().Be("q");
        opts.TimeoutMs.Should().Be(7777);
    }

    [Fact]
    public void LocalLlmOptions_ЛокалкаПеребиваетOllama()
    {
        var cfg = Config(new()
        {
            ["LocalLlm:Provider"] = "llama-server",
            ["LocalLlm:BaseUrl"] = "http://llama:8080",
            ["LocalLlm:Model"] = "new",
            ["Ollama:BaseUrl"] = "http://ollama:11434",
            ["Ollama:Model"] = "old",
        });
        var opts = LocalLlmOptions.Read(cfg);
        opts.Provider.Should().Be("llama-server");
        opts.BaseUrl.Should().Be("http://llama:8080");
        opts.Model.Should().Be("new");
    }

    [Fact]
    public void LocalLlmOptions_НеизвестныйProvider_FallbackНаOllama()
    {
        var cfg = Config(new() { ["LocalLlm:Provider"] = "totally-unknown" });
        var opts = LocalLlmOptions.Read(cfg);
        opts.Provider.Should().Be("ollama");
    }

    [Fact]
    public void LocalLlmOptions_TextModelПусто_БерётModel()
    {
        var cfg = Config(new() { ["LocalLlm:Model"] = "shared" });
        var opts = LocalLlmOptions.Read(cfg);
        opts.TextModel.Should().Be("shared");
    }

    // --- выбор реализации по Provider: фактическая фабрика в Program.cs --
    // Тест самого факта: класс OllamaClient и LlamaServerClient отдают разный ProviderKey.
    [Fact]
    public void РеализацииОтдаютРазныйProviderKey()
    {
        var cfg = EnabledConfig();
        var ollama = new OllamaClient(new NullHttpFactory(), cfg, NullLogger<OllamaClient>.Instance);
        var llama = NewClient(new NullHttpFactory(), cfg);

        ollama.ProviderKey.Should().Be("ollama");
        llama.ProviderKey.Should().Be("llama-server");
    }

    // --- helpers ---

    private sealed class CollectingSpend : ISpendCollector
    {
        public List<SpendRecord> Records { get; } = [];
        public void Record(SpendRecord record) => Records.Add(record);
    }

    private sealed class FakeHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class FakeHandler(
        string body,
        string contentType = "application/json",
        HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            });
    }

    private sealed class CaptureHandler(
        string body,
        Action<HttpRequestMessage> capture,
        string contentType = "application/json",
        HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            capture(request);
            return await Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            });
        }
    }
}
