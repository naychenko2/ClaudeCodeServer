using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// HasFreeRoute — единая точка «есть бесплатный шаг» в цепочке CheapTextRunner.
// До правки OllamaActionRankService.Enabled знал только про direct-маршрут через
// HasFreeDirectRoute, и для места `action-rank` (DefaultLocal=true) с конкретной
// не-direct моделью возвращал false, хотя реальная цепочка RunFreeAsync сходила бы
// на локаль. Этот файл ловит именно эту развилку: после HasFreeRoute все четыре
// сценария из постановки должны давать ожидаемый ответ.
public class CheapTextRunnerHasFreeRouteTests
{
    private static IConfiguration Config(Dictionary<string, string?> d) =>
        TestConfig.Build(d);

    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static OllamaClient Ollama(IConfiguration config) =>
        new(new NullHttpFactory(), config, NullLogger<OllamaClient>.Instance);

    // Cloud-адаптер без настроенного openrouter: Enabled=false. На сценарии `direct:` это
    // не мешает — HasFreeRoute проверяет только CloudCheapClient.IsDirectRoute, а не Cloud.Enabled.
    private static CloudCheapClient Cloud(IConfiguration config) =>
        new(new NullHttpFactory(), config, new LlmProviderRegistry(config),
            NullLogger<CloudCheapClient>.Instance);

    private static IConfiguration ConfigWithTempData(Dictionary<string, string?> d)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        d["DataPath"] = Path.Combine(dir, "projects.json");
        return Config(d);
    }

    private static LocalActionOverridesStore Store(IConfiguration config) =>
        new(config, NullLogger<LocalActionOverridesStore>.Instance);

    private static (LocalActionRouter Router, LocalActionOverridesStore Store) MakeRouter(
        Dictionary<string, string?> cfg)
    {
        var config = ConfigWithTempData(cfg);
        var store = Store(config);
        return (new LocalActionRouter(Ollama(config), store, config, NullLogger<LocalActionRouter>.Instance), store);
    }

    private static CheapTextRunner Runner(IConfiguration config, LocalActionRouter router) =>
        new(router, Ollama(config), Cloud(config), new FakeOneShot(), NullLogger<CheapTextRunner>.Instance);

    private sealed class FakeOneShot : IOneShotRunner
    {
        public string? NormalizeModel(string? model) => model;
        public Task<string> RunAsync(string prompt, string? model = null, TimeSpan? timeout = null,
            CancellationToken ct = default, string? ownerId = null, string? effort = null, string? label = null)
            => Task.FromResult($"CLAUDE[{model}]:{prompt}");
        public Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null,
            TimeSpan? timeout = null, CancellationToken ct = default, string? ownerId = null,
            string? effort = null, string? label = null)
            => Task.FromResult(new OneShotResult($"CLAUDE[{model}]:{prompt}", null, 0));
    }

    [Fact]
    public void DirectRoute_True()
    {
        // Сценарий: место notes-tags уводится на direct-модель через админский оверрайд.
        var (router, store) = MakeRouter(new() { ["Ollama:Model"] = "qwen3:14b" });
        Assert.True(store.Set(LocalActionCatalog.NotesTags, "direct:nemotron:free"));
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "qwen3:14b" });
        var runner = Runner(config, router);
        Assert.True(runner.HasFreeRoute(LocalActionCatalog.NotesTags),
            "прямая модель агрегатора — HasFreeRoute обязан вернуть true даже при выключенной локали");
    }

    [Fact]
    public void LocalKind_True()
    {
        // Kind=Local через админский оверрайд + живой Ollama → бесплатный шаг есть.
        var (router, store) = MakeRouter(new() { ["Ollama:Model"] = "qwen3:14b" });
        Assert.True(store.Set(LocalActionCatalog.NotesTags, LocalActionOverridesStore.LocalRoute));
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "qwen3:14b" });
        var runner = Runner(config, router);
        Assert.True(runner.HasFreeRoute(LocalActionCatalog.NotesTags),
            "Kind=Local + живой ILocalLlmClient — есть бесплатный маршрут на локаль");
    }

    // Главный сценарий из постановки: DefaultLocal=true и переопределение на конкретную
    // НЕ-direct модель. До правки HasFreeDirectRoute возвращал false (Kind=Model && Model
    // не начинается с direct:), и OllamaActionRankService.Enabled выключал фичу. После
    // HasFreeRoute та же конфигурация сходится на локаль — должно быть true.
    [Fact]
    public void ConcreteNonDirectModel_DefaultLocalTrue_True()
    {
        // action-rank имеет DefaultLocal=true в каталоге — см. LocalActionCatalog.Builtin.
        // Через админский оверрайд уходим на конкретную НЕ-direct модель.
        var (router, store) = MakeRouter(new() { ["Ollama:Model"] = "qwen3:14b" });
        Assert.True(store.Set(LocalActionCatalog.ActionRank, "haiku"));
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "qwen3:14b" });
        var runner = Runner(config, router);
        Assert.True(runner.HasFreeRoute(LocalActionCatalog.ActionRank),
            "DefaultLocal=true → локаль-страховка работает и при Kind=Model с конкретной моделью");
    }

    [Fact]
    public void TierKind_False()
    {
        // Без переопределений место с DefaultLocal=false (SkillTranslate) идёт на Kind=Tier,
        // локаль-страховка не применяется (DefaultLocal=false), direct-модели тоже нет.
        var (router, _) = MakeRouter(new() { ["Ollama:Model"] = "qwen3:14b" });
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "qwen3:14b" });
        var runner = Runner(config, router);
        Assert.False(runner.HasFreeRoute(LocalActionCatalog.SkillTranslate),
            "Kind=Tier с DefaultLocal=false → ни direct, ни локаль, HasFreeRoute обязан быть false");
    }
}