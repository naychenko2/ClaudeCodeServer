using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Мульти-источниковый прямой HTTP-адаптер для бесплатных one-shot действий: CloudCheapClient.
public class CloudCheapClientTests
{
    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class CaptureLogger : ILogger<CloudCheapClient>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private static IConfiguration Config(Dictionary<string, string?> d)
    {
        d.TryAdd("ClaudeUserProfileDir", TestConfig.EmptyClaudeProfileDir());
        return TestConfig.Build(d);
    }

    private static CloudCheapClient Build(Dictionary<string, string?> cfg, ILogger<CloudCheapClient>? logger = null)
    {
        var config = Config(cfg);
        var providers = new LlmProviderRegistry(config);
        return new CloudCheapClient(new NullHttpFactory(), config, providers,
            logger ?? NullLogger<CloudCheapClient>.Instance);
    }

    private static Dictionary<string, string?> WithOpenRouter(Dictionary<string, string?> cfg)
    {
        cfg["OpenRouter:Provider"] = "openrouter";
        cfg["LlmProviders:openrouter:DisplayName"] = "OpenRouter";
        cfg["LlmProviders:openrouter:ApiKey"] = "test-key";
        cfg["LlmProviders:openrouter:AnthropicBaseUrl"] = "https://openrouter.ai/api/v1";
        cfg["LlmProviders:openrouter:ApiBaseUrl"] = "https://openrouter.ai/api/v1";
        cfg["OpenRouter:DirectModels:0:Id"] = "nvidia/nemotron:free";
        cfg["OpenRouter:DirectModels:0:DisplayName"] = "Nemotron";
        cfg["OpenRouter:DirectModels:0:ContextWindow"] = "1000000";
        return cfg;
    }

    private static Dictionary<string, string?> WithFreeLlmApi(Dictionary<string, string?> cfg)
    {
        cfg["LlmProviders:freellmapi:DisplayName"] = "FreeLLM";
        cfg["LlmProviders:freellmapi:ApiKey"] = "test-key";
        cfg["LlmProviders:freellmapi:AnthropicBaseUrl"] = "http://localhost:3001";
        cfg["LlmProviders:freellmapi:ApiBaseUrl"] = "http://localhost:3001/v1";
        cfg["CheapHttpSources:freellmapi:Provider"] = "freellmapi";
        cfg["CheapHttpSources:freellmapi:Models:0:Id"] = "auto:fast";
        cfg["CheapHttpSources:freellmapi:Models:0:DisplayName"] = "FreeLLM Fast";
        cfg["CheapHttpSources:freellmapi:Models:0:ContextWindow"] = "32000";
        cfg["CheapHttpSources:freellmapi:Models:1:Id"] = "auto:smart";
        cfg["CheapHttpSources:freellmapi:Models:1:DisplayName"] = "FreeLLM Smart";
        cfg["CheapHttpSources:freellmapi:Models:1:ContextWindow"] = "128000";
        return cfg;
    }

