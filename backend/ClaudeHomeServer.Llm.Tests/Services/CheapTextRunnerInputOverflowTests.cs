using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Q1-S10 · fail-closed для мест вики: переполнение входа — отказ окна (LocalInputOverflowException),
// а не обрезка хвоста промпта. Что ловят тесты:
//  • именованный движок с окном меньше профиля — отказ ДО вызова движка: 0 обращений к
//    клиенту, 0 к облаку, 0 к claude (модель не звалась ни разу, слот не занят);
//  • ровно на границе бюджета — не отказ: окно влезает целиком;
//  • цепочка с облачным фолбэком на основной локали — отказ НЕ уходит в облако (иначе
//    неполный вход дособочила бы платная модель, и потеря стала бы неотличима от успеха);
//  • место без флага — прежняя обрезка в клиенте: исключения нет, клиент вызван один раз;
//  • флаг стоит ровно на трёх местах вики — он не «общий режим раннера».
// Бюджет нигде не копируется: он берётся из LlamaServerClient.InputBudgetChars — той же
// точки, по которой живой путь резал бы промпт (формула в одном месте, инвариант задачи).
public class CheapTextRunnerInputOverflowTests
{
    private const string Engine = "gemma";
    private const string FallbackModel = "fallback-model";
    // Окно именованного движка из задачи: боевой NumCtx gemma до правки Андрея.
    private const int EngineNumCtx = 32_768;

