using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// W1b · Прогон на названном локальном движке (RunOnLocalEngineDetailedAsync /
// IsLocalEngineReadyAsync): исполнитель задан аргументом, а не маршрутом места.
// Что ловят тесты (И3, пути 2 и 4): метод не доходит до облака — неизвестный движок и
// движок с direct-страховкой, не ответивший, дают null при 0 вызовов облака и claude;
// два вызова на движок с MaxConcurrency=1 — второй ждёт слот в общем счётчике и не
// уходит в другой движок; исключение движка — null наружу не летит, отмена по ct — летит.
public class CheapTextRunnerLocalEngineTests
{
    private const string Engine = "gemma";
    private const string FallbackModel = "fallback-model";

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

    // Клиент движка: отдаёт answer (null — «движок не ответил»), считает вызовы;
    // throws — «движок дал сбой»; withUsage — метрики как у живого движка (стоимости у них нет).
    private sealed class FakeEngineClient(string? answer = "ответ движка", bool enabled = true,
        bool throws = false, bool withUsage = false) : ILocalLlmClient
    {
        public int Calls;
        public bool Enabled => enabled;
        public string BaseUrl => "http://engine";
        public string Model => "gemma-4-26b-a4b";
        public string TextModel => "gemma-4-26b-a4b";
        public string ProviderKey => LocalLlmOptions.LlamaServer;

        public Task<string?> ChatJsonAsync(string systemPrompt, string userPrompt, object jsonFormat,
            CancellationToken ct = default, string? model = null, int? timeoutMs = null, int? numPredict = null,
            int? numCtx = null, string? ownerId = null, string? label = null)
        { Calls++; return Task.FromResult(answer); }

        public Task<string?> GenerateTextAsync(string prompt, string? model, TimeSpan timeout, int numPredict,
            int numCtx, string? ownerId = null, string? label = null, CancellationToken ct = default)
        {
            Calls++;
            if (throws) throw new InvalidOperationException("движок дал сбой");
            return Task.FromResult(answer);
        }

        public async Task<(string? Text, UsageInfo? Usage)> GenerateTextDetailedAsync(string prompt, string? model,
            TimeSpan timeout, int numPredict, int numCtx, string? ownerId = null, string? label = null,
            CancellationToken ct = default)
        {
            var text = await GenerateTextAsync(prompt, model, timeout, numPredict, numCtx, ownerId, label, ct);
            return withUsage && text is not null
                ? (text, new UsageInfo(120, 30, 0, 0))
                : (text, null);
        }

        public Task<ChatTurnResult> ChatTurnAsync(IReadOnlyList<ChatMsg> messages, string? model, TimeSpan timeout,
            int numPredict, int numCtx, string? ownerId, Func<string, Task>? onDelta = null,
            CancellationToken ct = default)
            => Task.FromResult(new ChatTurnResult(answer, null));

        public Task WarmUpAsync(string? model, CancellationToken ct = default) => Task.CompletedTask;
    }

    // Клиент движка с семафором: первый вызов не возвращается, пока тест не откроет затвор.
    // Так тест видит «второй стоит в очереди слота, до движка не дошёл».
    private sealed class GatedEngineClient : ILocalLlmClient
    {
        public int Reached;
        private readonly SemaphoreSlim Gate = new(0, 1);
        public bool Enabled => true;
        public string BaseUrl => "http://engine";
        public string Model => "gemma-4-26b-a4b";
        public string TextModel => "gemma-4-26b-a4b";
        public string ProviderKey => LocalLlmOptions.LlamaServer;

        public async Task<string?> GenerateTextAsync(string prompt, string? model, TimeSpan timeout, int numPredict,
            int numCtx, string? ownerId = null, string? label = null, CancellationToken ct = default)
        {
            Reached++;
            if (Reached > 1) return "второй";
            await Gate.WaitAsync(ct);
            return "первый";
        }

        public Task<string?> ChatJsonAsync(string systemPrompt, string userPrompt, object jsonFormat,
            CancellationToken ct = default, string? model = null, int? timeoutMs = null, int? numPredict = null,
            int? numCtx = null, string? ownerId = null, string? label = null)
            => Task.FromResult<string?>(null);

        public Task<ChatTurnResult> ChatTurnAsync(IReadOnlyList<ChatMsg> messages, string? model, TimeSpan timeout,
            int numPredict, int numCtx, string? ownerId, Func<string, Task>? onDelta = null,
            CancellationToken ct = default)
            => Task.FromResult(new ChatTurnResult(null, null));

        public Task WarmUpAsync(string? model, CancellationToken ct = default) => Task.CompletedTask;

