using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// W2a · Стор исполнителей ночи (data/local-action-executors.json): валидатор, атомарная
// запись, чтение. Что проверяют тесты:
// • И3 путь 5 — Validate отвергает каждую чужую форму исполнителя (id модели, tier:,
//   preset:, direct:) и место без флага ParallelExecutors;
// • И3 путь 6 — имя, которого нет среди живых движков (файл правили руками), не становится
//   исполнителем: ни в TryGet, ни в CheapTextRunner.LocalExecutorsFor;
// • потолок parallel — потолок того же движка (MaxConcurrency из LocalEngines:, для
//   «local» — MaxConcurrentTurns провайдера по LocalLlm:EngineKey), а не своё правило;
// • конкурентность — статический замок по пути + temp + rename: после гонки файл валиден и
//   в нём ровно то же, что в памяти стора (мутация «снять замок» краснит);
// • битый JSON — пустой список без исключения наружу (стор читается на старте контейнера).
public class LocalActionExecutorsStoreTests
{
    private const string Key = LocalActionCatalog.WikiNightly;

    private static string DataDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static IConfiguration Config(string dir, Dictionary<string, string?>? extra = null)
    {
        var d = extra ?? [];
        d["DataPath"] = Path.Combine(dir, "projects.json");
        return TestConfig.Build(d);
    }

    private static string StoreFile(string dir) => Path.Combine(dir, "local-action-executors.json");

    private static LocalActionExecutorsStore Store(string dir, NamedLocalEngines? engines = null,
        LlmProviderRegistry? providers = null) =>
        new(Config(dir), NullLogger<LocalActionExecutorsStore>.Instance, engines, providers);

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

    // Минимальный ILocalLlmClient: список движков никого не вызывает, но реестр обязан
    // считать движок живым (Enabled) и знать его модель — из неё складывается Title.
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

    // ─── Validate: И3 путь 5 и границы ────────────────────────────────────────────────

    [Theory]
    // Каждая чужая форма называется отдельно: «неизвестный движок» по id модели или слоту
    // админ прочитал бы как опечатку в имени, а не как отказ от облачного исполнителя.
    [InlineData("claude-sonnet-4-5", "Неизвестный локальный движок")]
    [InlineData("gemma-4-26b-a4b", "Неизвестный локальный движок")]
    [InlineData("tier:strong", "слот тира")]
    [InlineData("preset:11111111-1111-1111-1111-111111111111", "пресет")]
    [InlineData("direct:minimax-m2", "облачный адаптер")]
    public void Validate_ForbiddenExecutorForms_RejectedWithTheirReason(string engine, string reason)
    {
        var errors = Store(DataDir(), Engines(("gemma", 2)))
            .Validate(Key, Cfg(2, Ex(engine)));
        Assert.Contains(errors, e => e.Contains(reason));
    }

    [Fact]
    public void Validate_PlaceWithoutParallelExecutors_Rejected()
    {
        // Список движков разрешён только месту с флагом каталога: у остальных одна работа
        // за один проход цепочки, и их настройка не читалась бы никем.
        var errors = Store(DataDir(), Engines(("gemma", 2)))
            .Validate(LocalActionCatalog.WikiTriage, Cfg(2, Ex("gemma")));
        Assert.Contains(errors, e => e.Contains("ParallelExecutors"));
    }

    [Fact]
    public void Validate_MaxTotalOutsideRange_Rejected()
    {
        var store = Store(DataDir(), Engines(("gemma", 2)));
        Assert.Contains(store.Validate(Key, Cfg(0, Ex("gemma"))), e => e.Contains("maxTotal 0"));
        Assert.Contains(store.Validate(Key, Cfg(5, Ex("gemma"))), e => e.Contains("maxTotal 5"));
        Assert.Empty(store.Validate(Key, Cfg(1, Ex("gemma"))));
        Assert.Empty(store.Validate(Key, Cfg(LocalActionExecutorsStore.MaxTotalLimit, Ex("gemma"))));
    }

