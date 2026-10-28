using System.Reflection;
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

    // Клиент движка с барьером: отпускает ровно `arrivals` запросов одновременно — чтобы EndOf
    // журнала пришёл плотной пачкой на threads, а не по одному. Без sleep и таймингов.
    private sealed class BarrierEngineClient(int arrivals, string answer) : ILocalLlmClient
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public bool Enabled => true;
        public string BaseUrl => "http://engine";
        public string Model => "bonsai-9b";
        public string TextModel => "bonsai-9b";
        public string ProviderKey => LocalLlmOptions.LlamaServer;

        public Task<string?> ChatJsonAsync(string systemPrompt, string userPrompt, object jsonFormat,
            CancellationToken ct = default, string? model = null, int? timeoutMs = null, int? numPredict = null,
            int? numCtx = null, string? ownerId = null, string? label = null)
            => Task.FromResult<string?>(answer);

        public Task<string?> GenerateTextAsync(string prompt, string? model, TimeSpan timeout, int numPredict,
            int numCtx, string? ownerId = null, string? label = null, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _arrived) == arrivals) _all.SetResult();
            return AwaitBarrier();
        }

        private async Task<string?> AwaitBarrier()
        {
            await _all.Task;
            return answer;
        }

        public Task<ChatTurnResult> ChatTurnAsync(IReadOnlyList<ChatMsg> messages, string? model, TimeSpan timeout,
            int numPredict, int numCtx, string? ownerId, Func<string, Task>? onDelta = null,
            CancellationToken ct = default)
            => Task.FromResult(new ChatTurnResult(answer, null));

        public Task WarmUpAsync(string? model, CancellationToken ct = default) => Task.CompletedTask;
    }

    // Клиент движка, ждущий пока тест разрешит ответить: цепочка стоит в полёте внутри
    // запроса. `Entered` — факт, что запрос дошёл до клиента; `Release` — ответ (null —
    // «движок не ответил»). Без sleep: синхронизация по TCS.
    private sealed class HeldEngineClient(string? answer) : ILocalLlmClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string?> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public void Release() => _release.SetResult(answer);

        public bool Enabled => true;
        public string BaseUrl => "http://engine";
        public string Model => "bonsai-9b";
        public string TextModel => "bonsai-9b";
        public string ProviderKey => LocalLlmOptions.LlamaServer;

        public Task<string?> ChatJsonAsync(string systemPrompt, string userPrompt, object jsonFormat,
            CancellationToken ct = default, string? model = null, int? timeoutMs = null, int? numPredict = null,
            int? numCtx = null, string? ownerId = null, string? label = null)
            => Task.FromResult(answer);

        public async Task<string?> GenerateTextAsync(string prompt, string? model, TimeSpan timeout, int numPredict,
            int numCtx, string? ownerId = null, string? label = null, CancellationToken ct = default)
        {
            _entered.TrySetResult();
            return await _release.Task;
        }

        public Task<ChatTurnResult> ChatTurnAsync(IReadOnlyList<ChatMsg> messages, string? model, TimeSpan timeout,
            int numPredict, int numCtx, string? ownerId, Func<string, Task>? onDelta = null,
            CancellationToken ct = default)
            => Task.FromResult(new ChatTurnResult(answer, null));

        public Task WarmUpAsync(string? model, CancellationToken ct = default) => Task.CompletedTask;
    }

    // Клиент движка, ждущий отмены ct вызывающего: обрыв извне, в полёте, не таймаут.
    // `Entered` — факт, что запрос дошёл до клиента (цепочка уже в журнале с звеном движка).
    private sealed class CancelOnCancelClient : ILocalLlmClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public bool Enabled => true;
        public string BaseUrl => "http://engine";
        public string Model => "bonsai-9b";
        public string TextModel => "bonsai-9b";
        public string ProviderKey => LocalLlmOptions.LlamaServer;

        public Task<string?> ChatJsonAsync(string systemPrompt, string userPrompt, object jsonFormat,
            CancellationToken ct = default, string? model = null, int? timeoutMs = null, int? numPredict = null,
            int? numCtx = null, string? ownerId = null, string? label = null)
            => Task.FromResult<string?>(null);

        public async Task<string?> GenerateTextAsync(string prompt, string? model, TimeSpan timeout, int numPredict,
            int numCtx, string? ownerId = null, string? label = null, CancellationToken ct = default)
        {
            _entered.TrySetResult();
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            return await tcs.Task;
        }

        public Task<ChatTurnResult> ChatTurnAsync(IReadOnlyList<ChatMsg> messages, string? model, TimeSpan timeout,
            int numPredict, int numCtx, string? ownerId, Func<string, Task>? onDelta = null,
            CancellationToken ct = default)
            => Task.FromResult(new ChatTurnResult(null, null));

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
        EnginesWith(new FakeEngineClient(engineAnswer), fallback);

    // Движок с произвольным клиентом — тесты параллельности (barrier), отмены и удержания
    // цепочки в полёте: тот же состав, что выше, только клиент свой.
    private static NamedLocalEngines EnginesWith(ILocalLlmClient client, string? fallback = null) =>
        new([new LocalEngine(
            new LocalEngineSpec(Engine, ReadyUrl: null, StateUrl: null,
                FallbackRoute: fallback, NumCtx: 8192,
                Actions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { LocalActionCatalog.ChatTitle }),
            client, readyProbe: null)]);

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

    // === Потокобезопасность журнала (ревью Б5, п.1) ===

    [Fact]
    public async Task ConcurrentCalls_AllCountedOnce_NoOpenCalls()
    {
        // 20 параллельных цепочек на одном движке. Барьерный клиент отпускает их одновременно —
        // EndOf журнала приходит плотной пачкой на threads, а не по одному. Инкремент корзины
        // обязан быть под тем же лока, что и проверка Closed: вынесенный из-под лока, он
        // ломает счёт (проверено мутацией: второй End из finally досчитывал к корзине —
        // сумма 40 вместо 20). Каждый завершённый вызов учтён ровно один раз; в «кто сейчас»
        // не остаётся открытых. Без sleep и таймингов — синхронизация барьером.
        const int calls = 20;
        var h = Make(EnginesWith(new BarrierEngineClient(calls, "ответ движка")));
        var tasks = new Task<string>[calls];
        for (var i = 0; i < calls; i++)
            tasks[i] = Task.Run(() => h.Runner.RunAsync(LocalActionCatalog.ChatTitle, "prompt",
                fallbackModel: "haiku"));
        var texts = await Task.WhenAll(tasks);
        Assert.All(texts, t => Assert.Equal("ответ движка", t));
        var a = h.Ledger.AnswersLastHour(Engine);
        Assert.Equal(calls, a.Bonsai + a.Insurance + a.Claude + a.Missed); // сумма корзин — число вызовов
        Assert.Equal(calls, a.Bonsai);                                    // все ответы — в корзине движка
        Assert.Empty(h.Ledger.CallsNow(Engine));                        // открытых вызовов не осталось
    }

    // === Внешняя отмена вызова (ревью Б5, п.3) ===

    [Fact]
    public async Task ExternalCancel_MidFlight_MissedAndNoOpenCalls()
    {
        // Вызов в полёте (запрос дошёл до клиента, цепочка в журнале с звеном движка) — ct
        // отменён извне. Отмена — не сбой модели: цепочка не продолжается, но открытый вызов
        // обязан закрыться «без ответа» и не остаться в «кто сейчас» призраком.
        var client = new CancelOnCancelClient();
        var h = Make(EnginesWith(client));
        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => h.Runner.RunAsync(LocalActionCatalog.ChatTitle, "prompt",
            fallbackModel: "haiku", ct: cts.Token));
        await client.Entered;
        cts.Cancel();
        // ThrowsAny: боевой HTTP-клиент бросает TaskCanceledException (наследник
        // OperationCanceledException) — проверяю базовый тип, а не конкретную наследника.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        AssertBaskets(h.Ledger.AnswersLastHour(Engine), 0, 0, 0, 1);
        Assert.Empty(h.Ledger.CallsNow(Engine));
    }

    // === Звено Claude — только у цепочек, реально идущих в облако (ревью Б5, п.4) ===

    // Для теста: открытый вызов движка по месту. Закрытый вызов уходит из «кто сейчас», но
    // объект остаётся — его звено читается post-factum, когда цепочка уже завершилась.
    private static EngineCallLedger.OpenCall OpenCallOf(EngineCallLedger ledger, string engine, string actionKey)
    {
        var byEngine = (Dictionary<string, EngineCallLedger.EngineLedger>)typeof(EngineCallLedger)
            .GetField("_byEngine", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ledger)!;
        return byEngine[engine].Open.Single(c => c.ActionKey == actionKey);
    }

    [Fact]
    public async Task NoCloudMode_NeverStepsClaude()
    {
        // Режим «без облака» (RunLocalOrChosenAsync) с движком: цепочка обрывается без ответа
        // и в облако не идёт — ни страховка (здесь она есть в конфиге), ни claude не зовутся.
        // Открытый вызов захвачен объектом, пока цепочка держится в клиенте; его звено после
        // цепочки не должно быть Claude: до правки Step(Claude) стоял до раннего выхода и
        // «кто сейчас» показывал Claude у места, которое в облако не уходит.
        var client = new HeldEngineClient(null);
        var h = Make(EnginesWith(client, "direct:nvidia/nemotron:free"));
        var run = Task.Run(() => h.Runner.RunLocalOrChosenAsync(LocalActionCatalog.ChatTitle, "prompt"));
        await client.Entered;
        var open = OpenCallOf(h.Ledger, Engine, LocalActionCatalog.ChatTitle);
        client.Release();
        Assert.Null(await run);
        Assert.NotEqual(EngineCallLink.Claude, open.Link);
        AssertBaskets(h.Ledger.AnswersLastHour(Engine), 0, 0, 0, 1);
    }
}
