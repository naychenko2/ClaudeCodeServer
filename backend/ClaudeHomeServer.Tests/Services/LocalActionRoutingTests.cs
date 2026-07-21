using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Маршрутизация фоновых действий локаль(Ollama)/claude: LocalActionRouter + CheapTextRunner.
public class LocalActionRoutingTests
{
    private static IConfiguration Config(Dictionary<string, string?> d) =>
        new ConfigurationBuilder().AddInMemoryCollection(d).Build();

    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static OllamaClient Ollama(IConfiguration config) =>
        new(new NullHttpFactory(), config, NullLogger<OllamaClient>.Instance);

    private static LocalActionRouter Router(Dictionary<string, string?> cfg)
    {
        var config = Config(cfg);
        return new LocalActionRouter(Ollama(config), config, NullLogger<LocalActionRouter>.Instance);
    }

    // Фейковый claude-раннер: помечает ответ, чтобы отличить claude-путь от локали
    private sealed class FakeOneShot : IOneShotRunner
    {
        public string? NormalizeModel(string? model) => model;
        public Task<string> RunAsync(string prompt, string? model = null, TimeSpan? timeout = null,
            CancellationToken ct = default, string? ownerId = null, string? effort = null) =>
            Task.FromResult($"CLAUDE[{model}]:{prompt}");
        public Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null, TimeSpan? timeout = null,
            CancellationToken ct = default, string? ownerId = null, string? effort = null) =>
            throw new NotImplementedException();
    }

    [Fact]
    public void OllamaOff_NeverLocal()
    {
        // Пустой Model → Ollama выключена → любое действие идёт на claude
        var router = Router(new() { ["Ollama:Model"] = "" });
        Assert.False(router.OllamaEnabled);
        Assert.False(router.UsesLocal(LocalActionCatalog.NotesTags));
        Assert.False(router.UsesLocal(LocalActionCatalog.ActionRank));
    }

    [Fact]
    public void OllamaOn_CatalogDefaultsApply()
    {
        var router = Router(new() { ["Ollama:Model"] = "qwen3:14b", ["Ollama:BaseUrl"] = "http://localhost:11434" });
        Assert.True(router.OllamaEnabled);
        // Рекомендованные (DefaultLocal=true) — на локаль
        Assert.True(router.UsesLocal(LocalActionCatalog.NotesTags));
        Assert.True(router.UsesLocal(LocalActionCatalog.ChatExtractTasks));
        // Оставленные на claude (DefaultLocal=false)
        Assert.False(router.UsesLocal(LocalActionCatalog.SkillTranslate));
        Assert.False(router.UsesLocal(LocalActionCatalog.DailyBriefing));
    }

    [Fact]
    public void ActionsOverride_WinsOverDefault()
    {
        var router = Router(new()
        {
            ["Ollama:Model"] = "qwen3:14b",
            ["Ollama:Actions:notes-tags"] = "false",     // рекомендованное — насильно на claude
            ["Ollama:Actions:skill-translate"] = "true", // claude-дефолт — насильно на локаль
        });
        Assert.False(router.UsesLocal(LocalActionCatalog.NotesTags));
        Assert.True(router.UsesLocal(LocalActionCatalog.SkillTranslate));
    }

    [Fact]
    public void UnknownActionKey_DoesNotThrow()
    {
        var router = Router(new()
        {
            ["Ollama:Model"] = "qwen3:14b",
            ["Ollama:Actions:bogus-action"] = "true",
        });
        // Неизвестный ключ игнорируется (лог-warning), роутер жив
        Assert.False(router.UsesLocal("bogus-action"));
        Assert.True(router.UsesLocal(LocalActionCatalog.NotesTags));
    }

    [Fact]
    public void ProfileFor_UsesCatalogDefaults_AndConfigOverride()
    {
        var router = Router(new() { ["Ollama:Model"] = "qwen3:14b" });
        // notes-tags — профиль Small (дефолт num_ctx 4096)
        Assert.Equal(4096, router.ProfileFor(LocalActionCatalog.NotesTags).NumCtx);

        var overridden = Router(new()
        {
            ["Ollama:Model"] = "qwen3:14b",
            ["Ollama:Profiles:small:NumCtx"] = "9000",
        });
        Assert.Equal(9000, overridden.ProfileFor(LocalActionCatalog.NotesTags).NumCtx);
    }

    [Fact]
    public async Task CheapRunner_OllamaOff_GoesToClaude()
    {
        var config = Config(new() { ["Ollama:Model"] = "" });
        var router = new LocalActionRouter(Ollama(config), config, NullLogger<LocalActionRouter>.Instance);
        var runner = new CheapTextRunner(router, Ollama(config), new FakeOneShot(),
            NullLogger<CheapTextRunner>.Instance);

        var result = await runner.RunAsync(LocalActionCatalog.NotesTags, "prompt-text", "haiku");
        Assert.Equal("CLAUDE[haiku]:prompt-text", result);
        Assert.False(runner.UsesLocal(LocalActionCatalog.NotesTags));
    }

    [Fact]
    public void Catalog_AllKeysUnique()
    {
        var keys = LocalActionCatalog.All.Select(a => a.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(LocalActionCatalog.All, a => Assert.True(LocalActionCatalog.IsKnown(a.Key)));
    }
}
