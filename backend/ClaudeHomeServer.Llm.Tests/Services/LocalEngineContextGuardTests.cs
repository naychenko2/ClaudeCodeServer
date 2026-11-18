using System.Reflection;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Сторож при старте: NumCtx именованного движка меньше NumCtx его мест (разбор 8d66a3d3 §2–3).
// Что ловят тесты: чистый Check считает ровно то, что должен, по обеим сторонам назначения
// (Actions движка и список исполнителей места — одна строка на пару, не две); движок без
// потолка или с достаточным — молчит; исключение чтения стора не роняет StartAsync и
// остаётся видимым в журнале.
public class LocalEngineContextGuardTests
{
    private const string Engine = "gemma";

    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // Фейковый ILocalLlmClient движка (образец — NamedLocalEngineTests): сторож транспорт
    // движка не зовёт вовсе, клиенту нужен только Enabled.
    private sealed class FakeEngineClient : ILocalLlmClient
    {
        public bool Enabled => true;
        public string BaseUrl => "http://engine";
        public string Model => "gemma-4-26b-a4b";
        public string TextModel => "gemma-4-26b-a4b";
        public string ProviderKey => LocalLlmOptions.LlamaServer;

        public Task<string?> ChatJsonAsync(string systemPrompt, string userPrompt, object jsonFormat,
            CancellationToken ct = default, string? model = null, int? timeoutMs = null, int? numPredict = null,
            int? numCtx = null, string? ownerId = null, string? label = null) => Task.FromResult<string?>(null);

        public Task<string?> GenerateTextAsync(string prompt, string? model, TimeSpan timeout, int numPredict,
            int numCtx, string? ownerId = null, string? label = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<ChatTurnResult> ChatTurnAsync(IReadOnlyList<ChatMsg> messages, string? model, TimeSpan timeout,
            int numPredict, int numCtx, string? ownerId, Func<string, Task>? onDelta = null,
            CancellationToken ct = default) => Task.FromResult(new ChatTurnResult(null, null));

        public Task WarmUpAsync(string? model, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static IConfiguration Config()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return TestConfig.Build(new() { ["DataPath"] = Path.Combine(dir, "projects.json") });
    }

    private static NamedLocalEngines Engines(string name, int? numCtx, params string[] actions) =>
        new([new LocalEngine(
            new LocalEngineSpec(name, ReadyUrl: null, StateUrl: null, FallbackRoute: null,
                numCtx, new HashSet<string>(actions, StringComparer.OrdinalIgnoreCase)),
            new FakeEngineClient(), null)]);

    private static LocalActionRouter Router(IConfiguration config, NamedLocalEngines engines)
    {
        // Основная локаль выключена (Ollama:Model пуст): движок места — только из его Actions.
        var ollama = new OllamaClient(new NullHttpFactory(), config, NullLogger<OllamaClient>.Instance);
        var overrides = new LocalActionOverridesStore(config, NullLogger<LocalActionOverridesStore>.Instance);
        return new LocalActionRouter(ollama, overrides, config, NullLogger<LocalActionRouter>.Instance,
            engines: engines);
    }

    private static LocalEngineContextGuard Guard(
        LocalActionRouter router, LocalActionExecutorsStore store, NamedLocalEngines engines, RecordingLogger log) =>
        new(router, store, engines, new SingleLoggerFactory(log).CreateLogger<LocalEngineContextGuard>());

    [Fact]
    public void Check_LowerEngineCeiling_ReportsOneLine()
    {
        var pairs = new List<(string, int, string, int?)>
        {
            (LocalActionCatalog.WikiNightly, 65_536, Engine, 32_768),
        };
        var lines = LocalEngineContextGuard.Check(pairs);
        Assert.Single(lines);
        Assert.Contains(Engine, lines[0]);
        Assert.Contains("32768", lines[0]);
        Assert.Contains("65536", lines[0]);
        Assert.Contains("wiki-nightly", lines[0]);
    }

    [Fact]
    public void Check_CeilingEqualOrAbsent_NoLines()
    {
        // Ровный потолок — не расхождение (строгое «меньше»), null — у движка нет потолка.
        Assert.Empty(LocalEngineContextGuard.Check(
            [(LocalActionCatalog.WikiNightly, 65_536, Engine, 65_536)]));
        Assert.Empty(LocalEngineContextGuard.Check(
            [(LocalActionCatalog.WikiNightly, 65_536, Engine, null)]));
    }

    [Fact]
    public async Task StartAsync_SameEngineFromActionsAndExecutors_ReportsExactlyOneLine()
    {
        // Болевой случай (разбор 8d66a3d3): gemma с NumCtx 32768 назначена wiki-nightly
        // и через Actions движка, и через список исполнителей места. Пара одна — и
        // предупреждение одно; «local» (основная локаль, без LocalEngineSpec) не считается.
        var config = Config();
        var engines = Engines(Engine, 32_768, LocalActionCatalog.WikiNightly);
        var store = new LocalActionExecutorsStore(config, NullLogger<LocalActionExecutorsStore>.Instance, engines);
        Assert.True(store.Save(LocalActionCatalog.WikiNightly, new()
        {
            MaxTotal = 2,
            Executors =
            [
                new LocalActionExecutorConfig { Engine = Engine, Parallel = 1 },
                new LocalActionExecutorConfig { Engine = "local", Parallel = 1 },
            ],
        }));

        var log = new RecordingLogger();
        var guard = Guard(Router(config, engines), store, engines, log);
        await guard.StartAsync(default);

        Assert.Single(guard.Mismatches);
        Assert.Contains(Engine, guard.Mismatches[0]);
        Assert.Contains("32768", guard.Mismatches[0]);
        Assert.Contains("65536", guard.Mismatches[0]);
        Assert.Contains("wiki-nightly", guard.Mismatches[0]);
        // Строка сказана и в журнале: без неё наблюдение осталось бы в свойстве, которого никто не читает.
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("wiki-nightly"));
    }

    [Fact]
    public async Task StartAsync_ThrowingStore_SwallowsExceptionAndWarns()
    {
        // Стор, бросающий исключение при чтении: моделируется состоянием, которого публичный API
        // не производит (cfg с null Executors в снимке — Load такие фильтрует): TryGet падает
        // на cfg.Executors.Count. Смысл проверки — любой исключительный случай чтения
        // остаётся warning-ом и не роняет хост; Mismatches при этом остаётся пустым.
        var config = Config();
        var engines = Engines(Engine, 32_768 /* без Actions: пар из маршрута нет */);
        var store = new LocalActionExecutorsStore(config, NullLogger<LocalActionExecutorsStore>.Instance, engines);
        typeof(LocalActionExecutorsStore)
            .GetField("_configs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(store, new Dictionary<string, LocalActionExecutorsConfig>(StringComparer.OrdinalIgnoreCase)
            {
                [LocalActionCatalog.WikiNightly] = new() { Executors = null! },
            });

        var log = new RecordingLogger();
        var guard = Guard(Router(config, engines), store, engines, log);
        await guard.StartAsync(default); // без исключения — сам факт этого вызова и есть проверка

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Empty(guard.Mismatches);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class SingleLoggerFactory(RecordingLogger log) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => log;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }
}