        public void ReleaseFirst() => Gate.Release();
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

    // Окно фикстуры — 32768, а не NumPredict мест вики (8192, Q1-S14): на окне, равном
    // NumPredict, бюджет входа (numCtx − numPredict)·3 − запас нулевой — fail-closed
    // места отказывает такой вызов до обращения к движку (Q1-S10). Здесь же проверяются слот,
    // транспорт и usage прогона на названном движке, а не вместимость окна: окно фикстуры
    // обязано быть тем, которое эту работу действительно принимает.
    private static NamedLocalEngines Engines(ILocalLlmClient client, bool ready = true, string? fallback = null,
        Func<CancellationToken, Task<bool>>? probe = null) =>
        new([new LocalEngine(
            new LocalEngineSpec(Engine, ReadyUrl: "http://arbiter/ready", StateUrl: null,
                FallbackRoute: fallback, NumCtx: 32_768,
                Actions: new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            client, probe ?? (_ => Task.FromResult(ready)))]);

    // Движок под общим счётчиком слотов с потолком 1 — физика той же очереди, что в DI.
    private static NamedLocalEngines TrackedEngine(ILocalLlmClient inner, LocalEngineBusyTracker busy) =>
        Engines(new TrackedLocalLlmClient(inner, busy, Engine, () => 1,
            NullLogger<TrackedLocalLlmClient>.Instance));

    // Стенд: именованный движок и основная локаль — как даны (не дана — не настроена);
    // прямой облачный источник настроен и считается counting-хендлером, чтобы «0 вызовов»
    // было доказуемо; трекер занятости — свой, глобальный Instance между тестами не делим.
    private static (CheapTextRunner Runner, CountingHttpFactory Http, FakeOneShot Claude, LocalActionRouter Router)
        Stand(NamedLocalEngines? engines = null, ILocalLlmClient? mainLocal = null,
            LocalEngineBusyTracker? busy = null)
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
            busy: busy, engines: engines);
        var cloud = new CloudCheapClient(http, config, new LlmProviderRegistry(config),
            NullLogger<CloudCheapClient>.Instance);
        var claude = new FakeOneShot();
        var runner = new CheapTextRunner(router, ollama, cloud, claude,
            NullLogger<CheapTextRunner>.Instance, namedEngines: engines);
        return (runner, http, claude, router);
    }

    [Fact]
    public async Task Run_UnknownEngine_ReturnsNull_CloudAndClaudeNotCalled()
    {
        // Путь 2 (И3): неизвестный движок — отказ окна без подмены другим движком,
        // облаком или claude.
        var engine = new FakeEngineClient();
        var (runner, http, claude, _) = Stand(engines: Engines(engine));
        var result = await runner.RunOnLocalEngineDetailedAsync("нет-такого", LocalActionCatalog.WikiNightly, "p");
        Assert.Null(result);
        Assert.Equal(0, engine.Calls);
        Assert.Equal(0, http.Requests);
        Assert.Empty(claude.Calls);
    }

    [Fact]
    public async Task Run_DisabledEngineOrDisabledMainLocal_ReturnsNull()
    {
        var engine = new FakeEngineClient(enabled: false);
        var (runner, http, claude, _) = Stand(engines: Engines(engine));
        Assert.Null(await runner.RunOnLocalEngineDetailedAsync(Engine, LocalActionCatalog.WikiNightly, "p"));
        Assert.Equal(0, engine.Calls);
        // Основная локаль не настроена — «local» даёт тот же null, без облачного шага.
        Assert.Null(await runner.RunOnLocalEngineDetailedAsync("local", LocalActionCatalog.WikiNightly, "p"));
        Assert.Equal(0, http.Requests);
        Assert.Empty(claude.Calls);
    }

    [Fact]
    public async Task Run_EngineNotReady_NotCalled_ReturnsNull()
    {
        // Арбитр GPU выгрузил движок: проба готовности отказывает до вызова — движок не
        // звался вовсе, подмены нет.
        var engine = new FakeEngineClient();
        var (runner, http, claude, _) = Stand(engines: Engines(engine, ready: false));
        Assert.Null(await runner.RunOnLocalEngineDetailedAsync(Engine, LocalActionCatalog.WikiNightly, "p"));
        Assert.Equal(0, engine.Calls);
        Assert.Equal(0, http.Requests);
        Assert.Empty(claude.Calls);
    }

