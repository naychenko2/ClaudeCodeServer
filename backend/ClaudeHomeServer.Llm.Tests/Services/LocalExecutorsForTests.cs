using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// W2a · доработка 1 (ревью R-W2a): шов числа слотов. CheapTextRunner.LocalExecutorsFor сам
// применяет потолок места (maxTotal из стора) и отдаёт исполнителю его слоты числом.
// Что проверяют тесты — инварианты урезания:
// • Σ Parallel ≤ maxTotal; 1 ≤ Parallel ≤ parallel из стора (ничего не придумано сверху);
// • урезание — раунд-робином, а не жадно: при maxTotal=2 и двух движках с parallel 2 слот
//   получает КАЖДЫЙ по одному (жадный проход оставил бы второго без слота при живом бюджете);
// • порядок списка — приоритет: урезание снимает хвост, а не переставляет живых;
// • исполнитель без слота выпадает, а не стоит в списке пустой строкой;
// • урезание идёт ПОСЛЕ фильтра неживых движков: мёртвое имя не съедает слот живого.
// Список, порядок и подписи движка — LocalActionExecutorsStoreTests (не тронуты объёмом).
public class LocalExecutorsForTests
{
    private const string Key = LocalActionCatalog.WikiNightly;

    private static string DataDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static IConfiguration Config(string dir) =>
        TestConfig.Build(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(dir, "projects.json") });

    private static LocalActionExecutorConfig Ex(string engine, int parallel = 1) =>
        new() { Engine = engine, Parallel = parallel };

    private static LocalActionExecutorsConfig Cfg(int maxTotal, params LocalActionExecutorConfig[] executors) =>
        new() { MaxTotal = maxTotal, Executors = executors };

    private static NamedLocalEngines Engines(params (string Name, int MaxConcurrency)[] specs)
    {
        var engines = new List<LocalEngine>();
        foreach (var (name, maxConcurrency) in specs)
        {
            var spec = new LocalEngineSpec(name, null, null, null, null,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)) { MaxConcurrency = maxConcurrency };
            engines.Add(new LocalEngine(spec, new PlainEngineClient(name), null));
        }
        return new NamedLocalEngines(engines);
    }

    // Стенд: ОДИН экземпляр стора и у раннера (в бою — синглтон DI). Реестр движков у стора и
    // у раннера может быть РАЗНЫМ — это и есть расхождение двух снимков (стор перечитан,
    // реестр ещё нет), которое раннер дофильтровывает сам.
    private static (CheapTextRunner Runner, LocalActionExecutorsStore Store) Stand(
        string dir, NamedLocalEngines runnerEngines, NamedLocalEngines? storeEngines = null)
    {
        var config = Config(dir);
        var http = new NoHttp();
        var ollama = new PlainEngineClient("main");
        var overrides = new LocalActionOverridesStore(config, NullLogger<LocalActionOverridesStore>.Instance);
        var router = new LocalActionRouter(ollama, overrides, config, NullLogger<LocalActionRouter>.Instance,
            engines: runnerEngines);
        var store = new LocalActionExecutorsStore(config, NullLogger<LocalActionExecutorsStore>.Instance,
            storeEngines ?? runnerEngines);
        var runner = new CheapTextRunner(router, ollama,
            new CloudCheapClient(http, config, new LlmProviderRegistry(config), NullLogger<CloudCheapClient>.Instance),
            new NoClaude(), NullLogger<CheapTextRunner>.Instance,
            namedEngines: runnerEngines, executorsStore: store);
        return (runner, store);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class NoClaude : IOneShotRunner
    {
        public string? NormalizeModel(string? model) => model;
        public Task<string> RunAsync(string prompt, string? model = null, TimeSpan? timeout = null,
            CancellationToken ct = default, string? ownerId = null, string? effort = null, string? label = null)
            => Task.FromResult("");
        public Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null,
            TimeSpan? timeout = null, CancellationToken ct = default, string? ownerId = null,
            string? effort = null, string? label = null)
            => Task.FromResult(new OneShotResult("", null, 0));
    }

    private sealed class PlainEngineClient(string name) : ILocalLlmClient
    {
        public bool Enabled => true;
        public string BaseUrl => "http://" + name;
        public string Model => name + "-модель";
        public string TextModel => name + "-модель";
        public string ProviderKey => LocalLlmOptions.LlamaServer;

        public Task<string?> ChatJsonAsync(string systemPrompt, string userPrompt, object jsonFormat,
            CancellationToken ct = default, string? model = null, int? timeoutMs = null,
            int? numPredict = null, int? numCtx = null, string? ownerId = null, string? label = null)
            => Task.FromResult<string?>(null);

        public Task<string?> GenerateTextAsync(string prompt, string? model, TimeSpan timeout, int numPredict,
            int numCtx, string? ownerId = null, string? label = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<ChatTurnResult> ChatTurnAsync(IReadOnlyList<ChatMsg> messages, string? model, TimeSpan timeout,
            int numPredict, int numCtx, string? ownerId, Func<string, Task>? onDelta = null,
            CancellationToken ct = default) => Task.FromResult(new ChatTurnResult(null, null));

        public Task WarmUpAsync(string? model, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ─── Раунд-робин, а не жадность ───────────────────────────────────────────────────

    [Fact]
    public void Truncation_RoundRobin_EveryLiveEngineGetsASlot()
    {
        // maxTotal=2, у обоих движков parallel=2: жадный отдал бы всё первому (и второй
        // выпал бы), хотя его очередь стояла живой. Раунд-робин: по одному слоту каждому.
        var (runner, store) = Stand(DataDir(), Engines(("gemma", 2), ("strata", 2)));
        Assert.True(store.Save(Key, Cfg(2, Ex("gemma", 2), Ex("strata", 2))));

        var list = runner.LocalExecutorsFor(Key);
        Assert.Equal(["gemma", "strata"], list.Select(e => e.Engine));
        Assert.Equal([1, 1], list.Select(e => e.Parallel));
        Assert.Equal(2, list.Sum(e => e.Parallel)); // Σ = maxTotal, не больше
    }

    [Fact]
    public void NoTruncation_WhenSumFitsMaxTotal()
    {
        // Сумма parallel (3) меньше потолка места (4): урезания нет, каждый ведёт столько,
        // сколько ему дал админ; неиспользованный остаток бюджета — не чья-то вина.
        var (runner, store) = Stand(DataDir(), Engines(("gemma", 2), ("strata", 1)));
        Assert.True(store.Save(Key, Cfg(4, Ex("gemma", 2), Ex("strata", 1))));

        var list = runner.LocalExecutorsFor(Key);
        Assert.Equal(["gemma", "strata"], list.Select(e => e.Engine));
        Assert.Equal([2, 1], list.Select(e => e.Parallel));
    }

    [Fact]
    public void Truncation_DropsZeroRemainder_KeepsPriorityOrder()
    {
        // maxTotal=2 при трёх движках с parallel 1: третьему слота не досталось совсем —
        // он выпадает из списка, а не стоит в нём с нулём. Порядок первых двух — прежний
        // (приоритет списка урезание не переставляет).
        var (runner, store) = Stand(DataDir(), Engines(("gemma", 1), ("strata", 1), ("bare", 1)));
        Assert.True(store.Save(Key, Cfg(2, Ex("gemma"), Ex("strata"), Ex("bare"))));

        var list = runner.LocalExecutorsFor(Key);
        Assert.Equal(["gemma", "strata"], list.Select(e => e.Engine));
        Assert.Equal([1, 1], list.Select(e => e.Parallel));
    }

    [Fact]
    public void DeadEngine_DoesNotConsumeSlots()
    {
        // Урезание — ПОСЛЕ фильтра неживых: имя, которое пропустил стор (его реестр живее),
        // но которого нет в реестре раннера, не должно съесть слот живого движка.
        var dir = DataDir();
        File.WriteAllText(Path.Combine(dir, "local-action-executors.json"),
            """{"wiki-nightly":{"maxTotal":2,"executors":[{"engine":"ghost","parallel":2},{"engine":"gemma","parallel":2}]}}""");
        var (runner, _) = Stand(dir, Engines(("gemma", 2)), storeEngines: Engines(("gemma", 2), ("ghost", 2)));

        var list = runner.LocalExecutorsFor(Key);
        Assert.Equal(["gemma"], list.Select(e => e.Engine));
        Assert.Equal([2], list.Select(e => e.Parallel)); // весь бюджет — живому
    }
}