    // OpenAI-compatible direct-источники (см. appsettings.json CheapHttpSources) — endpoint-пути
    // и id моделей согласованы с конфигом: {ApiBaseUrl}/chat/completions.
    private static Dictionary<string, string?> WithDeepSeek(Dictionary<string, string?> cfg)
    {
        cfg["LlmProviders:deepseek:DisplayName"] = "DeepSeek";
        cfg["LlmProviders:deepseek:ApiKey"] = "test-key";
        cfg["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.com/anthropic";
        cfg["LlmProviders:deepseek:ApiBaseUrl"] = "https://api.deepseek.com";
        cfg["CheapHttpSources:deepseek:Provider"] = "deepseek";
        cfg["CheapHttpSources:deepseek:Models:0:Id"] = "deepseek-v4-flash";
        cfg["CheapHttpSources:deepseek:Models:0:DisplayName"] = "DeepSeek Flash";
        cfg["CheapHttpSources:deepseek:Models:0:ContextWindow"] = "1000000";
        cfg["CheapHttpSources:deepseek:Models:1:Id"] = "deepseek-v4-pro";
        cfg["CheapHttpSources:deepseek:Models:1:DisplayName"] = "DeepSeek Pro";
        cfg["CheapHttpSources:deepseek:Models:1:ContextWindow"] = "1000000";
        return cfg;
    }

    private static Dictionary<string, string?> WithGlm(Dictionary<string, string?> cfg)
    {
        cfg["LlmProviders:glm:DisplayName"] = "GLM";
        cfg["LlmProviders:glm:ApiKey"] = "test-key";
        cfg["LlmProviders:glm:AnthropicBaseUrl"] = "https://api.z.ai/api/anthropic";
        cfg["LlmProviders:glm:ApiBaseUrl"] = "https://api.z.ai/api/paas/v4";
        cfg["CheapHttpSources:glm:Provider"] = "glm";
        cfg["CheapHttpSources:glm:Models:0:Id"] = "glm-5.2";
        cfg["CheapHttpSources:glm:Models:0:DisplayName"] = "GLM 5.2";
        cfg["CheapHttpSources:glm:Models:0:ContextWindow"] = "200000";
        cfg["CheapHttpSources:glm:Models:1:Id"] = "glm-4.7";
        cfg["CheapHttpSources:glm:Models:1:DisplayName"] = "GLM 4.7";
        cfg["CheapHttpSources:glm:Models:1:ContextWindow"] = "200000";
        cfg["CheapHttpSources:glm:Models:2:Id"] = "glm-4.5-air";
        cfg["CheapHttpSources:glm:Models:2:DisplayName"] = "GLM 4.5 Air";
        cfg["CheapHttpSources:glm:Models:2:ContextWindow"] = "128000";
        return cfg;
    }

    private static Dictionary<string, string?> WithKimi(Dictionary<string, string?> cfg)
    {
        cfg["LlmProviders:kimi:DisplayName"] = "Kimi";
        cfg["LlmProviders:kimi:ApiKey"] = "test-key";
        cfg["LlmProviders:kimi:AnthropicBaseUrl"] = "https://api.kimi.com/coding";
        cfg["LlmProviders:kimi:ApiBaseUrl"] = "https://api.kimi.com/coding/v1";
        cfg["CheapHttpSources:kimi:Provider"] = "kimi";
        cfg["CheapHttpSources:kimi:Models:0:Id"] = "kimi-k3";
        cfg["CheapHttpSources:kimi:Models:0:DisplayName"] = "Kimi K3";
        cfg["CheapHttpSources:kimi:Models:0:ContextWindow"] = "1048576";
        cfg["CheapHttpSources:kimi:Models:1:Id"] = "kimi-k2.6";
        cfg["CheapHttpSources:kimi:Models:1:DisplayName"] = "Kimi K2.6";
        cfg["CheapHttpSources:kimi:Models:1:ContextWindow"] = "262144";
        cfg["CheapHttpSources:kimi:Models:2:Id"] = "kimi-k2.7-code-highspeed";
        cfg["CheapHttpSources:kimi:Models:2:DisplayName"] = "Kimi K2.7 Code (highspeed)";
        cfg["CheapHttpSources:kimi:Models:2:ContextWindow"] = "262144";
        return cfg;
    }

    private static Dictionary<string, string?> WithMinimax(Dictionary<string, string?> cfg)
    {
        cfg["LlmProviders:minimax:DisplayName"] = "MiniMax";
        cfg["LlmProviders:minimax:ApiKey"] = "test-key";
        cfg["LlmProviders:minimax:AnthropicBaseUrl"] = "https://api.minimax.io/anthropic";
        cfg["LlmProviders:minimax:ApiBaseUrl"] = "https://api.minimax.io/v1";
        cfg["CheapHttpSources:minimax:Provider"] = "minimax";
        cfg["CheapHttpSources:minimax:Models:0:Id"] = "MiniMax-M3";
        cfg["CheapHttpSources:minimax:Models:0:DisplayName"] = "MiniMax M3";
        cfg["CheapHttpSources:minimax:Models:0:ContextWindow"] = "1048576";
        cfg["CheapHttpSources:minimax:Models:1:Id"] = "MiniMax-M2.7";
        cfg["CheapHttpSources:minimax:Models:1:DisplayName"] = "MiniMax M2.7";
        cfg["CheapHttpSources:minimax:Models:1:ContextWindow"] = "1048576";
        cfg["CheapHttpSources:minimax:Models:2:Id"] = "MiniMax-M2.7-highspeed";
        cfg["CheapHttpSources:minimax:Models:2:DisplayName"] = "MiniMax M2.7 Highspeed";
        cfg["CheapHttpSources:minimax:Models:2:ContextWindow"] = "1048576";
        return cfg;
    }

    [Fact]
    public void MultiSource_ResolvesModelToCorrectSource()
    {
        var client = Build(WithFreeLlmApi(WithOpenRouter(new())));

        Assert.Equal("freellmapi", client.ResolveSource("direct:auto:fast")?.Key);
        Assert.Equal("freellmapi", client.ResolveSource("direct:auto:smart")?.Key);
        Assert.Equal("openrouter", client.ResolveSource("direct:nvidia/nemotron:free")?.Key);
    }

    [Fact]
    public void MultiSource_ResolvesNewSourcesToCorrectSource()
    {
        var client = Build(WithMinimax(WithKimi(WithGlm(WithDeepSeek(WithFreeLlmApi(WithOpenRouter(new())))))));

        Assert.Equal("deepseek", client.ResolveSource("direct:deepseek-v4-flash")?.Key);
        Assert.Equal("deepseek", client.ResolveSource("direct:deepseek-v4-pro")?.Key);
        Assert.Equal("glm", client.ResolveSource("direct:glm-5.2")?.Key);
        Assert.Equal("glm", client.ResolveSource("direct:glm-4.7")?.Key);
        Assert.Equal("glm", client.ResolveSource("direct:glm-4.5-air")?.Key);
        Assert.Equal("kimi", client.ResolveSource("direct:kimi-k3")?.Key);
        Assert.Equal("kimi", client.ResolveSource("direct:kimi-k2.6")?.Key);
        Assert.Equal("kimi", client.ResolveSource("direct:kimi-k2.7-code-highspeed")?.Key);
        Assert.Equal("minimax", client.ResolveSource("direct:MiniMax-M3")?.Key);
        Assert.Equal("minimax", client.ResolveSource("direct:MiniMax-M2.7")?.Key);
        Assert.Equal("minimax", client.ResolveSource("direct:MiniMax-M2.7-highspeed")?.Key);
    }

    [Fact]
    public void Collision_FirstSourceWins_AndLogsWarning()
    {
        var cfg = WithFreeLlmApi(WithOpenRouter(new()));
        // Коллизия: одинаковый id в двух источниках. Первый по порядку в конфиге — openrouter (legacy),
        // freellmapi идёт позже, поэтому openrouter выигрывает.
        cfg["CheapHttpSources:freellmapi:Models:2:Id"] = "nvidia/nemotron:free";
        cfg["CheapHttpSources:freellmapi:Models:2:DisplayName"] = "Nemotron Clone";
        cfg["CheapHttpSources:freellmapi:Models:2:ContextWindow"] = "1000000";

        var logger = new CaptureLogger();
        var client = Build(cfg, logger);

        var source = client.ResolveSource("direct:nvidia/nemotron:free");
        Assert.Equal("openrouter", source?.Key);
        Assert.Contains("openrouter", logger.Warnings[0]);
        Assert.Contains("freellmapi", logger.Warnings[0]);
        Assert.Contains("nvidia/nemotron:free", logger.Warnings[0]);
    }

    [Fact]
    public void Collision_NewSourceVsExisting_FirstSourceWins_AndLogsWarning()
    {
        var cfg = WithDeepSeek(WithOpenRouter(new()));
        // Коллизия: id нового источника deepseek совпадает с id существующего legacy openrouter.
        // Legacy openrouter добавляется в _sources ПЕРВЫМ (до цикла по CheapHttpSources),
        // поэтому он выигрывает независимо от порядка секции CheapHttpSources в конфиге.
        cfg["CheapHttpSources:deepseek:Models:2:Id"] = "nvidia/nemotron:free";
        cfg["CheapHttpSources:deepseek:Models:2:DisplayName"] = "Nemotron Clone";
        cfg["CheapHttpSources:deepseek:Models:2:ContextWindow"] = "1000000";

        var logger = new CaptureLogger();
        var client = Build(cfg, logger);

        var source = client.ResolveSource("direct:nvidia/nemotron:free");
        Assert.Equal("openrouter", source?.Key);
        Assert.Contains("openrouter", logger.Warnings[0]);
        Assert.Contains("deepseek", logger.Warnings[0]);
        Assert.Contains("nvidia/nemotron:free", logger.Warnings[0]);
    }

    [Theory]
    [InlineData("deepseek-v4-flash", "https://api.deepseek.com/chat/completions")]
    [InlineData("glm-5.2", "https://api.z.ai/api/paas/v4/chat/completions")]
    [InlineData("kimi-k3", "https://api.kimi.com/coding/v1/chat/completions")]
    [InlineData("MiniMax-M3", "https://api.minimax.io/v1/chat/completions")]
    public async Task GenerateDetailedAsync_BuildsRequestAgainstSourceEndpoint(string modelId, string expectedUrl)
    {
        var cfg = WithMinimax(WithKimi(WithGlm(WithDeepSeek(new()))));
        var config = TestConfig.Build(cfg);
        var providers = new LlmProviderRegistry(config);
        var stub = new StubHttpFactory(OkJson);
        var client = new CloudCheapClient(stub, config, providers, NullLogger<CloudCheapClient>.Instance);

        await client.GenerateDetailedAsync($"direct:{modelId}", "p", TimeSpan.FromSeconds(5), maxTokens: 128);

        Assert.Equal(expectedUrl, stub.Handler.LastRequestUri?.ToString());
    }

    private const string OkJson = """
        {
          "choices": [{
            "index": 0,
            "finish_reason": "stop",
            "message": {"role": "assistant", "content": "ok"}
          }],
          "usage": {"prompt_tokens": 1, "completion_tokens": 1}
        }
        """;

    // Без per-source override температуры CloudCheapClient шлёт temperature=0 — детерминированность
    // фоновых one-shot действий (теги, сводки, JSON-парсинг). Проверяем на нескольких источниках,
    // включая kimi без override: пока CheapHttpSources:kimi:Temperature не задан — тоже 0.
    [Theory]
    [InlineData("deepseek-v4-flash")]
    [InlineData("glm-5.2")]
    [InlineData("MiniMax-M3")]
    [InlineData("kimi-k3")]
    public async Task GenerateDetailedAsync_DefaultTemperature_IsZero(string modelId)
    {
        var cfg = WithMinimax(WithKimi(WithGlm(WithDeepSeek(new()))));
        var config = TestConfig.Build(cfg);
        var providers = new LlmProviderRegistry(config);
        var stub = new StubHttpFactory(OkJson);
        var client = new CloudCheapClient(stub, config, providers, NullLogger<CloudCheapClient>.Instance);

        await client.GenerateDetailedAsync($"direct:{modelId}", "p", TimeSpan.FromSeconds(5), maxTokens: 128);

        Assert.Contains("\"temperature\":0", stub.Handler.LastRequestBody);
    }

    // kimi на всех моделях каталога принимает ТОЛЬКО temperature=1 и падает 400 при 0
    // (прод 2026-08-12). CheapHttpSources:kimi:Temperature=1 обязан попасть в тело запроса.
    [Theory]
    [InlineData("kimi-k3")]
    [InlineData("kimi-k2.6")]
    [InlineData("kimi-k2.7-code-highspeed")]
    public async Task GenerateDetailedAsync_KimiTemperatureOverride_SendsOne(string modelId)
    {
        var cfg = WithKimi(new());
        cfg["CheapHttpSources:kimi:Temperature"] = "1";
        var config = TestConfig.Build(cfg);
        var providers = new LlmProviderRegistry(config);
        var stub = new StubHttpFactory(OkJson);
        var client = new CloudCheapClient(stub, config, providers, NullLogger<CloudCheapClient>.Instance);

        await client.GenerateDetailedAsync($"direct:{modelId}", "p", TimeSpan.FromSeconds(5), maxTokens: 128);

        Assert.Contains("\"temperature\":1", stub.Handler.LastRequestBody);
        Assert.DoesNotContain("\"temperature\":0", stub.Handler.LastRequestBody);
    }

    [Fact]
    public void LegacyFallback_WhenCheapHttpSourcesEmpty()
    {
        var client = Build(WithOpenRouter(new()));

        Assert.Single(client.Sources);
        Assert.Equal("openrouter", client.Sources[0].Key);
        Assert.Equal("openrouter", client.ResolveSource("direct:nvidia/nemotron:free")?.Key);
    }

    [Fact]
    public void ResolveSource_UnknownModel_FallsBackToFirstConfiguredSource()
    {
        var client = Build(WithFreeLlmApi(WithOpenRouter(new())));
        var source = client.ResolveSource("direct:unknown-model");
        Assert.NotNull(source);
        Assert.Equal("openrouter", source.Key); // legacy openrouter — первый configured источник
    }

    // Обрыв по лимиту вывода (прод 2026-08-05): провайдер кладёт finish_reason="length" в choice,
    // контент в ответе обрезан. CloudCheapClient обязан отдать Truncated=true, иначе планировщик
    // «Командной реализации» спутает обрез с таймаутом. Тест идёт через подставной HTTP — JSON
    // ровно как у OpenRouter-compatible источника с content=обрезанный фрагмент и finish_reason.
    [Fact]
    public async Task GenerateDetailedAsync_FinishReasonLength_Truncated()
    {
        var truncated = """
            {
              "choices": [{
                "index": 0,
                "finish_reason": "length",
                "message": {"role": "assistant", "content": "{\"summary\":\"Экспорт\",\"subtasks\":[{\"title\":\"A\""}
              }],
              "usage": {"prompt_tokens": 7, "completion_tokens": 1024}
            }
            """;
        var config = TestConfig.Build(WithOpenRouter(new()));
        var providers = new LlmProviderRegistry(config);
        var capture = new CaptureLogger();
        var client = new CloudCheapClient(new StubHttpFactory(truncated), config, providers, capture);

        var result = await client.GenerateDetailedAsync("direct:nvidia/nemotron:free", "p",
            TimeSpan.FromSeconds(5), maxTokens: 1024, ownerId: "u", label: "team-implement-plan");

        Assert.True(result.Truncated, "finish_reason=length даёт Truncated=true — иначе симптом неотличим от таймаута");
        Assert.NotNull(result.Text);
        Assert.Contains("обрез", string.Join(" | ", capture.Warnings), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateDetailedAsync_FinishReasonStop_NotTruncated()
    {
        var ok = """
            {
              "choices": [{
                "index": 0,
                "finish_reason": "stop",
                "message": {"role": "assistant", "content": "{\"summary\":\"X\"}"}
              }],
              "usage": {"prompt_tokens": 3, "completion_tokens": 4}
            }
            """;
        var config = TestConfig.Build(WithOpenRouter(new()));
        var providers = new LlmProviderRegistry(config);
        var client = new CloudCheapClient(new StubHttpFactory(ok), config, providers, new CaptureLogger());

        var result = await client.GenerateDetailedAsync("direct:nvidia/nemotron:free", "p",
            TimeSpan.FromSeconds(5), maxTokens: 1024);

        Assert.False(result.Truncated);
        Assert.Equal("{\"summary\":\"X\"}", result.Text);
    }

    // Подставной HTTP для unit-тестов CloudCheapClient: отдаёт заготовленный JSON.
    // Handler хранит последний RequestUri — используется тестами, проверяющими,
    // на какой endpoint ушёл запрос (остальные тесты его игнорируют).
    private sealed class StubHttpFactory(string json) : IHttpClientFactory
    {
        public StubHandler Handler { get; } = new(json);
        public HttpClient CreateClient(string name) => new(Handler);
    }

    private sealed class StubHandler(string json) : System.Net.Http.HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
