using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Именованный локальный движок (LocalEngines:*) в цепочке CheapTextRunner: место из его
// Actions идёт на движок → при неготовности/пустом ответе на direct-страховку движка →
// на claude. Главное, что ловят тесты: отказ движка под арбитром GPU не теряет действие и
// не тратит время на таймаут (проба готовности), а выбор админа важнее конфига движка.
public class NamedLocalEngineTests
{
    private const string Engine = "bonsai";

    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // Фейковый ILocalLlmClient движка: отдаёт answer (null — «движок не ответил»), считает вызовы.
    private sealed class FakeEngineClient(string? answer, bool enabled = true) : ILocalLlmClient
    {
        public int Calls;
        public bool Enabled => enabled;
        public string BaseUrl => "http://engine";
        public string Model => "bonsai-9b";
        public string TextModel => "bonsai-9b";
        public string ProviderKey => LocalLlmOptions.LlamaServer;

        public Task<string?> ChatJsonAsync(string systemPrompt, string userPrompt, object jsonFormat,
            CancellationToken ct = default, string? model = null, int? timeoutMs = null, int? numPredict = null,
            int? numCtx = null, string? ownerId = null, string? label = null)
        { Calls++; return Task.FromResult(answer); }

        public Task<string?> GenerateTextAsync(string prompt, string? model, TimeSpan timeout, int numPredict,
            int numCtx, string? ownerId = null, string? label = null, CancellationToken ct = default)
        { Calls++; return Task.FromResult(answer); }

        public Task<ChatTurnResult> ChatTurnAsync(IReadOnlyList<ChatMsg> messages, string? model, TimeSpan timeout,
            int numPredict, int numCtx, string? ownerId, Func<string, Task>? onDelta = null,
            CancellationToken ct = default)
            => Task.FromResult(new ChatTurnResult(answer, null));

        public Task WarmUpAsync(string? model, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeOneShot : IOneShotRunner
    {
        public readonly List<string?> Calls = [];
        public string? NormalizeModel(string? model) => model;
        public Task<string> RunAsync(string prompt, string? model = null, TimeSpan? timeout = null,
            CancellationToken ct = default, string? ownerId = null, string? effort = null, string? label = null)
        { Calls.Add(model); return Task.FromResult($"CLAUDE[{model}]"); }
        public Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null,
            TimeSpan? timeout = null, CancellationToken ct = default, string? ownerId = null,
            string? effort = null, string? label = null)
        { Calls.Add(model); return Task.FromResult(new OneShotResult($"CLAUDE[{model}]", null, 0)); }
    }

    private static IConfiguration Config(Dictionary<string, string?> d)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        d["DataPath"] = Path.Combine(dir, "projects.json");
        return TestConfig.Build(d);
    }

    private static NamedLocalEngines Engines(FakeEngineClient client, bool ready, string? fallback,
        params string[] actions) =>
        new([new LocalEngine(
            new LocalEngineSpec(Engine, ReadyUrl: "http://arbiter/ready", StateUrl: null,
                FallbackRoute: fallback, NumCtx: 8192,
                Actions: new HashSet<string>(actions, StringComparer.OrdinalIgnoreCase)),
            client, _ => Task.FromResult(ready))]);

    private static (CheapTextRunner Runner, LocalActionRouter Router, LocalActionOverridesStore Store, FakeOneShot Claude)
        Make(NamedLocalEngines engines, Dictionary<string, string?>? cfg = null)
    {
        // Основная локаль выключена (Ollama:Model пуст): шаг локали в цепочке только движковый.
        var config = Config(cfg ?? new());
        var ollama = new OllamaClient(new NullHttpFactory(), config, NullLogger<OllamaClient>.Instance);
        var store = new LocalActionOverridesStore(config, NullLogger<LocalActionOverridesStore>.Instance);
        var router = new LocalActionRouter(ollama, store, config, NullLogger<LocalActionRouter>.Instance,
            engines: engines);
        var cloud = new CloudCheapClient(new NullHttpFactory(), config, new LlmProviderRegistry(config),
            NullLogger<CloudCheapClient>.Instance);
        var claude = new FakeOneShot();
        return (new CheapTextRunner(router, ollama, cloud, claude, NullLogger<CheapTextRunner>.Instance),
            router, store, claude);
    }

    [Fact]
    public void Resolve_ActionFromEngineConfig_RoutesToEngine()
    {
        var (runner, router, _, _) = Make(Engines(new FakeEngineClient("ok"), ready: true, null,
            LocalActionCatalog.ChatTitle));
        var route = router.Resolve(LocalActionCatalog.ChatTitle);
        Assert.Equal(RouteKind.Local, route.Kind);
        Assert.Equal(Engine, route.Engine);
        Assert.Equal(RouteSource.Config, route.Source);
        Assert.True(router.UsesLocal(LocalActionCatalog.ChatTitle));
        Assert.True(runner.HasFreeRoute(LocalActionCatalog.ChatTitle));
        Assert.Equal("local:bonsai", runner.DescribeRoute(LocalActionCatalog.ChatTitle, null));
        // Место вне Actions движка идёт своим прежним маршрутом (основная локаль выключена → слот).
        Assert.Null(router.Resolve(LocalActionCatalog.NotesTags).Engine);
    }

    [Fact]
    public void AdminOverride_WinsOverEngineConfig()
    {
        var (_, router, store, _) = Make(Engines(new FakeEngineClient("ok"), ready: true, null,
            LocalActionCatalog.ChatTitle));
        Assert.True(store.Set(LocalActionCatalog.ChatTitle, "tier:strong"));
        var route = router.Resolve(LocalActionCatalog.ChatTitle);
        Assert.Equal(RouteKind.Tier, route.Kind);
        Assert.Null(route.Engine);
    }

