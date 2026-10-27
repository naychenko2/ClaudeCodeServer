using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Прямой клиент локального движка в общем счётчике LocalEngineBusyTracker: слот без ожидания,
// общий ключ с ходами CLI, признак Busy слот не взводит.
public class LocalEngineGateTests
{
    // Заглушка движка: держит вызовы до явного отпускания
    private sealed class StubEngine : ILocalLlmClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _entered = new(0);
        public int Entered;

        public bool Enabled => true;
        public string BaseUrl => "http://stub";
        public string Model => "stub";
        public string TextModel => "stub";
        public string ProviderKey => LocalLlmOptions.LlamaServer;

        public void Release() => _release.TrySetResult();

        public async Task WaitEnteredAsync(int n)
        {
            for (var i = 0; i < n; i++) await _entered.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private async Task<string?> Hold()
        {
            Interlocked.Increment(ref Entered);
            _entered.Release();
            await _release.Task;
            return "ok";
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

    private static TrackedLocalLlmClient Client(StubEngine stub, LocalEngineBusyTracker tracker, string key, int limit) =>
        new(stub, tracker, key, () => limit, NullLogger<TrackedLocalLlmClient>.Instance);

    private static Task<string?> Call(ILocalLlmClient c) =>
        c.GenerateTextAsync("p", null, TimeSpan.FromSeconds(5), 16, 1024);

    private static Task<LocalTurnAdmission> Turn(LocalEngineBusyTracker t, string key, int limit) =>
        t.AcquireAsync(key, limit, maxQueued: 0, TimeSpan.FromSeconds(5), CancellationToken.None);

    [Fact]
    public async Task Занятый_счётчик_даёт_null_сразу_а_слот_клиента_Busy_не_взводит()
    {
        var tracker = new LocalEngineBusyTracker();
        var stub = new StubEngine();
        var client = Client(stub, tracker, "LocalLlm", limit: 1);

        var first = Call(client);
        await stub.WaitEnteredAsync(1);

        // слот занят, но признак «идёт ход» (источник LocalActionRouter.LocalBlockedByTurn) не взведён
        Assert.Equal(1, tracker.ActiveFor("LocalLlm"));
        Assert.False(tracker.Busy);
        Assert.Equal(0, tracker.Active);

        // второй вызов: ни исключения, ни ожидания — null, до движка не дошёл
        var second = await Call(client).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(second);
        Assert.Equal(1, Volatile.Read(ref stub.Entered));

        stub.Release();
        Assert.Equal("ok", await first);
        Assert.Equal(0, tracker.ActiveFor("LocalLlm"));
    }

    [Fact]
    public async Task Клиент_с_общим_ключом_делит_потолок_с_ходами_CLI()
    {
        var tracker = new LocalEngineBusyTracker();
        var stub = new StubEngine();
        var client = Client(stub, tracker, "local-qwen", limit: 3);

        // два хода CLI на local-qwen из трёх слотов
        using var t1 = (await Turn(tracker, "local-qwen", 3)).Slot;
        using var t2 = (await Turn(tracker, "local-qwen", 3)).Slot;
        Assert.NotNull(t1);
        Assert.NotNull(t2);

        // оставшийся слот берёт прямой клиент, следующий вызов — отказ
        var first = Call(client);
        await stub.WaitEnteredAsync(1);
        Assert.Equal(3, tracker.ActiveFor("local-qwen"));
        Assert.Null(await Call(client));
        Assert.Equal(1, Volatile.Read(ref stub.Entered));

        // и обратный порядок: ход CLI при заполненном счётчике ждёт/получает отказ
        var turn = await Turn(tracker, "local-qwen", 3);
        Assert.Null(turn.Slot);
        Assert.Equal(LocalTurnRefusal.QueueFull, turn.Refusal);

        stub.Release();
        await first;
    }

    [Fact]
    public async Task Разные_ключи_при_общем_движке_дают_два_потолка()
    {
        // мутация c3: клиент под своим ключом не видит ходов local-qwen и пролезает сверх потолка
        var tracker = new LocalEngineBusyTracker();
        var stub = new StubEngine();
        var client = Client(stub, tracker, "LocalLlm", limit: 3);

        using var t1 = (await Turn(tracker, "local-qwen", 3)).Slot;
        using var t2 = (await Turn(tracker, "local-qwen", 3)).Slot;
        using var t3 = (await Turn(tracker, "local-qwen", 3)).Slot;

        var call = Call(client);
        await stub.WaitEnteredAsync(1);
        Assert.Equal(1, Volatile.Read(ref stub.Entered)); // прошёл при трёх занятых ходах: потолок неправда

        stub.Release();
        await call;
    }

    [Fact]
    public void Ключ_движка_берётся_из_LocalLlm_EngineKey_без_него_свой_ключ()
    {
        IConfiguration Cfg(string? v) => new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["LocalLlm:EngineKey"] = v }).Build();

        Assert.Equal("local-qwen", TrackedLocalLlmClient.ReadEngineKey(Cfg("local-qwen")));
        Assert.Equal("localllm", TrackedLocalLlmClient.ReadEngineKey(Cfg(null)));
        Assert.Equal("localllm", TrackedLocalLlmClient.ReadEngineKey(Cfg("  ")));
        // дефолтный путь не расходится с ключом реестра по регистру (реестр держит ключи в нижнем)
        Assert.Equal(TrackedLocalLlmClient.DefaultEngineKey.ToLowerInvariant(), TrackedLocalLlmClient.DefaultEngineKey);
    }

