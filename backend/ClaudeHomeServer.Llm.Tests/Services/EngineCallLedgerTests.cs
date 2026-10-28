using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Б5: EngineCallLedger — журнал вызовов мест к именованным движкам: «кто сейчас на модели»
// + ответы за час (блок «Дежурство», разрез v2 §4). Писатель — CheapTextRunner, в обеих
// цепочках (RunChainAsync и RunDetailedAsync) и только когда маршрут дал движок.
// Восемь сценариев ниже — 4 корзины × 2 цепочки: ровно то, что разрез называет риском
// («локальная модель дописывает вызов только в одну цепочку»).
public class EngineCallLedgerTests
{
    private const string Engine = "bonsai";

    // T0 — ровно минутная граница: клетки индексируются unix-минутами, тест окна
    // не должен зависеть от внутри минутного сдвига.
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-28T12:00:00+00:00");

    // Часы с ручным ходом (тот же приём, что в других тестовых вертикалях):
    // окно и sinceAt меряются на них, а не на реальном времени.
    private sealed class MutableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // Фейковый ILocalLlmClient движка (та же форма, что в NamedLocalEngineTests):
    // отдаёт answer (null — «движок не ответил»).
    private sealed class FakeEngineClient(string? answer) : ILocalLlmClient
    {
        public bool Enabled => true;
        public string BaseUrl => "http://engine";
        public string Model => "bonsai-9b";
        public string TextModel => "bonsai-9b";
        public string ProviderKey => LocalLlmOptions.LlamaServer;

        public Task<string?> ChatJsonAsync(string systemPrompt, string userPrompt, object jsonFormat,
            CancellationToken ct = default, string? model = null, int? timeoutMs = null, int? numPredict = null,
            int? numCtx = null, string? ownerId = null, string? label = null)
            => Task.FromResult(answer);

        public Task<string?> GenerateTextAsync(string prompt, string? model, TimeSpan timeout, int numPredict,
            int numCtx, string? ownerId = null, string? label = null, CancellationToken ct = default)
            => Task.FromResult(answer);

        public Task<ChatTurnResult> ChatTurnAsync(IReadOnlyList<ChatMsg> messages, string? model, TimeSpan timeout,
            int numPredict, int numCtx, string? ownerId, Func<string, Task>? onDelta = null,
            CancellationToken ct = default)
            => Task.FromResult(new ChatTurnResult(answer, null));

        public Task WarmUpAsync(string? model, CancellationToken ct = default) => Task.CompletedTask;
    }