    [Fact]
    public async Task Run_EngineReady_AnswersFromEngine()
    {
        var client = new FakeEngineClient("Заголовок от движка");
        var (runner, _, _, claude) = Make(Engines(client, ready: true, "direct:MiniMax-M3.1-Flash",
            LocalActionCatalog.ChatTitle));
        var text = await runner.RunAsync(LocalActionCatalog.ChatTitle, "prompt", fallbackModel: "haiku");
        Assert.Equal("Заголовок от движка", text);
        Assert.Equal(1, client.Calls);
        Assert.Empty(claude.Calls);
    }

    [Fact]
    public async Task Run_EngineNotReady_SkipsEngineWithoutCall_FallsThrough()
    {
        // Арбитр GPU выгрузил движок: проба говорит «не готов» — вызова движка нет вовсе,
        // direct-страховка без настроенного провайдера отдаёт null, остаётся claude.
        var client = new FakeEngineClient("never");
        var (runner, _, _, claude) = Make(Engines(client, ready: false, "direct:MiniMax-M3.1-Flash",
            LocalActionCatalog.ChatTitle));
        var text = await runner.RunAsync(LocalActionCatalog.ChatTitle, "prompt", fallbackModel: "haiku");
        Assert.Equal(0, client.Calls);
        Assert.Equal("CLAUDE[haiku]", text);
        Assert.Equal(["haiku"], claude.Calls);
    }

    [Fact]
    public async Task Run_EngineEmptyAnswer_FallsThroughToClaude()
    {
        var client = new FakeEngineClient(null);
        var (runner, _, _, claude) = Make(Engines(client, ready: true, null, LocalActionCatalog.ChatTitle));
        var text = await runner.RunAsync(LocalActionCatalog.ChatTitle, "prompt", fallbackModel: "haiku");
        Assert.Equal(1, client.Calls);
        Assert.Equal("CLAUDE[haiku]", text);
    }

    [Fact]
    public async Task RunLocalOrChosen_EngineFails_NoCloud_ReturnsNull()
    {
        // Режим «без облака»: ни direct-страховка движка, ни claude не вызываются.
        var client = new FakeEngineClient(null);
        var (runner, _, _, claude) = Make(Engines(client, ready: true, "direct:MiniMax-M3.1-Flash",
            LocalActionCatalog.ChatTitle));
        var text = await runner.RunLocalOrChosenAsync(LocalActionCatalog.ChatTitle, "prompt");
        Assert.Null(text);
        Assert.Empty(claude.Calls);
    }

    [Fact]
    public async Task RunDetailed_EngineReady_AnswersFromEngineWithoutUsage()
    {
        var client = new FakeEngineClient("{\"ok\":true}");
        var (runner, _, _, claude) = Make(Engines(client, ready: true, null, LocalActionCatalog.ChatTitle));
        var result = await runner.RunDetailedAsync(LocalActionCatalog.ChatTitle, "prompt", fallbackModel: "haiku",
            jsonFormat: "json");
        Assert.Equal("{\"ok\":true}", result.Text);
        Assert.Null(result.Usage);
        Assert.Empty(claude.Calls);
    }

    [Fact]
    public void Config_ReadsEngineSection_SkipsUnknownActionsAndBadFallback()
    {
        var config = Config(new()
        {
            ["LocalEngines:bonsai:BaseUrl"] = "http://192.168.7.107:18030",
            ["LocalEngines:bonsai:Model"] = "bonsai-9b",
            ["LocalEngines:bonsai:ReadyUrl"] = "http://192.168.7.107:18031/ready",
            ["LocalEngines:bonsai:FallbackRoute"] = "direct:MiniMax-M3.1-Flash",
            ["LocalEngines:bonsai:NumCtx"] = "8192",
            ["LocalEngines:bonsai:Actions:0"] = LocalActionCatalog.ChatTitle,
            ["LocalEngines:bonsai:Actions:1"] = "no-such-action",
            // Движок без модели — пропускается целиком.
            ["LocalEngines:broken:BaseUrl"] = "http://x",
            ["LocalEngines:broken:Actions:0"] = LocalActionCatalog.NotesTags,
            // Страховка не direct: — выключается, движок остаётся.
            ["LocalEngines:plain:BaseUrl"] = "http://y",
            ["LocalEngines:plain:Model"] = "m",
            ["LocalEngines:plain:FallbackRoute"] = "haiku",
            ["LocalEngines:plain:Actions:0"] = LocalActionCatalog.TaskClassify,
        });
        var engines = new NamedLocalEngines(new NullHttpFactory(), config, NullLoggerFactory.Instance);
        Assert.Equal("bonsai", engines.EngineFor(LocalActionCatalog.ChatTitle));
        Assert.Null(engines.EngineFor("no-such-action"));
        Assert.Null(engines.EngineFor(LocalActionCatalog.NotesTags));
        Assert.Null(engines.Get("broken"));
        var bonsai = engines.Get("bonsai")!;
        Assert.True(bonsai.Enabled);
        Assert.Equal("direct:MiniMax-M3.1-Flash", bonsai.Spec.FallbackRoute);
        Assert.Equal(8192, bonsai.Spec.NumCtx);
        Assert.Equal("http://192.168.7.107:18030", bonsai.Client.BaseUrl);
        Assert.Null(engines.Get("plain")!.Spec.FallbackRoute);
        Assert.Equal("plain", engines.EngineFor(LocalActionCatalog.TaskClassify));
    }
}
