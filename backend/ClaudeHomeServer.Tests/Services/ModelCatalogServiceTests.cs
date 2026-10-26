using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Каталог моделей: группировка прямых (direct:) моделей по виртуальным провайдерам {source}-direct.
public class ModelCatalogServiceTests
{
    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static IConfiguration Config(Dictionary<string, string?> d)
    {
        d["ModelCatalog:QueryCli"] = "false";
        d["ModelCatalog:QueryProviderApis"] = "false";
        d.TryAdd("ClaudeUserProfileDir", TestConfig.EmptyClaudeProfileDir());
        return TestConfig.Build(d);
    }

    private static ModelCatalogService Build(Dictionary<string, string?> cfg)
    {
        var config = Config(cfg);
        var providers = new LlmProviderRegistry(config);
        return new ModelCatalogService(providers, new NullHttpFactory(), config);
    }

    private static Dictionary<string, string?> WithProviders(Dictionary<string, string?> cfg)
    {
        cfg["LlmProviders:openrouter:DisplayName"] = "OpenRouter";
        cfg["LlmProviders:openrouter:ApiKey"] = "test-key";
        cfg["LlmProviders:openrouter:AnthropicBaseUrl"] = "https://openrouter.ai/api/v1";
        cfg["LlmProviders:openrouter:ApiBaseUrl"] = "https://openrouter.ai/api/v1";
        cfg["LlmProviders:freellmapi:DisplayName"] = "FreeLLM";
        cfg["LlmProviders:freellmapi:ApiKey"] = "test-key";
        cfg["LlmProviders:freellmapi:AnthropicBaseUrl"] = "http://localhost:3001";
        cfg["LlmProviders:freellmapi:ApiBaseUrl"] = "http://localhost:3001/v1";
        return cfg;
    }

    [Fact]
    public async Task DirectModels_GroupedByVirtualProvider()
    {
        var cfg = WithProviders(new());
        cfg["OpenRouter:DirectModels:0:Id"] = "nvidia/nemotron:free";
        cfg["OpenRouter:DirectModels:0:DisplayName"] = "Nemotron";
        cfg["OpenRouter:DirectModels:0:ContextWindow"] = "1000000";
        cfg["CheapHttpSources:freellmapi:Provider"] = "freellmapi";
        cfg["CheapHttpSources:freellmapi:Models:0:Id"] = "auto:fast";
        cfg["CheapHttpSources:freellmapi:Models:0:DisplayName"] = "FreeLLM Fast";
        cfg["CheapHttpSources:freellmapi:Models:0:ContextWindow"] = "32000";

        var service = Build(cfg);
        var models = await service.GetModelsAsync();
        var byProvider = models.ToLookup(m => m.Provider);

        Assert.Contains("openrouter-direct", byProvider.Select(g => g.Key));
        Assert.Contains("freellmapi-direct", byProvider.Select(g => g.Key));
        Assert.Contains("direct:nvidia/nemotron:free", byProvider["openrouter-direct"].Select(m => m.Value));
        Assert.Contains("direct:auto:fast", byProvider["freellmapi-direct"].Select(m => m.Value));
    }

    [Fact]
    public async Task DisabledSource_DirectModelsSkipped()
    {
        var cfg = WithProviders(new());
        cfg["LlmProviders:freellmapi:ApiKey"] = ""; // выключен
        cfg["OpenRouter:DirectModels:0:Id"] = "nvidia/nemotron:free";
        cfg["OpenRouter:DirectModels:0:DisplayName"] = "Nemotron";
        cfg["OpenRouter:DirectModels:0:ContextWindow"] = "1000000";
        cfg["CheapHttpSources:freellmapi:Provider"] = "freellmapi";
        cfg["CheapHttpSources:freellmapi:Models:0:Id"] = "auto:fast";
        cfg["CheapHttpSources:freellmapi:Models:0:DisplayName"] = "FreeLLM Fast";
        cfg["CheapHttpSources:freellmapi:Models:0:ContextWindow"] = "32000";

        var service = Build(cfg);
        var models = await service.GetModelsAsync();

        Assert.All(models, m => Assert.NotEqual("freellmapi-direct", m.Provider));
        Assert.Contains(models, m => m.Provider == "openrouter-direct");
    }

    // Ценовой класс провайдера (/api/models → providers[].costTier): локальный движок →
    // бесплатно, claude (и подписки) → дорого, прочие облачные → дёшево. Правило — в Core
    // (SpendCostTiers), здесь только прокладка каталога.
    [Fact]
    public void CostTier_ПоПровайдеру_ЛокальныйДешёвыйДорогой()
    {
        var cfg = new Dictionary<string, string?>
        {
            ["LlmProviders:glm:ApiKey"] = "test-key",
            ["LlmProviders:glm:AnthropicBaseUrl"] = "https://api.z.ai/api/anthropic",
            ["LlmProviders:local-qwen:IsLocal"] = "true",
            ["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:8000",
        };
        var config = Config(cfg);
        var providers = new LlmProviderRegistry(config);
        var service = new ModelCatalogService(providers, new NullHttpFactory(), config);

        service.CostTierOf(providers.GetByKey("glm")!).Should().Be("cheap", "облачный сторонний → дёшево");
        service.CostTierOf(providers.GetByKey("local-qwen")!).Should().Be("free", "локальный движок → бесплатно");
        service.CostTierOf(new LlmProviderConfig { Key = "claude" }).Should().Be("expensive", "claude → дорого");
        service.CostTierOf(new LlmProviderConfig { Key = "sub-plus" }).Should().Be("expensive",
            "подписка claude (sub-*) → тот же дорогой класс");
    }

    // Провайдер с пустым ключом (цена неизвестна) в каталоге → «дорого», как и его трата
    // в аналитике: правило «цена неизвестна — занижать нельзя» одно на оба места.
    // Мутация «в OfProvider пустой ключ → cheap» → краснеет.
    [Fact]
    public void CostTier_ПустойКлючПровайдера_Дорого()
    {
        var config = Config(new Dictionary<string, string?>());
        var providers = new LlmProviderRegistry(config);
        var service = new ModelCatalogService(providers, new NullHttpFactory(), config);

        service.CostTierOf(new LlmProviderConfig { Key = "" }).Should().Be("expensive",
            "цена неизвестна → занижать нельзя, как и в аналитике");
    }

    // «:free» — признак модели, а не провайдера: класс провайдера в каталоге остаётся
    // «дёшево» (у провайдера с такими моделями есть и платные), бесплатность такой модели
    // видна в тратах (столбик «Бесплатные»). Расхождение уровней осознанное.
    [Fact]
    public void CostTier_ПровайдерСБесплатнымиМоделями_ВКаталогеДёшевый()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["LlmProviders:openrouter:ApiKey"] = "test-key",
            ["LlmProviders:openrouter:AnthropicBaseUrl"] = "https://openrouter.ai/api/v1",
            ["LlmProviders:openrouter:Models:0:Id"] = "nvidia/nemotron:free",
        });
        var providers = new LlmProviderRegistry(config);
        var service = new ModelCatalogService(providers, new NullHttpFactory(), config);

        service.CostTierOf(providers.GetByKey("openrouter")!).Should().Be("cheap",
            "класс ПРОВАЙДЕРА: часть моделей бесплатна, но не все → дёшево");
    }
}