    [Fact]
    public async Task Ключ_в_другом_регистре_попадает_в_корзину_хода_CLI()
    {
        // мутация c1: без ToLowerInvariant в ReadEngineKey клиент уходит в корзину «LOCAL-QWEN» и пролезает сверх потолка
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["LocalLlm:EngineKey"] = " LOCAL-QWEN " }).Build();
        var key = TrackedLocalLlmClient.ReadEngineKey(cfg);
        Assert.Equal("local-qwen", key);

        var tracker = new LocalEngineBusyTracker();
        var stub = new StubEngine();
        var client = Client(stub, tracker, key, limit: 1);
        using var turn = (await Turn(tracker, "local-qwen", 1)).Slot; // ход CLI занял единственный слот
        Assert.NotNull(turn);

        Assert.Null(await Call(client).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref stub.Entered));
        Assert.Equal(1, tracker.ActiveFor("local-qwen"));
    }

    [Fact]
    public async Task Занятый_счётчик_даёт_различимый_отказ_разговорного_хода()
    {
        var tracker = new LocalEngineBusyTracker();
        var stub = new StubEngine();
        var client = Client(stub, tracker, "local-qwen", limit: 1);
        using var turn = (await Turn(tracker, "local-qwen", 1)).Slot;

        var result = await client.ChatTurnAsync([], null, TimeSpan.FromSeconds(5), 16, 1024, null);

        Assert.Null(result.Text);
        Assert.True(result.Busy);
    }
}

// Полный хост: коллекция Inotify, как у соседних тестов с TestWebApplicationFactory
[Collection(TestCollections.Inotify)]
public class LocalEngineGateDiTests
{
    // Регистрация ILocalLlmClient в LlmSubsystem обёрнута слотом общего счётчика: снятие обёртки
    // вернуло бы движок без потолка, а остальные тесты строят обёртку вручную и этого не заметили бы.
    [Fact]
    public void Основной_клиент_из_DI_обёрнут_слотом_общего_счётчика()
    {
        using var factory = new TestWebApplicationFactory();

        var client = factory.Services.GetRequiredService<ILocalLlmClient>();
        var tracked = Assert.IsType<TrackedLocalLlmClient>(client);

        // singleton: повторный резолв даёт тот же клиент
        Assert.Same(client, factory.Services.GetRequiredService<ILocalLlmClient>());
        Assert.Equal(TrackedLocalLlmClient.DefaultEngineKey, tracked.EngineKey);
    }
}
