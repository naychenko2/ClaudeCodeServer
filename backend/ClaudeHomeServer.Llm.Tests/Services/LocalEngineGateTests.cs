using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Потолок конкурентности к локальному движку (И7): один гейт на движок, ограниченная очередь,
// отказ по существу при переполнении.
public class LocalEngineGateTests
{
    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class CapturingLogger : ILogger<GatedLocalLlmClient>
    {
        public readonly List<string> Warnings = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) lock (Warnings) Warnings.Add(formatter(state, exception));
        }
    }

    // Заглушка движка: считает вызовы в полёте и держит их до явного отпускания
    private sealed class StubEngine : ILocalLlmClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _entered = new(0);
        private int _inFlight;
        public int MaxInFlight;
        public int Entered;

        public bool Enabled => true;
        public string BaseUrl => "http://stub";
        public string Model => "stub";
        public string TextModel => "stub";
        public string ProviderKey => LocalLlmOptions.LlamaServer;

        public void Release() => _release.TrySetResult();

        // Ждёт, пока в заглушку войдёт n вызовов (по событию, без Task.Delay)
        public async Task WaitEnteredAsync(int n)
        {
            for (var i = 0; i < n; i++) await _entered.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private async Task<string?> Hold()
        {
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref MaxInFlight)) &&
                   Interlocked.CompareExchange(ref MaxInFlight, now, seen) != seen) { }
            Interlocked.Increment(ref Entered);
            _entered.Release();
            try { await _release.Task; return "ok"; }
            finally { Interlocked.Decrement(ref _inFlight); }
        }

        public Task<string?> ChatJsonAsync(string systemPrompt, string userPrompt, object jsonFormat,
            CancellationToken ct = default, string? model = null, int? timeoutMs = null, int? numPredict = null,
            int? numCtx = null, string? ownerId = null, string? label = null) => Hold();

        public Task<string?> GenerateTextAsync(string prompt, string? model, TimeSpan timeout, int numPredict,
            int numCtx, string? ownerId = null, string? label = null, CancellationToken ct = default) => Hold();

        public async Task<ChatTurnResult> ChatTurnAsync(IReadOnlyList<ChatMsg> messages, string? model,
            TimeSpan timeout, int numPredict, int numCtx, string? ownerId, Func<string, Task>? onDelta = null,
            CancellationToken ct = default) => new(await Hold(), null);

        public Task WarmUpAsync(string? model, CancellationToken ct = default) => Hold();
    }

    private static Task<string?> Call(ILocalLlmClient c) =>
        c.GenerateTextAsync("p", null, TimeSpan.FromSeconds(5), 16, 1024);

    [Fact]
    public async Task Десять_одновременных_вызовов_при_M2_держат_в_полёте_не_больше_двух()
    {
        var stub = new StubEngine();
        var client = new GatedLocalLlmClient(stub, new LocalEngineGate("e", concurrency: 2, maxQueue: 20),
            NullLogger<GatedLocalLlmClient>.Instance);

        var calls = Enumerable.Range(0, 10).Select(_ => Call(client)).ToArray();
        await stub.WaitEnteredAsync(2);

        // два слота заняты, остальные восемь стоят в очереди гейта и до заглушки не дошли
        Assert.Equal(2, Volatile.Read(ref stub.Entered));
        Assert.Equal(8, client.Gate.Waiting);

        // отпускаем: очередь проходит по двое, пик в заглушке не растёт
        stub.Release();
        var results = await Task.WhenAll(calls);

        Assert.All(results, r => Assert.Equal("ok", r));
        Assert.Equal(10, stub.Entered);
        Assert.Equal(2, stub.MaxInFlight);
        Assert.Equal(2, client.Gate.Available);
    }

    [Fact]
    public async Task Переполнение_очереди_даёт_отказ_с_понятным_текстом_и_не_растит_очередь()
    {
        var gate = new LocalEngineGate("main", concurrency: 1, maxQueue: 2);
        var stub = new StubEngine();
        var log = new CapturingLogger();
        var client = new GatedLocalLlmClient(stub, gate, log);

        var running = Call(client);
        await stub.WaitEnteredAsync(1);
        var queued = new[] { Call(client), Call(client) };
        Assert.Equal(2, gate.Waiting);

        // сам гейт отказывает исключением с текстом по существу
        var ex = await Assert.ThrowsAsync<LocalEngineOverloadedException>(
            () => gate.RunAsync(() => Task.FromResult(1), CancellationToken.None));
        Assert.Contains("«main»", ex.Message);
        Assert.Contains("перегружен", ex.Message);
        Assert.Contains("очередь ожидания (2) заполнена", ex.Message);

        // клиент сохраняет контракт «null при беде», но отказ не тихий: он в логе
        Assert.Null(await Call(client));
        Assert.Contains(log.Warnings, w => w.Contains("перегружен"));
        Assert.Equal(2, gate.Waiting);
        Assert.Equal(1, Volatile.Read(ref stub.Entered));

        stub.Release();
        await Task.WhenAll(queued.Append(running));
        Assert.Equal(0, gate.Waiting);
    }

    [Fact]
    public async Task Счётчики_разных_движков_раздельные_а_конфиг_читается_с_дефолтом()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalEngines:alpha:BaseUrl"] = "http://a",
            ["LocalEngines:alpha:Model"] = "m",
            ["LocalEngines:alpha:MaxConcurrency"] = "1",
            ["LocalEngines:alpha:MaxQueue"] = "0",
            ["LocalEngines:beta:BaseUrl"] = "http://b",
            ["LocalEngines:beta:Model"] = "m",
        }).Build();

        var engines = new NamedLocalEngines(new NullHttpFactory(), config, NullLoggerFactory.Instance);
        var alpha = Assert.IsType<GatedLocalLlmClient>(engines.Get("alpha")!.Client).Gate;
        var beta = Assert.IsType<GatedLocalLlmClient>(engines.Get("beta")!.Client).Gate;
        var main = LocalEngineGate.Read(config, "LocalLlm", "LocalLlm");

        Assert.NotSame(alpha, beta);
        Assert.NotSame(alpha, main);
        Assert.Equal((1, 0), (alpha.Concurrency, alpha.MaxQueue));
        Assert.Equal((LocalEngineGate.DefaultConcurrency, LocalEngineGate.DefaultQueue),
            (beta.Concurrency, beta.MaxQueue));

        // насыщенный alpha не занимает слоты beta и основного движка
        var hold = new TaskCompletionSource();
        var busy = alpha.RunAsync(async () => { await hold.Task; return 1; }, CancellationToken.None);
        Assert.Equal(0, alpha.Available);
        Assert.Equal(beta.Concurrency, beta.Available);
        Assert.Equal(main.Concurrency, main.Available);
        await Assert.ThrowsAsync<LocalEngineOverloadedException>(
            () => alpha.RunAsync(() => Task.FromResult(1), CancellationToken.None));
        Assert.Equal(7, await beta.RunAsync(() => Task.FromResult(7), CancellationToken.None));
        hold.SetResult();
        await busy;
    }
}