    [Fact]
    public async Task Run_EngineWithDirectFallbackDidNotAnswer_NullCloudNotCalled()
    {
        // Путь 4 (И3): у движка есть direct-страховка, но этот метод её не знает по построению:
        // движок не ответил — null, облако не звалось ни разу.
        var engine = new FakeEngineClient(answer: null);
        var (runner, http, claude, _) = Stand(engines: Engines(engine, fallback: $"direct:{FallbackModel}"));
        var result = await runner.RunOnLocalEngineDetailedAsync(Engine, LocalActionCatalog.WikiNightly, "p");
        Assert.Null(result);
        Assert.Equal(1, engine.Calls);
        Assert.Equal(0, http.Requests);
        Assert.Empty(claude.Calls);
    }

    [Fact]
    public async Task Run_EngineAnswered_UsageIsLocalFree()
    {
        var engine = new FakeEngineClient(answer: "ответ ночи", withUsage: true);
        var (runner, http, claude, _) = Stand(engines: Engines(engine));
        var result = await runner.RunOnLocalEngineDetailedAsync(Engine, LocalActionCatalog.WikiNightly, "p");
        Assert.Equal("ответ ночи", result!.Text);
        Assert.Equal(120, result.Usage!.InputTokens);
        Assert.Equal(30, result.Usage.OutputTokens);
        Assert.Equal(0, result.Usage.CostUsd);
        Assert.Equal("gemma-4-26b-a4b", result.Usage.Model);
        Assert.Equal(0, http.Requests);
        Assert.Empty(claude.Calls);
    }

    [Fact]
    public async Task Run_MainLocal_CallsOnlyMainClient()
    {
        // «local» — основной LocalLlm, а не именованный движок: именованный не звался
        // (прогон на названном движке не ходит по движкам).
        var main = new FakeEngineClient(answer: "основная локаль");
        var named = new FakeEngineClient(answer: "именованный");
        var (runner, http, claude, _) = Stand(engines: Engines(named), mainLocal: main);
        var result = await runner.RunOnLocalEngineDetailedAsync("local", LocalActionCatalog.WikiNightly, "p");
        Assert.Equal("основная локаль", result!.Text);
        Assert.Equal(1, main.Calls);
        Assert.Equal(0, named.Calls);
        Assert.Equal(0, http.Requests);
        Assert.Empty(claude.Calls);
    }

    [Fact]
    public async Task Run_EngineThrows_ReturnsNullNotThrown()
    {
        // «Исключение движка → null, наружу не летит»: подмены нет, решение за вызывающим.
        var engine = new FakeEngineClient(throws: true);
        var (runner, http, claude, _) = Stand(engines: Engines(engine));
        var result = await runner.RunOnLocalEngineDetailedAsync(Engine, LocalActionCatalog.WikiNightly, "p");
        Assert.Null(result);
        Assert.Equal(1, engine.Calls);
        Assert.Equal(0, http.Requests);
        Assert.Empty(claude.Calls);
    }