    // Фейковый IOneShotRunner: отдаёт текст или бросает (сценарий missed — исключение
    // обязан проброситься наверх, как раньше).
    private sealed class FakeOneShot(bool throws = false) : IOneShotRunner
    {
        public string? NormalizeModel(string? model) => model;
        public Task<string> RunAsync(string prompt, string? model = null, TimeSpan? timeout = null,
            CancellationToken ct = default, string? ownerId = null, string? effort = null, string? label = null)
            => throws ? Task.FromException<string>(new InvalidOperationException("облако недоступно"))
                     : Task.FromResult($"CLAUDE[{model}]");
        public Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null,
            TimeSpan? timeout = null, CancellationToken ct = default, string? ownerId = null,
            string? effort = null, string? label = null)
            => throws ? Task.FromException<OneShotResult>(new InvalidOperationException("облако недоступно"))
                     : Task.FromResult(new OneShotResult($"CLAUDE[{model}]", null, 0));
    }

    // Ответ прямого адаптера (страховка движка): та же форма, что в CloudCheapClientTests.
    private const string CloudOkJson = """
        {
          "choices": [{
            "index": 0,
            "finish_reason": "stop",
            "message": {"role": "assistant", "content": "REPLY"}
          }],
          "usage": {"prompt_tokens": 3, "completion_tokens": 4}
        }
        """;

    private sealed class StubHttpFactory(string json) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(json));
    }

    private sealed class StubHandler(string json) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
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

    private static NamedLocalEngines Engines(string? engineAnswer, string? fallback = null) =>
        new([new LocalEngine(
            new LocalEngineSpec(Engine, ReadyUrl: null, StateUrl: null,
                FallbackRoute: fallback, NumCtx: 8192,
                Actions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { LocalActionCatalog.ChatTitle }),
            new FakeEngineClient(engineAnswer), readyProbe: null)]);

    private sealed class Harness
    {
        public CheapTextRunner Runner = null!;
        public EngineCallLedger Ledger = null!;
        public MutableTime Time = null!;
        public LocalActionOverridesStore Store = null!;
    }

    // Тот же состав, что в NamedLocalEngineTests: основная локаль выключена (Ollama:Model
    // пуст), шаг локали в цепочке — только движковый. Часы журнала — ручные, от T0.
    private static Harness Make(NamedLocalEngines engines, IOneShotRunner? claude = null,
        IHttpClientFactory? cloudHttp = null)
    {
        var cfg = new Dictionary<string, string?>();
        if (cloudHttp is not null) WithOpenRouter(cfg);
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        cfg["DataPath"] = Path.Combine(dir, "projects.json");
        var config = TestConfig.Build(cfg);
        var time = new MutableTime(T0);
        var ledger = new EngineCallLedger(time);
        var ollama = new OllamaClient(new NullHttpFactory(), config, NullLogger<OllamaClient>.Instance);
        var store = new LocalActionOverridesStore(config, NullLogger<LocalActionOverridesStore>.Instance);
        var router = new LocalActionRouter(ollama, store, config, NullLogger<LocalActionRouter>.Instance,
            engines: engines);
        var cloud = new CloudCheapClient(cloudHttp ?? new NullHttpFactory(), config,
            new LlmProviderRegistry(config), NullLogger<CloudCheapClient>.Instance);
        var runner = new CheapTextRunner(router, ollama, cloud, claude ?? new FakeOneShot(),
            NullLogger<CheapTextRunner>.Instance, ledger: ledger);
        return new Harness { Runner = runner, Ledger = ledger, Time = time, Store = store };
    }

    private static void AssertBaskets(EngineAnswerCounts a, int bonsai, int insurance, int claude, int missed)
    {
        Assert.Equal(bonsai, a.Bonsai);
        Assert.Equal(insurance, a.Insurance);
        Assert.Equal(claude, a.Claude);
        Assert.Equal(missed, a.Missed);
    }

    // === RunChainAsync (RunAsync / RunLocalOrChosenAsync): 4 корзины ===

    [Fact]
    public async Task RunChain_EngineAnswers_BasketBonsai()
    {
        var h = Make(Engines("заголовок от движка", "direct:nvidia/nemotron:free"));
        var text = await h.Runner.RunAsync(LocalActionCatalog.ChatTitle, "prompt", fallbackModel: "haiku");
        Assert.Equal("заголовок от движка", text);
        AssertBaskets(h.Ledger.AnswersLastHour(Engine), 1, 0, 0, 0);
        Assert.Empty(h.Ledger.CallsNow(Engine)); // закрытый вызов не остаётся в «сейчас»
    }

    [Fact]
    public async Task RunChain_InsuranceAnswers_BasketInsurance()
    {
        // Движок не ответил — direct-страховка движка (тот же путь, что в проде под арбитром).
        var h = Make(Engines(null, "direct:nvidia/nemotron:free"), cloudHttp: new StubHttpFactory(CloudOkJson));
        var text = await h.Runner.RunAsync(LocalActionCatalog.ChatTitle, "prompt", fallbackModel: "haiku");
        Assert.Equal("REPLY", text);
        AssertBaskets(h.Ledger.AnswersLastHour(Engine), 0, 1, 0, 0);
    }

    [Fact]
    public async Task RunChain_ClaudeAnswers_BasketClaude()
    {
        // Движок не ответил, страховки нет — финальный облачный шаг.
        var h = Make(Engines(null));
        var text = await h.Runner.RunAsync(LocalActionCatalog.ChatTitle, "prompt", fallbackModel: "haiku");
        Assert.Equal("CLAUDE[haiku]", text);
        AssertBaskets(h.Ledger.AnswersLastHour(Engine), 0, 0, 1, 0);
    }

    [Fact]
    public async Task RunChain_ClaudeThrows_BasketMissedAndExceptionPropagates()
    {
        var h = Make(Engines(null), claude: new FakeOneShot(throws: true));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.RunAsync(LocalActionCatalog.ChatTitle, "prompt", fallbackModel: "haiku"));
        AssertBaskets(h.Ledger.AnswersLastHour(Engine), 0, 0, 0, 1);
    }

    // Режим «без облака» (RunLocalOrChosenAsync): ни страховка, ни claude не вызываются —
    // цепочка вернула null, и это тоже missed (разрез: режим отличается только корзиной missed).
    [Fact]
    public async Task RunChain_NoCloudModeEngineFails_BasketMissed()
    {
        var h = Make(Engines(null, "direct:nvidia/nemotron:free"));
        var text = await h.Runner.RunLocalOrChosenAsync(LocalActionCatalog.ChatTitle, "prompt");
        Assert.Null(text);
        AssertBaskets(h.Ledger.AnswersLastHour(Engine), 0, 0, 0, 1);
    }

    // === RunDetailedAsync: те же 4 корзины (мутация-проверка «двух цепочек») ===

    [Fact]
    public async Task RunDetailed_EngineAnswers_BasketBonsai()
    {
        var h = Make(Engines("{\"ok\":true}", "direct:nvidia/nemotron:free"));
        var result = await h.Runner.RunDetailedAsync(LocalActionCatalog.ChatTitle, "prompt",
            fallbackModel: "haiku");
        Assert.Equal("{\"ok\":true}", result.Text);
        AssertBaskets(h.Ledger.AnswersLastHour(Engine), 1, 0, 0, 0);
        Assert.Empty(h.Ledger.CallsNow(Engine));
    }

    [Fact]
    public async Task RunDetailed_InsuranceAnswers_BasketInsurance()
    {
        var h = Make(Engines(null, "direct:nvidia/nemotron:free"), cloudHttp: new StubHttpFactory(CloudOkJson));
        var result = await h.Runner.RunDetailedAsync(LocalActionCatalog.ChatTitle, "prompt",
            fallbackModel: "haiku");
        Assert.Equal("REPLY", result.Text);
        AssertBaskets(h.Ledger.AnswersLastHour(Engine), 0, 1, 0, 0);
    }

    [Fact]
    public async Task RunDetailed_ClaudeAnswers_BasketClaude()
    {
        var h = Make(Engines(null));
        var result = await h.Runner.RunDetailedAsync(LocalActionCatalog.ChatTitle, "prompt",
            fallbackModel: "haiku");
        Assert.Equal("CLAUDE[haiku]", result.Text);
        AssertBaskets(h.Ledger.AnswersLastHour(Engine), 0, 0, 1, 0);
    }

    [Fact]
    public async Task RunDetailed_ClaudeThrows_BasketMissedAndExceptionPropagates()
    {
        var h = Make(Engines(null), claude: new FakeOneShot(throws: true));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.RunDetailedAsync(LocalActionCatalog.ChatTitle, "prompt", fallbackModel: "haiku"));
        AssertBaskets(h.Ledger.AnswersLastHour(Engine), 0, 0, 0, 1);
    }

    // === Учёт только цепочек с движком (критерий c2) ===

    [Fact]
    public async Task ChainWithoutEngine_NothingCounted()
    {
        // Явная модель админа (оверрайд на direct-модель): маршрут Kind=Model,
        // router.EngineOf == null — ни одна корзина не двигается, «сейчас» пусто.
        var h = Make(Engines("никогда не зовётся", "direct:nvidia/nemotron:free"),
            cloudHttp: new StubHttpFactory(CloudOkJson));
        Assert.True(h.Store.Set(LocalActionCatalog.ChatTitle, "direct:nvidia/nemotron:free"));
        var text = await h.Runner.RunAsync(LocalActionCatalog.ChatTitle, "prompt");
        Assert.Equal("REPLY", text);
        AssertBaskets(h.Ledger.AnswersLastHour(Engine), 0, 0, 0, 0);
        Assert.Empty(h.Ledger.CallsNow(Engine));
    }

    // === Окно и sinceAt на ручных часах ===

    [Fact]
    public async Task Window_61Minutes_RecordLeavesWindow()
    {
        var h = Make(Engines("ok"));
        await h.Runner.RunAsync(LocalActionCatalog.ChatTitle, "prompt");
        h.Time.Advance(TimeSpan.FromMinutes(59));
        AssertBaskets(h.Ledger.AnswersLastHour(Engine), 1, 0, 0, 0); // ровно на границе окна — ещё внутри
        h.Time.Advance(TimeSpan.FromMinutes(2));
        AssertBaskets(h.Ledger.AnswersLastHour(Engine), 0, 0, 0, 0); // 61 мин — записи в окне нет
    }

    [Fact]
    public async Task SinceAt_IsMaxOfHourAndProcessStart()
    {
        var h = Make(Engines("ok"));
        await h.Runner.RunAsync(LocalActionCatalog.ChatTitle, "prompt");
        h.Time.Advance(TimeSpan.FromMinutes(10));
        // Процессу 10 минут: знаменатель — старт процесса, а не выдуманный «пустой час».
        Assert.Equal(T0, h.Ledger.AnswersLastHour(Engine).SinceAt);
        h.Time.Advance(TimeSpan.FromHours(2));
        // Прошло 2 ч 10 мин: знаменатель — полный час окна (now − 1 ч = T0+70 мин).
        Assert.Equal(T0 + TimeSpan.FromMinutes(70), h.Ledger.AnswersLastHour(Engine).SinceAt);
    }

    // === «Кто сейчас»: звено nullable до первого Step (контракт callsNow[].link) ===

    [Fact]
    public void CallsNow_LinkNullUntilFirstStep()
    {
        var h = Make(Engines("ok"));
        var call = h.Ledger.Begin(Engine, LocalActionCatalog.ChatTitle);
        Assert.NotNull(call);
        var now0 = h.Ledger.CallsNow(Engine);
        Assert.Single(now0);
        Assert.Null(now0[0].Link); // «ждёт в очереди»: звена ещё нет
        Assert.Equal(LocalActionCatalog.ChatTitle, now0[0].ActionKey);
        Assert.Equal(LocalActionCatalog.Find(LocalActionCatalog.ChatTitle)!.Title, now0[0].Place);
        call.Step(EngineCallLink.Insurance);
        Assert.Equal(EngineCallLink.Insurance, h.Ledger.CallsNow(Engine)[0].Link);
        call.End(EngineCallOutcome.Insurance);
        Assert.Empty(h.Ledger.CallsNow(Engine));
        AssertBaskets(h.Ledger.AnswersLastHour(Engine), 0, 1, 0, 0);
        // Повторный End — no-op: двойного сента нет.
        call.End(EngineCallOutcome.Bonsai);
        AssertBaskets(h.Ledger.AnswersLastHour(Engine), 0, 1, 0, 0);
    }

    [Fact]
    public void EmptyLedger_ZerosAndNoCalls()
    {
        // Пустая коллекция: неизвестный движок — нули и пустой «сейчас», без исключения.
        var h = Make(Engines("ok"));
        AssertBaskets(h.Ledger.AnswersLastHour("no-such-engine"), 0, 0, 0, 0);
        Assert.Empty(h.Ledger.CallsNow("no-such-engine"));
    }
}