    [Fact]
    public void Validate_ParallelAboveEngineCeiling_Rejected_AboveItsOwn()
    {
        // Потолок — LocalEngines:gemma:MaxConcurrency (2), а не общее правило стора:
        // «разрешить 3 на движке с 2 слотами» значило бы очередь вместо ускорения.
        var store = Store(DataDir(), Engines(("gemma", 2)));
        Assert.Empty(store.Validate(Key, Cfg(2, Ex("gemma", 2))));
        Assert.Contains(store.Validate(Key, Cfg(2, Ex("gemma", 3))), e => e.Contains("потолок движка 2"));
        Assert.Contains(store.Validate(Key, Cfg(2, Ex("gemma", 0))), e => e.Contains("не меньше 1"));
        // Пустой список исполнителей — «снять ключ»: maxTotal при нём не проверяется.
        Assert.Empty(store.Validate(Key, Cfg(0)));
    }

    [Fact]
    public void Validate_MainLocalParallelUsesProviderCeiling()
    {
        // «local» — основной LocalLlm: его потолок — MaxConcurrentTurns провайдера по
        // LocalLlm:EngineKey, тот же расчёт, что у счётчика слотов основной локали в DI.
        var dir = DataDir();
        var config = Config(dir, new Dictionary<string, string?>
        {
            ["LocalLlm:EngineKey"] = "local-qwen",
            ["LlmProviders:local-qwen:DisplayName"] = "Local Qwen",
            ["LlmProviders:local-qwen:BaseUrl"] = "http://localhost:8080",
            ["LlmProviders:local-qwen:MaxConcurrentTurns"] = "2",
        });
        var store = new LocalActionExecutorsStore(config,
            NullLogger<LocalActionExecutorsStore>.Instance, providers: new LlmProviderRegistry(config));
        Assert.Empty(store.Validate(Key, Cfg(2, Ex("local", 2))));
        Assert.Contains(store.Validate(Key, Cfg(2, Ex("local", 3))), e => e.Contains("потолок движка 2"));
    }

    [Fact]
    public void Validate_DuplicateEngine_Rejected()
    {
        var errors = Store(DataDir(), Engines(("gemma", 2))).Validate(Key, Cfg(2, Ex("gemma"), Ex("gemma")));
        Assert.Contains(errors, e => e.Contains("назван дважды"));
    }

    // ─── Запись/чтение ────────────────────────────────────────────────────────────────

    [Fact]
    public void SaveAndRead_RoundTrip_KeepsOrderAndFile()
    {
        // Порядок списка = приоритет: свободный слот первого получает чат раньше.
        var dir = DataDir();
        var engines = Engines(("gemma", 2), ("strata", 1));
        var store = Store(dir, engines);

        Assert.True(store.Save(Key, Cfg(2, Ex("gemma", 2), Ex("local", 1))));

        var cfg = store.TryGet(Key);
        Assert.NotNull(cfg);
        Assert.Equal(2, cfg.MaxTotal);
        Assert.Equal(["gemma", "local"], cfg.Executors.Select(e => e.Engine));
        Assert.Equal([2, 1], cfg.Executors.Select(e => e.Parallel));

        // Файл — camelCase, читаемый человеком (его правят и руками).
        var json = File.ReadAllText(StoreFile(dir));
        Assert.Contains("\"maxTotal\"", json);
        Assert.Contains("\"engine\": \"gemma\"", json);

        // Перезапуск: тот же файл даёт тот же список (настройка не эфемерна).
        var after = Store(dir, engines);
        Assert.Equal(["gemma", "local"], after.TryGet(Key)!.Executors.Select(e => e.Engine));
    }

    [Fact]
    public void Save_EmptyList_RemovesKey()
    {
        var dir = DataDir();
        var store = Store(dir, Engines(("gemma", 2)));
        Assert.True(store.Save(Key, Cfg(2, Ex("gemma"))));
        Assert.NotNull(store.TryGet(Key));

        Assert.True(store.Save(Key, Cfg(0)));
        Assert.Null(store.TryGet(Key));
        Assert.DoesNotContain(Key, File.ReadAllText(StoreFile(dir)));
    }

    [Fact]
    public void Save_InvalidConfig_WritesNothing()
    {
        var dir = DataDir();
        var store = Store(dir, Engines(("gemma", 2)));
        Assert.False(store.Save(Key, Cfg(2, Ex("tier:strong"))));
        Assert.Null(store.TryGet(Key));
        Assert.False(File.Exists(StoreFile(dir)));
    }