    [Fact]
    public async Task Run_CancellationByCt_Propagates()
    {
        // Отмена по ct — не отказ чата, а конец прохода: пробрасывается наружу (W3/W4
        // ловят её как остановку ночи), а не превращаются в null.
        var inner = new GatedEngineClient();
        var busy = new LocalEngineBusyTracker();
        var (runner, _, _, _) = Stand(engines: TrackedEngine(inner, busy), busy: busy);

        var first = runner.RunOnLocalEngineDetailedAsync(Engine, LocalActionCatalog.WikiNightly, "p1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runner.RunOnLocalEngineDetailedAsync(Engine, LocalActionCatalog.WikiNightly, "p2", ct: cts.Token));
        inner.ReleaseFirst();
        Assert.Equal("первый", (await first)!.Text);
    }

    [Fact]
    public async Task TwoCallsOneSlot_SecondWaits()
    {
        // Два вызова на движок с MaxConcurrency=1: второй ждёт слот в общем счётчике
        // (тот же TrackedLocalLlmClient, что у ходов), в другой движок, в облако и в claude
        // не уходит. Мутация «не ждать слот» (обычный вызов вместо AwaitingSlot) — красный:
        // второй не дошёл бы до движка и вернулся бы null.
        var inner = new GatedEngineClient();
        var named = new FakeEngineClient(answer: "именованный");
        var busy = new LocalEngineBusyTracker();
        var (runner, http, claude, _) = Stand(engines: TrackedEngine(inner, busy), mainLocal: named, busy: busy);

        var first = runner.RunOnLocalEngineDetailedAsync(Engine, LocalActionCatalog.WikiNightly, "p1");
        var second = runner.RunOnLocalEngineDetailedAsync(Engine, LocalActionCatalog.WikiNightly, "p2");

        // Первый дошёл до движка и держит слот; второй стоит в очереди: до движка не дошёл,
        // ни в облако, ни в claude, ни в основную локаль не ушёл.
        Assert.Equal(1, inner.Reached);
        Assert.False(second.IsCompleted);
        Assert.Equal(0, named.Calls);
        Assert.Equal(0, http.Requests);
        Assert.Empty(claude.Calls);

        inner.ReleaseFirst();
        Assert.Equal("первый", (await first)!.Text);
        Assert.Equal("второй", (await second)!.Text);
        Assert.Equal(2, inner.Reached);
    }

    [Fact]
    public async Task Run_JsonFormat_GoesThroughChatJson()
    {
        // jsonFormat — structured output того же движка (тот же выбор транспорта, что у
        // цепочки), без облачного шага.
        var engine = new FakeEngineClient(answer: "{\"ok\":true}");
        var (runner, http, claude, _) = Stand(engines: Engines(engine));
        var result = await runner.RunOnLocalEngineDetailedAsync(
            Engine, LocalActionCatalog.WikiNightly, "p", jsonFormat: "json");
        Assert.Equal("{\"ok\":true}", result!.Text);
        Assert.Equal(1, engine.Calls);
        Assert.Equal(0, http.Requests);
        Assert.Empty(claude.Calls);
    }

    [Fact]
    public async Task Run_ImageAction_ThrowsLikeOtherRunners()
    {
        // Место с картинками на входе текстовый раннер не выполняет нигде (ADR-027 §2) —
        // и прогон на названном движке не исключение.
        var engine = new FakeEngineClient();
        var (runner, _, _, _) = Stand(engines: Engines(engine));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunOnLocalEngineDetailedAsync(Engine, LocalActionCatalog.CharacterPhotoEmotion, "p"));
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task IsReady_UnknownOrDisabledEngine_False()
    {
        var (runner, _, _, _) = Stand(engines: Engines(new FakeEngineClient(), ready: true));
        Assert.False(await runner.IsLocalEngineReadyAsync("нет-такого"));
        var disabled = Stand(engines: Engines(new FakeEngineClient(enabled: false)));
        Assert.False(await disabled.Runner.IsLocalEngineReadyAsync(Engine));
    }

    [Fact]
    public async Task IsReady_EngineWithoutReadyUrl_True()
    {
        // Без ReadyUrl пробы нет: «считаем готовым», решит сам вызов.
        var engines = new NamedLocalEngines([new LocalEngine(
            new LocalEngineSpec(Engine, ReadyUrl: null, StateUrl: null, FallbackRoute: null, NumCtx: null,
                Actions: new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            new FakeEngineClient(), null)]);
        var (runner, _, _, _) = Stand(engines: engines);
        Assert.True(await runner.IsLocalEngineReadyAsync(Engine));
    }

    [Fact]
    public async Task IsReady_ByProbeResult_FalseTrue()
    {
        var off = Stand(engines: Engines(new FakeEngineClient(), ready: false));
        Assert.False(await off.Runner.IsLocalEngineReadyAsync(Engine));
        var on = Stand(engines: Engines(new FakeEngineClient(), ready: true));
        Assert.True(await on.Runner.IsLocalEngineReadyAsync(Engine));
    }

    [Fact]
    public async Task IsReady_CachesProbeForOneSecond()
    {
        // Кэш LocalEngine.IsReadyAsync (1 с): фон бьёт десятками мест подряд, проба — HTTP.
        // Делегируя пробу, метод не должен звать её на каждый вопрос.
        var probes = 0;
        var engines = Engines(new FakeEngineClient(),
            probe: _ => { probes++; return Task.FromResult(true); });
        var (runner, _, _, _) = Stand(engines: engines);
        Assert.True(await runner.IsLocalEngineReadyAsync(Engine));
        Assert.True(await runner.IsLocalEngineReadyAsync(Engine));
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task IsReady_MainLocal_FreeTrue_DuringTurnFalse_DisabledFalse()
    {
        // Основная локаль: «выгружен» — не настроенный LocalLlm, «занят ходом исполнителя» —
        // признак роутера (фоновые слоты его не взводят).
        var main = new FakeEngineClient();
        var busy = new LocalEngineBusyTracker();
        var (runner, _, _, _) = Stand(mainLocal: main, busy: busy);
        Assert.True(await runner.IsLocalEngineReadyAsync("local"));
        using (busy.Enter())
            Assert.False(await runner.IsLocalEngineReadyAsync("local"));
        var off = Stand(mainLocal: new FakeEngineClient(enabled: false));
        Assert.False(await off.Runner.IsLocalEngineReadyAsync("local"));
    }
}
