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

    [Fact]
    public void MultiSource_ResolvesModelToCorrectSource()
    {
        var client = Build(WithFreeLlmApi(WithOpenRouter(new())));

        Assert.Equal("freellmapi", client.ResolveSource("direct:auto:fast")?.Key);
        Assert.Equal("freellmapi", client.ResolveSource("direct:auto:smart")?.Key);
        Assert.Equal("openrouter", client.ResolveSource("direct:nvidia/nemotron:free")?.Key);
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

    // Подставной HTTP для unit-тестов CloudCheapClient: отдаёт заготовленный JSON
    // без проверки URL, заголовков и тела. Достаточно для проверки парсинга ответа.
    private sealed class StubHttpFactory(string json) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(json));
    }

    private sealed class StubHandler(string json) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