    private static IConfiguration Config(Dictionary<string, string?> d)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        d["DataPath"] = Path.Combine(dir, "projects.json");
        return TestConfig.Build(d);
    }

    // Облако, которое можно посчитать: прямой источник настроен, а запросы считает handler.
    // «Облако не звалось» = счётчик 0, а не «ответ пустой».
    private sealed class CountingHttpFactory : IHttpClientFactory
    {
        public int Requests;
        public HttpClient CreateClient(string name) => new(new CountingHandler(this));
    }

    private sealed class CountingHandler(CountingHttpFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            owner.Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("{}") });
        }
    }

    // Клиент локального движка: считает обращения и отвечает; «не дошёл» = Calls 0.
    private sealed class FakeLocalClient(string? answer = "ответ локали") : ILocalLlmClient
    {
        public int Calls;
        public bool Enabled => true;
        public string BaseUrl => "http://local";
        public string Model => "qwen3.8-27b";
        public string TextModel => "qwen3.8-27b";
        public string ProviderKey => LocalLlmOptions.LlamaServer;

        public Task<string?> ChatJsonAsync(string systemPrompt, string userPrompt, object jsonFormat,
            CancellationToken ct = default, string? model = null, int? timeoutMs = null, int? numPredict = null,
            int? numCtx = null, string? ownerId = null, string? label = null)
        { Calls++; return Task.FromResult(answer); }

        public Task<string?> GenerateTextAsync(string prompt, string? model, TimeSpan timeout, int numPredict,
            int numCtx, string? ownerId = null, string? label = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(answer);
        }

        public async Task<(string? Text, UsageInfo? Usage)> GenerateTextDetailedAsync(string prompt, string? model,
            TimeSpan timeout, int numPredict, int numCtx, string? ownerId = null, string? label = null,
            CancellationToken ct = default)
            => (await GenerateTextAsync(prompt, model, timeout, numPredict, numCtx, ownerId, label, ct), null);

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

    private static NamedLocalEngines Engines(ILocalLlmClient client, int numCtx = EngineNumCtx) =>
        new([new LocalEngine(
            new LocalEngineSpec(Engine, ReadyUrl: "http://arbiter/ready", StateUrl: null,
                FallbackRoute: null, NumCtx: numCtx,
                Actions: new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            client, _ => Task.FromResult(true))]);

    // Стенд: именованный движок и основная локаль — как даны (не дана — не настроена);
    // прямой облачный источник настроен и считается counting-хендлером, чтобы «0 вызовов»
    // был доказуем.
    private static (CheapTextRunner Runner, CountingHttpFactory Http, FakeOneShot Claude, LocalActionRouter Router)
        Stand(NamedLocalEngines? engines = null, ILocalLlmClient? mainLocal = null)
    {
        var cfg = new Dictionary<string, string?>
        {
            ["LlmProviders:freellmapi:DisplayName"] = "FreeLLM",
            ["LlmProviders:freellmapi:ApiKey"] = "test-key",
            ["LlmProviders:freellmapi:AnthropicBaseUrl"] = "http://localhost:3001",
            ["LlmProviders:freellmapi:ApiBaseUrl"] = "http://localhost:3001/v1",
            ["CheapHttpSources:freellmapi:Provider"] = "freellmapi",
            ["CheapHttpSources:freellmapi:Models:0:Id"] = FallbackModel,
            ["CheapHttpSources:freellmapi:Models:0:DisplayName"] = "Fallback",
            ["CheapHttpSources:freellmapi:Models:0:ContextWindow"] = "32000",
        };
        var config = Config(cfg);
        var http = new CountingHttpFactory();
        var ollama = mainLocal ?? new OllamaClient(http, config, NullLogger<OllamaClient>.Instance);
        var store = new LocalActionOverridesStore(config, NullLogger<LocalActionOverridesStore>.Instance);
        var router = new LocalActionRouter(ollama, store, config, NullLogger<LocalActionRouter>.Instance,
            engines: engines);
        var cloud = new CloudCheapClient(http, config, new LlmProviderRegistry(config),
            NullLogger<CloudCheapClient>.Instance);
        var claude = new FakeOneShot();
        var runner = new CheapTextRunner(router, ollama, cloud, claude,
            NullLogger<CheapTextRunner>.Instance, namedEngines: engines);
        return (runner, http, claude, router);
    }

    // Бюджет живого пути: та же точка формулы, что у клиента; для именованного движка
    // NumCtx — окно движка (его урезает RunEngineAsync), NumPredict — профиль места.
    private static int BudgetOf(LocalActionRouter router, string actionKey, int? numCtxOverride = null)
    {
        var spec = router.ProfileFor(actionKey);
        var numCtx = numCtxOverride is { } cap && cap < spec.NumCtx ? cap : spec.NumCtx;
        return LlamaServerClient.InputBudgetChars(numCtx, spec.NumPredict);
    }

    [Fact]
    public async Task RunOnNamedEngine_WikiPlaceOverflows_ThrowsBeforeEngineCall()
    {
        // c1: ночь на движке с окном 32768 (боевой NumCtx до правки Андрея) и промптом 80 000
        // симв. — отказ окна ДО вызова: движок не звался, облако и claude не звались.
        // Число промпта поднято с 60 000 (Q1-S14): после NumPredict 8192 бюджет этого движка
        // 73 472 симв., и 60 000-символьный промпт в него влезает — отказ не случился бы.
        var engine = new FakeLocalClient();
        var (runner, http, claude, router) = Stand(engines: Engines(engine));
        var budget = BudgetOf(router, LocalActionCatalog.WikiNightly, EngineNumCtx);
        Assert.True(80_000 > budget); // промпт критерия действительно за границей

        var ex = await Assert.ThrowsAsync<LocalInputOverflowException>(() =>
            runner.RunOnLocalEngineDetailedAsync(Engine, LocalActionCatalog.WikiNightly, new string('а', 80_000)));
        // Причина с цифрами: их называет вызывающий (Q1-S12) в своём итоге отказа окна.
        Assert.Contains("80000", ex.Message);
        Assert.Contains($"при бюджете {budget}", ex.Message);
        Assert.Contains($"NumCtx {EngineNumCtx}", ex.Message);
        Assert.Equal(0, engine.Calls);
        Assert.Equal(0, http.Requests);
        Assert.Empty(claude.Calls);
    }

    [Fact]
    public async Task RunOnNamedEngine_WikiPlaceExactlyAtBudget_CalledOnce()
    // Ровно на границе — не отказ: [from, to) здесь «влезает = не больше бюджета».
    // Мутация «prompt.Length >= budget» краснит этот тест.
    {
        var engine = new FakeLocalClient();
        var (runner, _, _, router) = Stand(engines: Engines(engine));
        var budget = BudgetOf(router, LocalActionCatalog.WikiNightly, EngineNumCtx);
        var result = await runner.RunOnLocalEngineDetailedAsync(
            Engine, LocalActionCatalog.WikiNightly, new string('а', budget));
        Assert.Equal("ответ локали", result!.Text);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task Run_MainLocalWikiPlaceOverflows_NoCloudFallback()
    {
        // c3: цепочка RunAsync (с облачным фолбэком) на основной локали: переполнение на
        // локальном шаге — отказ наружу, а не дособочка платной моделью: облачный раннер
        // не звался ни разу.
        var main = new FakeLocalClient();
        var (runner, http, claude, router) = Stand(mainLocal: main);
        var budget = BudgetOf(router, LocalActionCatalog.WikiIngest);
        await Assert.ThrowsAsync<LocalInputOverflowException>(() =>
            runner.RunAsync(LocalActionCatalog.WikiIngest, new string('а', budget + 1)));
        Assert.Equal(0, main.Calls);
        Assert.Equal(0, http.Requests);
        Assert.Empty(claude.Calls);
    }

    [Fact]
    public async Task Run_NonWikiPlaceOverflows_StillTrimsAtClient()
    {
        // c2: место без флага (конспект дня — тоже Large, тоже за бюджетом) режется
        // клиентом, как раньше: исключения нет, клиент вызван ровно один раз.
        var main = new FakeLocalClient();
        var (runner, _, claude, router) = Stand(mainLocal: main);
        var budget = BudgetOf(router, LocalActionCatalog.NotesDailySummary);
        var answer = await runner.RunAsync(LocalActionCatalog.NotesDailySummary, new string('а', budget + 1));
        Assert.Equal("ответ локали", answer);
        Assert.Equal(1, main.Calls);
        Assert.Empty(claude.Calls);
    }

    [Fact]
    public void Catalog_FailOnInputOverflow_IsExactlyWikiPlaces()
    {
        // c4: флаг — не общий режим раннера: он стоит ровно на трёх местах вики.
        var flagged = LocalActionCatalog.All
            .Where(a => a.FailOnInputOverflow)
            .Select(a => a.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            [LocalActionCatalog.WikiIngest, LocalActionCatalog.WikiJudge, LocalActionCatalog.WikiNightly],
            flagged);
    }
}
