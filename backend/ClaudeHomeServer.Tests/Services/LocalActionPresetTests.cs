using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Пресеты автоподбора исполнителя фоновых действий: LocalActionPresetService.
public class LocalActionPresetTests
{
    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // Конфиг с временным DataPath (стор оверрайдов пишет файл рядом) и выключенным опросом
    // claude CLI — каталог моделей не спавнит настоящий процесс.
    private static IConfiguration Config(Dictionary<string, string?> d)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        d["DataPath"] = Path.Combine(dir, "projects.json");
        d["ModelCatalog:QueryCli"] = "false";
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }

    private static (LocalActionPresetService Service, LocalActionOverridesStore Store) Build(
        Dictionary<string, string?> cfg)
    {
        var config = Config(cfg);
        var http = new NullHttpFactory();
        var providers = new LlmProviderRegistry(config);
        var ollama = new OllamaClient(http, config, NullLogger<OllamaClient>.Instance);
        var store = new LocalActionOverridesStore(config, NullLogger<LocalActionOverridesStore>.Instance);
        var router = new LocalActionRouter(ollama, store, config, NullLogger<LocalActionRouter>.Instance);
        var models = new ModelCatalogService(providers, http, config);
        var service = new LocalActionPresetService(store, router, ollama, models, config,
            NullLogger<LocalActionPresetService>.Instance);
        return (service, store);
    }

    // Настроенный агрегатор + одна бесплатная прямая модель (широкое окно) в каталоге
    private static Dictionary<string, string?> WithFreeModel(Dictionary<string, string?> cfg)
    {
        cfg["OpenRouter:Provider"] = "openrouter";
        cfg["LlmProviders:openrouter:DisplayName"] = "OpenRouter";
        cfg["LlmProviders:openrouter:ApiKey"] = "test-key";
        // Enabled провайдера требует и ApiKey, и AnthropicBaseUrl
        cfg["LlmProviders:openrouter:AnthropicBaseUrl"] = "https://openrouter.ai/api/v1";
        cfg["LlmProviders:openrouter:ApiBaseUrl"] = "https://openrouter.ai/api/v1";
        cfg["OpenRouter:DirectModels:0:Id"] = "nvidia/nemotron:free";
        cfg["OpenRouter:DirectModels:0:DisplayName"] = "Nemotron";
        cfg["OpenRouter:DirectModels:0:ContextWindow"] = "1000000";
        return cfg;
    }

    [Fact]
    public async Task Recommended_OllamaOn_LightLocal_StrongTier()
    {
        var (service, store) = Build(new() { ["Ollama:Model"] = "qwen3:14b", ["Ollama:BaseUrl"] = "http://localhost:11434" });
        await service.ApplyAsync(ActionPreset.Recommended);

        // Лёгкое (DefaultLocal=true) → локаль
        Assert.Equal(LocalActionOverridesStore.LocalRoute, store.TryGet(LocalActionCatalog.NotesTags));
        // Сильное Small (skill-translate) → тир small = haiku
        Assert.Equal("haiku", store.TryGet(LocalActionCatalog.SkillTranslate));
        // Сильное Large (changelog) → тир large = sonnet
        Assert.Equal("sonnet", store.TryGet(LocalActionCatalog.Changelog));
    }

    [Fact]
    public async Task Recommended_OllamaOff_LightGetsTier()
    {
        var (service, store) = Build(new() { ["Ollama:Model"] = "" });
        await service.ApplyAsync(ActionPreset.Recommended);

        // Без локали лёгкое действие получает тир Claude по профилю (Small → haiku), а не local
        Assert.Equal("haiku", store.TryGet(LocalActionCatalog.NotesTags));
    }

    [Fact]
    public async Task Recommended_RespectsConfiguredTiers()
    {
        var (service, store) = Build(new()
        {
            ["Ollama:Model"] = "",
            ["Recommended:ClaudeTiers:large"] = "opus",
        });
        await service.ApplyAsync(ActionPreset.Recommended);
        Assert.Equal("opus", store.TryGet(LocalActionCatalog.Changelog));
    }

    [Fact]
    public async Task FreeOnly_AllGetDirectModel()
    {
        var (service, store) = Build(WithFreeModel(new() { ["Ollama:Model"] = "qwen3:14b" }));
        Assert.True(await service.FreeAvailableAsync());

        await service.ApplyAsync(ActionPreset.FreeOnly);
        var direct = CloudCheapClient.RoutePrefix + "nvidia/nemotron:free";
        // И лёгкие, и сильные — на бесплатную облачную (никаких local/claude)
        Assert.Equal(direct, store.TryGet(LocalActionCatalog.NotesTags));
        Assert.Equal(direct, store.TryGet(LocalActionCatalog.Changelog));
    }

    [Fact]
    public async Task LocalFirst_LightLocal_StrongDirect()
    {
        var (service, store) = Build(WithFreeModel(new() { ["Ollama:Model"] = "qwen3:14b" }));
        await service.ApplyAsync(ActionPreset.LocalFirst);

        var direct = CloudCheapClient.RoutePrefix + "nvidia/nemotron:free";
        // Лёгкое → локаль; сильное → бесплатная облачная (не Claude)
        Assert.Equal(LocalActionOverridesStore.LocalRoute, store.TryGet(LocalActionCatalog.NotesTags));
        Assert.Equal(direct, store.TryGet(LocalActionCatalog.Changelog));
    }

    [Fact]
    public async Task FreeUnavailable_WhenAggregatorNotConfigured()
    {
        var (service, _) = Build(new() { ["Ollama:Model"] = "qwen3:14b" });
        Assert.False(await service.FreeAvailableAsync());
    }

    [Fact]
    public async Task PreferredFree_PicksListedModel()
    {
        var cfg = WithFreeModel(new() { ["Ollama:Model"] = "" });
        // Вторая модель — предпочитаемая; окно достаточно для любого профиля
        cfg["OpenRouter:DirectModels:1:Id"] = "poolside/laguna:free";
        cfg["OpenRouter:DirectModels:1:DisplayName"] = "Laguna";
        cfg["OpenRouter:DirectModels:1:ContextWindow"] = "262144";
        cfg["OpenRouter:PreferredFree:0"] = "poolside/laguna:free";
        var (service, store) = Build(cfg);

        await service.ApplyAsync(ActionPreset.FreeOnly);
        Assert.Equal(CloudCheapClient.RoutePrefix + "poolside/laguna:free",
            store.TryGet(LocalActionCatalog.NotesTags));
    }
}