    [Fact]
    public void Load_HandEditedUnknownEngine_IsSkipped_KnownOneStays()
    {
        // И3 путь 6: имя, которого нет среди живых движков, не становится исполнителем —
        // ни в TryGet, ни в списке раннера. Живое имя при этом остаётся.
        var dir = DataDir();
        File.WriteAllText(StoreFile(dir),
            """{"wiki-nightly":{"maxTotal":3,"executors":[{"engine":"gemma","parallel":1},{"engine":"qwen3.8-27b","parallel":2}]}}""");
        var store = Store(dir, Engines(("gemma", 2)));

        var cfg = store.TryGet(Key);
        Assert.NotNull(cfg);
        Assert.Equal(["gemma"], cfg.Executors.Select(e => e.Engine));
    }

    [Fact]
    public void Load_AllNamesUnknown_NoExecutors()
    {
        var dir = DataDir();
        File.WriteAllText(StoreFile(dir),
            """{"wiki-nightly":{"maxTotal":2,"executors":[{"engine":"gpt-4o","parallel":1}]}}""");
        Assert.Null(Store(dir, Engines(("gemma", 2))).TryGet(Key));
    }

    [Fact]
    public void Load_KeyWithoutParallelFlag_IsIgnored()
    {
        var dir = DataDir();
        File.WriteAllText(StoreFile(dir),
            """{"wiki-triage":{"maxTotal":2,"executors":[{"engine":"gemma","parallel":1}]}}""");
        var store = Store(dir, Engines(("gemma", 2)));
        Assert.Null(store.TryGet(LocalActionCatalog.WikiTriage));
    }

    [Fact]
    public void Load_BrokenJson_EmptyAndNoThrow()
    {
        var dir = DataDir();
        File.WriteAllText(StoreFile(dir), "{\"wiki-nightly\":{\"maxTotal\":2,\"execut");
        var store = Store(dir, Engines(("gemma", 2)));
        Assert.Null(store.TryGet(Key));
        Assert.True(store.Save(Key, Cfg(1, Ex("gemma"))));
    }

    [Fact]
    public void Load_NoFile_TryGetNull()
    {
        Assert.Null(Store(DataDir(), Engines(("gemma", 2))).TryGet(Key));
    }

