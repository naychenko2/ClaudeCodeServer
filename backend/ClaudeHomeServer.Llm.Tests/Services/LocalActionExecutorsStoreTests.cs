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
// • битый JSON — пустой список без исключения наружу (стор читается на старте контейнера);
// • null-запись в правленном руками файле («"wiki-nightly": null», «"executors": null») —
//   не становится исполнителем и не роняет чтение;
// • та же судьба у битой СТРОКИ списка: «"executors":[null]» и элемент без имени —
//   пропуск с Warning, а не NRE в TryGet / LocalExecutorsFor / GET /api/usage;
// • повтор имени движка в правленном руками файле: остаётся ПЕРВАЯ строка (порядок =
//   приоритет), дубли выпадают — иначе два кандидата на одном физическом движке;
// • числа из правленного руками файла урезаются при чтении до тех же границ, что держит
//   Validate: maxTotal 1..MaxTotalLimit, parallel 1..потолок движка.
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

    [Theory]
    // Файл правят руками: null вместо записи или вместо списка доходит до стора —
    // десериализация ставит null поверх non-nullable и инициализаторов. Битая запись не
    // должна ни становиться исполнителем, ни ронять чтение (И3, путь 6): TryGet на ней
    // возвращает null, а не бросает, и живая запись после неё пишется как обычно.
    [InlineData("""{"wiki-nightly":null}""")]
    [InlineData("""{"wiki-nightly":{"maxTotal":2,"executors":null}}""")]
    [InlineData("""{"wiki-triage":null,"wiki-nightly":{"maxTotal":2,"executors":null}}""")]
    public void Load_NullRecordOrNullList_TryGetNullWithoutThrow(string json)
    {
        var dir = DataDir();
        File.WriteAllText(StoreFile(dir), json);
        var store = Store(dir, Engines(("gemma", 2)));

        Assert.Null(store.TryGet(Key));
        Assert.Null(store.TryGet(LocalActionCatalog.WikiTriage));

        // Чтение не сломано битой строкой: живая запись после неё работает.
        Assert.True(store.Save(Key, Cfg(1, Ex("gemma"))));
        Assert.NotNull(store.TryGet(Key));
    }

    [Theory]
    // MAJOR по сверке W2a: десериализация ставит null и ВНУТРИ списка («"executors":[null]»),
    // а «{"engine":""}» — запись без имени. До фильтра в Load такой элемент доходил до
    // TryGet как NRE на e.Engine: падал и GET /api/usage (DescribeExecutors), и каждый тик
    // ночи (LocalExecutorsFor внутри try прохода — дата не закрывалась, падение повторное).
    [InlineData("""{"wiki-nightly":{"maxTotal":2,"executors":[null]}}""")]
    [InlineData("""{"wiki-nightly":{"maxTotal":2,"executors":[{"engine":null,"parallel":1}]}}""")]
    [InlineData("""{"wiki-nightly":{"maxTotal":2,"executors":[{"engine":"","parallel":1},{"engine":"   "}]}}""")]
    public void Load_AllElementsBroken_TryGetNullWithoutThrow(string json)
    {
        var dir = DataDir();
        File.WriteAllText(StoreFile(dir), json);
        var store = Store(dir, Engines(("gemma", 2)));

        // Живых исполнителей нет — то же «нет списка», что при пустом списке: null, не бросок.
        Assert.Null(store.TryGet(Key));
        // Чтение не сломано: живая запись после битого файла работает как обычно.
        Assert.True(store.Save(Key, Cfg(1, Ex("gemma"))));
        Assert.NotNull(store.TryGet(Key));
    }

    [Theory]
    [InlineData("""{"wiki-nightly":{"maxTotal":2,"executors":[{"engine":"local","parallel":1},null]}}""", "local")]
    [InlineData("""{"wiki-nightly":{"maxTotal":2,"executors":[null,{"engine":"gemma","parallel":1}]}}""", "gemma")]
    [InlineData("""{"wiki-nightly":{"maxTotal":2,"executors":[{"engine":"local","parallel":1},{"engine":""},{"engine":null}]}}""", "local")]
    public void Load_BrokenElementAmongLive_KeepsOnlyLiveOnes(string json, string expectedEngine)
    {
        var dir = DataDir();
        File.WriteAllText(StoreFile(dir), json);
        var store = Store(dir, Engines(("gemma", 2)));

        var cfg = store.TryGet(Key);
        Assert.NotNull(cfg);
        // Ровно тот инвариант, на котором стоит DescribeExecutors (UsageController.cs:184:
        // cfg.Executors.Select(e => e.Engine…)): в списке нет null и нет пустого имени,
        // порядок живых — прежний.
        Assert.Equal([expectedEngine], cfg.Executors.Select(e => e.Engine));
        Assert.All(cfg.Executors, e => Assert.False(string.IsNullOrWhiteSpace(e.Engine)));
    }

    [Theory]
    // Файл правят руками: строку копируют. Validate при записи повтор запрещает (:131), в
    // чтении его держит Load: остаётся ПЕРВАЯ строка — порядок списка есть приоритет, а две
    // строки одного движка дали бы два кандидата в LocalExecutorsFor и до 2× его потолка
    // слотов на одном физическом движке. Регистр имени не меняет (тот же ключ, что у Validate).
    [InlineData("""{"wiki-nightly":{"maxTotal":4,"executors":[{"engine":"gemma","parallel":1},{"engine":"local","parallel":1},{"engine":"local","parallel":2},{"engine":"gemma","parallel":2}]}}""")]
    [InlineData("""{"wiki-nightly":{"maxTotal":4,"executors":[{"engine":"gemma","parallel":1},{"engine":"Gemma","parallel":2},{"engine":"local","parallel":1},{"engine":"LOCAL","parallel":2}]}}""")]
    public void Load_DuplicateEngine_KeepsFirstLineOthersInOrder(string json)
    {
        var dir = DataDir();
        File.WriteAllText(StoreFile(dir), json);
        var store = Store(dir, Engines(("gemma", 2)));

        var cfg = store.TryGet(Key);
        Assert.NotNull(cfg);
        // Первая строка каждого имени — со своими числами (parallel 1), хвостовые дубли
        // выпали; порядок остальных имён — прежний.
        Assert.Equal(["gemma", "local"], cfg.Executors.Select(e => e.Engine));
        Assert.Equal([1, 1], cfg.Executors.Select(e => e.Parallel));
    }

    [Fact]
    public void Load_HandEditedNumbers_ClampedToTheBoundsValidateEnforces()
    {
        // Файл правят руками: maxTotal 9 и parallel 0/9 проходят в него без валидатора.
        // При чтении берутся те же границы, что держит Validate при записи: maxTotal 1..4,
        // parallel 1..потолок движка (для «local» — MaxConcurrentTurns провайдера, здесь 2).
        var dir = DataDir();
        var config = Config(dir, new Dictionary<string, string?>
        {
            ["LocalLlm:EngineKey"] = "local-qwen",
            ["LlmProviders:local-qwen:DisplayName"] = "Local Qwen",
            ["LlmProviders:local-qwen:BaseUrl"] = "http://localhost:8080",
            ["LlmProviders:local-qwen:MaxConcurrentTurns"] = "2",
        });
        File.WriteAllText(StoreFile(dir),
            """{"wiki-nightly":{"maxTotal":9,"executors":[{"engine":"gemma","parallel":0},{"engine":"local","parallel":9}]}}""");
        var store = new LocalActionExecutorsStore(config, NullLogger<LocalActionExecutorsStore>.Instance,
            Engines(("gemma", 2)), new LlmProviderRegistry(config));

        var cfg = store.TryGet(Key);
        Assert.NotNull(cfg);
        Assert.Equal(LocalActionExecutorsStore.MaxTotalLimit, cfg.MaxTotal);
        Assert.Equal([1, 2], cfg.Executors.Select(e => e.Parallel));

        // maxTotal без числа в файле (default 0) — тоже нижняя граница, а не «ноль слотов»:
        // иначе место с живым списком молча оставалось бы без исполнителей.
        File.WriteAllText(StoreFile(dir), """{"wiki-nightly":{"executors":[{"engine":"gemma"}]}}""");
        var again = Store(dir, Engines(("gemma", 2)));
        Assert.Equal(1, again.TryGet(Key)!.MaxTotal);
    }

    [Fact]
    public void LocalExecutorsFor_HandEditedBrokenElement_NoThrow_OnlyLiveOnes()
    {
        // Тот же битый файл на стороне вызывающего ночи: LocalExecutorsFor брал e.Engine.Trim()
        // прямо из списка стора и на null-элементе падал вместе с проходом.
        var dir = DataDir();
        File.WriteAllText(StoreFile(dir),
            """{"wiki-nightly":{"maxTotal":2,"executors":[{"engine":"gemma","parallel":1},null,{"engine":""}]}}""");
        var (runner, _) = Stand(dir, Engines(("gemma", 2)));
        Assert.Equal(["gemma"], runner.LocalExecutorsFor(Key).Select(e => e.Engine));
    }

    [Fact]
    public void ConcurrentSaves_FileValidAndMemoryMatchesFile()
    {
        // Гонка PUT (тот же ключ): под замком «прочитать снимок → изменить → записать» целиком,
        // поэтому после гонки файл валиден и в нём ровно то же, что в памяти стора. Порядок
        // «кто победил» тест не утверждает — он недетерминирован; проверяется согласованность
        // памяти и файла. Мутация «снять lock» краснит этот тест; мутация «строить снимок вне
        // замка» — нет: при одном месте с ParallelExecutors два живых Save могут касаться только
        // одного ключа, и окно потери обновления снаружи недостижимо (см. доработку W2a).
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
        string dir, NamedLocalEngines engines, ILocalLlmClient? mainLocal = null,
        LlmProviderRegistry? providers = null)
    {
        var config = Config(dir);
        var http = new NoHttp();
        var ollama = mainLocal ?? new PlainEngineClient("main");
        var overrides = new LocalActionOverridesStore(config, NullLogger<LocalActionOverridesStore>.Instance);
        var router = new LocalActionRouter(ollama, overrides, config, NullLogger<LocalActionRouter>.Instance,
            engines: engines);
        var store = Store(dir, engines);
        // Реестр один на весь стенд: в бою это тот же синглтон, который читает панель
        // «Локальные модели». Без явного — пустой (тестовый конфиг не грузит appsettings.json),
        // это случай «подпись основной локали не задана».
        providers ??= new LlmProviderRegistry(config);
        var runner = new CheapTextRunner(router, ollama,
            new CloudCheapClient(http, config, providers, NullLogger<CloudCheapClient>.Instance),
            new NoClaude(), NullLogger<CheapTextRunner>.Instance, namedEngines: engines, executorsStore: store,
            providers: providers);
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
    public void LocalExecutorsFor_MainLocalTitleFromStats_UsesIt_WithoutIt_KeepsModelAlias()
    {
        // Явная подпись основной локали (LlmProviders:<ключ>:Stats:Title — то же значение, чем
        // зовёт модель панель «Локальные модели») идёт и в список исполнителей: под алиасом
        // qwen3.8-27b с октября работает Flash-Next, и рядом с «Qwen 27B (пилот)» человек читал
        // две «27» как две модели. Ключ исполнителя и маршрут не тронуты — «local» остаётся
        // «local», меняется только подпись.
        var dir = DataDir();
        var engines = Engines(("gemma", 2));
        var providers = new LlmProviderRegistry(Config(dir, new Dictionary<string, string?>
        {
            ["LlmProviders:local-qwen:IsLocal"] = "true",
            ["LlmProviders:local-qwen:Stats:Title"] = "Flash-Next",
        }));
        var (runner, store) = Stand(dir, engines, mainLocal: new PlainEngineClient("main"), providers);
        Assert.True(store.Save(Key, Cfg(2, Ex("local"), Ex("gemma"))));

        var list = runner.LocalExecutorsFor(Key);
        Assert.Equal(["local", "gemma"], list.Select(e => e.Engine));
        Assert.Equal("Flash-Next", list[0].Title);
        // Именованный движок — по-прежнему своей цепочке (Title → модель → имя; здесь ни Title,
        // ни Model не заданы → ключ движка): общая подпись основной локали его не касается.
        Assert.Equal("gemma", list[1].Title);

        // Без Stats:Title — прежнее поведение: модель, на которую реально ходит основной
        // движок (TextModel). Тот же случай проверяет и …WithEngineTitles выше.
        var (plainRunner, plainStore) = Stand(dir, engines, mainLocal: new PlainEngineClient("main"));
        Assert.True(plainStore.Save(Key, Cfg(1, Ex("local"))));
        Assert.Equal("main-модель", plainRunner.LocalExecutorsFor(Key)[0].Title);
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

    [Fact]
    public void LocalExecutorsFor_DuplicateEngineInFile_NotDoubled()
    {
        // Тот же правленный руками файл на стороне вызывающего ночи: без дедупа в списке
        // было бы два «local» (parallel 1 и 2) — два кандидата и 3 слота на одном физическом
        // движке, а его потолок без реестра провайдеров — DefaultLimit (3), то есть дубль
        // впритык съел бы весь потолок. С дедупом — одна первая строка.
        var dir = DataDir();
        File.WriteAllText(StoreFile(dir),
            """{"wiki-nightly":{"maxTotal":4,"executors":[{"engine":"local","parallel":1},{"engine":"local","parallel":2}]}}""");
        var (runner, _) = Stand(dir, Engines(("gemma", 2)));

        var list = runner.LocalExecutorsFor(Key);
        Assert.Equal(["local"], list.Select(e => e.Engine));
        Assert.Equal([1], list.Select(e => e.Parallel));
        Assert.True(list.Sum(e => e.Parallel) <= TrackedLocalLlmClient.DefaultLimit);
    }
}
