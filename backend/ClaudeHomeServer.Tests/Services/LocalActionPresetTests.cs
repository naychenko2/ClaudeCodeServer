using ClaudeHomeServer.Tests.Helpers;
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
        d["ModelCatalog:QueryProviderApis"] = "false";
        return TestConfig.Build(d);
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
        // Сильное Small (skill-translate) → слот «слабая»
        Assert.Equal("tier:weak", store.TryGet(LocalActionCatalog.SkillTranslate));
        // Сильное Large (changelog) → слот «средняя»
        Assert.Equal("tier:medium", store.TryGet(LocalActionCatalog.Changelog));
    }

    [Fact]
    public async Task Recommended_OllamaOff_LightGetsTier()
    {
        var (service, store) = Build(new() { ["Ollama:Model"] = "" });
        await service.ApplyAsync(ActionPreset.Recommended);

        // Без локали лёгкое действие получает слот по профилю (Small → слабая), а не local
        Assert.Equal("tier:weak", store.TryGet(LocalActionCatalog.NotesTags));
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
    public async Task FreeOnly_TwoSources_PrefersByConfigOrderAndProfile()
    {
        var cfg = WithFreeModel(new() { ["Ollama:Model"] = "qwen3:14b" });
        cfg["LlmProviders:freellmapi:DisplayName"] = "FreeLLM";
        cfg["LlmProviders:freellmapi:ApiKey"] = "test-key";
        cfg["LlmProviders:freellmapi:AnthropicBaseUrl"] = "http://localhost:3001";
        cfg["LlmProviders:freellmapi:ApiBaseUrl"] = "http://localhost:3001/v1";
        cfg["CheapHttpSources:freellmapi:Provider"] = "freellmapi";
        // Small-профилю хватает fast; Large-профилю нужен smart (окно fast меньше порога Large)
        cfg["CheapHttpSources:freellmapi:Models:0:Id"] = "auto:fast";
        cfg["CheapHttpSources:freellmapi:Models:0:DisplayName"] = "FreeLLM Fast";
        cfg["CheapHttpSources:freellmapi:Models:0:ContextWindow"] = "8192";
        cfg["CheapHttpSources:freellmapi:Models:1:Id"] = "auto:smart";
        cfg["CheapHttpSources:freellmapi:Models:1:DisplayName"] = "FreeLLM Smart";
        cfg["CheapHttpSources:freellmapi:Models:1:ContextWindow"] = "128000";

        var (service, store) = Build(cfg);
        await service.ApplyAsync(ActionPreset.FreeOnly);

        // freellmapi первый в CheapHttpSources → Small (NotesTags) берёт auto:fast
        Assert.Equal(CloudCheapClient.RoutePrefix + "auto:fast", store.TryGet(LocalActionCatalog.NotesTags));
        // Large (Changelog) требует больше окна → auto:smart
        Assert.Equal(CloudCheapClient.RoutePrefix + "auto:smart", store.TryGet(LocalActionCatalog.Changelog));
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
    public async Task Balanced_RoutesBySophistication()
    {
        var (service, store) = Build(WithFreeModel(new()
        {
            ["Ollama:Model"] = "qwen3:4b",
            ["Ollama:BaseUrl"] = "http://localhost:11434",
        }));
        await service.ApplyAsync(ActionPreset.Balanced);

        var direct = CloudCheapClient.RoutePrefix + "nvidia/nemotron:free";
        // Small + лёгкое (теги) → локальная модель
        Assert.Equal(LocalActionOverridesStore.LocalRoute, store.TryGet(LocalActionCatalog.NotesTags));
        // Text + лёгкое (связи) → бесплатная облачная
        Assert.Equal(direct, store.TryGet(LocalActionCatalog.NotesLinks));
        // Large + лёгкое (конспект дня) → слот «средняя»
        Assert.Equal("tier:medium", store.TryGet(LocalActionCatalog.NotesDailySummary));
        // Сильное (DefaultLocal=false, changelog) → слот, никакой локали/бесплатной
        Assert.Equal("tier:medium", store.TryGet(LocalActionCatalog.Changelog));
    }

    [Fact]
    public async Task Balanced_OllamaOff_SmallGetsClaudeTier()
    {
        // Без локали Small честно уходит на слот «слабая», а не local
        var (service, store) = Build(WithFreeModel(new() { ["Ollama:Model"] = "" }));
        await service.ApplyAsync(ActionPreset.Balanced);
        Assert.Equal("tier:weak", store.TryGet(LocalActionCatalog.NotesTags));
    }

    [Fact]
    public async Task Balanced_NoFree_TextGetsClaudeTier()
    {
        // Бесплатная облачная не настроена → Text падает на тир Claude (text = sonnet),
        // а Small всё равно остаётся на локали
        var (service, store) = Build(new()
        {
            ["Ollama:Model"] = "qwen3:4b",
            ["Ollama:BaseUrl"] = "http://localhost:11434",
        });
        await service.ApplyAsync(ActionPreset.Balanced);
        Assert.Equal(LocalActionOverridesStore.LocalRoute, store.TryGet(LocalActionCatalog.NotesTags));
        // Text без free падает на слот по профилю (Text → слабая)
        Assert.Equal("tier:weak", store.TryGet(LocalActionCatalog.NotesLinks));
    }

    [Fact]
    public async Task FreeUnavailable_WhenAggregatorNotConfigured()
    {
        var (service, _) = Build(new() { ["Ollama:Model"] = "qwen3:14b" });
        Assert.False(await service.FreeAvailableAsync());
    }

    [Fact]
    public async Task Tiers_SetsTierSlotsForAll_IncludingAgentic()
    {
        var (service, store) = Build(new() { ["Ollama:Model"] = "" });
        await service.ApplyAsync(ActionPreset.Tiers);

        // Агентное место с явным Tier=Strong
        Assert.Equal("tier:strong", store.TryGet(LocalActionCatalog.ChatNew));
        // Агентное место с явным Tier=Medium
        Assert.Equal("tier:medium", store.TryGet(LocalActionCatalog.SubagentConsultant));
        // Фоновое Small (DefaultLocal) → weak
        Assert.Equal("tier:weak", store.TryGet(LocalActionCatalog.NotesTags));
        // Фоновое Large (DefaultLocal=false) → medium
        Assert.Equal("tier:medium", store.TryGet(LocalActionCatalog.Changelog));
    }

    [Fact]
    public async Task TiersLocal_DefaultLocalBackgroundGetsLocal_AgenticGetsTier()
    {
        var (service, store) = Build(new()
        {
            ["Ollama:Model"] = "qwen3:14b",
            ["Ollama:BaseUrl"] = "http://localhost:11434",
        });
        await service.ApplyAsync(ActionPreset.TiersLocal);

        // Лёгкое фоновое (DefaultLocal) → локаль
        Assert.Equal(LocalActionOverridesStore.LocalRoute, store.TryGet(LocalActionCatalog.NotesTags));
        // Сильное фоновое (DefaultLocal=false) → tier medium
        Assert.Equal("tier:medium", store.TryGet(LocalActionCatalog.Changelog));
        // Агентное — всегда tier, never local
        Assert.Equal("tier:strong", store.TryGet(LocalActionCatalog.ChatNew));
        Assert.Equal("tier:medium", store.TryGet(LocalActionCatalog.SubagentConsultant));
    }

    [Fact]
    public async Task TiersLocal_OllamaOff_FallsBackToTiers()
    {
        var (service, store) = Build(new() { ["Ollama:Model"] = "" });
        await service.ApplyAsync(ActionPreset.TiersLocal);

        // Без Ollama поведение совпадает с Tiers
        Assert.Equal("tier:weak", store.TryGet(LocalActionCatalog.NotesTags));
        Assert.Equal("tier:medium", store.TryGet(LocalActionCatalog.Changelog));
        Assert.Equal("tier:strong", store.TryGet(LocalActionCatalog.ChatNew));
    }
}