    [Fact]
    public void ConcurrentSaves_LastWinsAndFileValid()
    {
        // Гонка двух PUT (тот же ключ): статический замок по пути делает «прочитать снимок →
        // изменить → записать» атомарным, поэтому после гонки файл валиден и в нём ровно то
        // же, что в памяти стора. Мутация «снять lock» краснит этот тест: два потока строят
        // снимок из одного prev, и последний в памяти перестаёт быть последним в файле.
        var dir = DataDir();
        var engines = Engines(("e1", 1), ("e2", 1), ("e3", 1), ("e4", 1));
        var store = Store(dir, engines);

        var rounds = Enumerable.Range(1, 4).ToArray();
        Parallel.For(0, rounds.Length, i =>
        {
            for (var r = 0; r < 5; r++)
                store.Save(Key, Cfg(i + 1, Enumerable.Range(1, i + 1)
                    .Select(n => Ex("e" + n)).ToArray()));
        });

        var memory = store.TryGet(Key);
        Assert.NotNull(memory);
        var fromFile = JsonSerializer.Deserialize<Dictionary<string, LocalActionExecutorsConfig>>(
            File.ReadAllText(StoreFile(dir)),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(fromFile);
        Assert.True(fromFile.TryGetValue(Key, out var written));
        Assert.Equal(memory.MaxTotal, written.MaxTotal);
        Assert.Equal(memory.Executors.Select(e => e.Engine), written.Executors.Select(e => e.Engine));
        Assert.Equal(memory.Executors.Select(e => e.Parallel), written.Executors.Select(e => e.Parallel));
    }

    // ─── CheapTextRunner.LocalExecutorsFor: список и подписи ─────────────────────────

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

    // Стенд: ОДИН экземпляр стора и у раннера, и у теста — в бою это синглтон DI, и запись
    // админа должна быть видна раннеру сразу, без перезагрузки.
    private static (CheapTextRunner Runner, LocalActionExecutorsStore Store) Stand(
        string dir, NamedLocalEngines engines, ILocalLlmClient? mainLocal = null)
    {
        var config = Config(dir);
        var http = new NoHttp();
        var ollama = mainLocal ?? new PlainEngineClient("main");
        var overrides = new LocalActionOverridesStore(config, NullLogger<LocalActionOverridesStore>.Instance);
        var router = new LocalActionRouter(ollama, overrides, config, NullLogger<LocalActionRouter>.Instance,
            engines: engines);
        var store = Store(dir, engines);
        var runner = new CheapTextRunner(router, ollama,
            new CloudCheapClient(http, config, new LlmProviderRegistry(config), NullLogger<CloudCheapClient>.Instance),
            new NoClaude(), NullLogger<CheapTextRunner>.Instance, namedEngines: engines, executorsStore: store);
        return (runner, store);
    }

    [Fact]
    public void LocalExecutorsFor_TakesListFromStore_WithEngineTitles()
    {
        // Title — подпись движка для владельца (та же сборка, что LocalActionRouter.
        // EngineLabel: Title → модель → имя), порядок — приоритет, parallel — из стора.
        var dir = DataDir();
        // Три формы подписи движка: явная Title, только модель, ничего — ключ движка
        // (та же цепочка, что LocalActionRouter.EngineLabel).
        static LocalEngineSpec Spec(string name) => new(name, null, null, null, null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var engines = new NamedLocalEngines([
            new LocalEngine(Spec("gemma") with { Title = "Gemma 4 на Video", MaxConcurrency = 2 },
                new PlainEngineClient("gemma"), null),
            new LocalEngine(Spec("strata") with { Model = "strata-модель", MaxConcurrency = 1 },
                new PlainEngineClient("strata"), null),
            new LocalEngine(Spec("bare"), new PlainEngineClient("bare"), null),
        ]);
        // maxTotal = сумма parallel списка: урезания нет, и тест остаётся ровно про список,
        // порядок и подписи. Числа слотов и урезание по maxTotal — LocalExecutorsForTests.
        var (runner, store) = Stand(dir, engines, mainLocal: new PlainEngineClient("main"));
        Assert.True(store.Save(Key, Cfg(4, Ex("gemma"), Ex("strata"), Ex("bare"), Ex("local"))));

        var list = runner.LocalExecutorsFor(Key);
        Assert.Equal(["gemma", "strata", "bare", "local"], list.Select(e => e.Engine));
        // Parallel в записи шва — число слотов этого исполнителя (был признак bool):
        // ровно то, что дал стор, когда урезания не потребовалось.
        Assert.Equal([1, 1, 1, 1], list.Select(e => e.Parallel));
        Assert.Equal("Gemma 4 на Video", list[0].Title);
        Assert.Equal("strata-модель", list[1].Title);
        Assert.Equal("bare", list[2].Title);
        // Основная локаль: подписи движка нет — её именем становится модель, на которую она
        // ходит (TextModel основной локали).
        Assert.Equal("main-модель", list[3].Title);
    }

    [Fact]
    public void LocalExecutorsFor_NoStoreEntry_IsEmpty()
    {
        // Пустой список = сегодняшнее поведение: место идёт по своему маршруту
        // (RunLocalOrChosen*), а не «куда-нибудь»: конфиг, назначающий месту один движок,
        // здесь подмены не даёт.
        var (runner, _) = Stand(DataDir(), Engines(("gemma", 2)));
        Assert.Empty(runner.LocalExecutorsFor(Key));
    }

    [Fact]
    public void LocalExecutorsFor_HandEditedUnknownEngine_NotInList()
    {
        // И3 путь 6 на стороне вызывающего: ночь не получает исполнителя, которого нет
        // (файл правили руками — валидатор PUT его не видал).
        var dir = DataDir();
        File.WriteAllText(StoreFile(dir),
            """{"wiki-nightly":{"maxTotal":2,"executors":[{"engine":"gemma","parallel":1},{"engine":"claude-sonnet-4-5","parallel":1}]}}""");
        var (runner, _) = Stand(dir, Engines(("gemma", 2)));
        Assert.Equal(["gemma"], runner.LocalExecutorsFor(Key).Select(e => e.Engine));
    }
}
