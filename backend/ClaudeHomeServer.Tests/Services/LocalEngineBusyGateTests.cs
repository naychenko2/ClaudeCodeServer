using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Правило «фоновое действие не лезет в локальный движок, пока на нём идёт ход исполнителя»
// (живой прогон 2026-09-23: 14 фоновых запросов на 213k токенов параллельно с ходом, два из
// шести промахов prefix cache — через 100–130 мс после такого запроса).
// Признак занятости ставит ClaudeSession через LocalEngineBusyTracker, решение принимает
// LocalActionRouter.LocalBlockedByTurn, исполняет CheapTextRunner.
public class LocalEngineBusyGateTests
{
    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // Локальный движок, который ЗАПОМИНАЕТ обращения: тест проверяет не текст ответа,
    // а сам факт «ходили или нет».
    private sealed class RecordingLocal(bool enabled = true) : ILocalLlmClient
    {
        public readonly List<string?> Calls = [];

        public bool Enabled => enabled;
        public string BaseUrl => "http://localhost:8000";
        public string Model => "qwen3:32b";
        public string TextModel => "qwen3:32b";
        public string ProviderKey => "llama-server";

        public Task<string?> ChatJsonAsync(string systemPrompt, string userPrompt, object jsonFormat,
            CancellationToken ct = default, string? model = null, int? timeoutMs = null,
            int? numPredict = null, int? numCtx = null, string? ownerId = null, string? label = null)
        {
            Calls.Add(label);
            return Task.FromResult<string?>("LOCAL-JSON");
        }

        public Task<string?> GenerateTextAsync(string prompt, string? model, TimeSpan timeout,
            int numPredict, int numCtx, string? ownerId = null, string? label = null,
            CancellationToken ct = default)
        {
            Calls.Add(label);
            return Task.FromResult<string?>("LOCAL-TEXT");
        }

        public Task<ChatTurnResult> ChatTurnAsync(IReadOnlyList<ChatMsg> messages, string? model,
            TimeSpan timeout, int numPredict, int numCtx, string? ownerId,
            Func<string, Task>? onDelta = null, CancellationToken ct = default)
            => Task.FromResult(new ChatTurnResult("LOCAL-TURN", null));

        public Task WarmUpAsync(string? model, CancellationToken ct = default) => Task.CompletedTask;
    }

    // Фейковый claude: помечает ответ, чтобы облачный шаг цепочки отличался от локального.
    private sealed class FakeOneShot : IOneShotRunner
    {
        public string? NormalizeModel(string? model) => model;
        public Task<string> RunAsync(string prompt, string? model = null, TimeSpan? timeout = null,
            CancellationToken ct = default, string? ownerId = null, string? effort = null, string? label = null)
            => Task.FromResult($"CLAUDE[{model}]");
        public Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null,
            TimeSpan? timeout = null, CancellationToken ct = default, string? ownerId = null,
            string? effort = null, string? label = null)
            => Task.FromResult(new OneShotResult($"CLAUDE[{model}]", null, 0));
    }

    private static IConfiguration ConfigWithTempData(Dictionary<string, string?> d)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        d["DataPath"] = Path.Combine(dir, "projects.json");
        return TestConfig.Build(d);
    }

    // Стенд: маршрут места — «локаль» (явный выбор админа), свой трекер занятости
    // (глобальный Instance между тестами не делим).
    private static (CheapTextRunner Runner, RecordingLocal Local, LocalEngineBusyTracker Busy) Stand(
        string? whileTurn = null, bool localEnabled = true)
    {
        var cfg = new Dictionary<string, string?> { ["Ollama:Model"] = "qwen3:32b" };
        if (whileTurn is not null) cfg["LocalLlm:BackgroundWhileTurn"] = whileTurn;
        var config = ConfigWithTempData(cfg);

        var local = new RecordingLocal(localEnabled);
        var store = new LocalActionOverridesStore(config, NullLogger<LocalActionOverridesStore>.Instance);
        Assert.True(store.Set(LocalActionCatalog.NotesTags, LocalActionOverridesStore.LocalRoute));

        var busy = new LocalEngineBusyTracker();
        var router = new LocalActionRouter(local, store, config,
            NullLogger<LocalActionRouter>.Instance, specialty: null, busy: busy);
        var cloud = new CloudCheapClient(new NullHttpFactory(), config, new LlmProviderRegistry(config),
            NullLogger<CloudCheapClient>.Instance);
        var runner = new CheapTextRunner(router, local, cloud, new FakeOneShot(),
            NullLogger<CheapTextRunner>.Instance);
        return (runner, local, busy);
    }

    [Fact]
    public async Task ВоВремяХодаНаЛокали_ДействиеНеУходитНаЛокаль()
    {
        var (runner, local, busy) = Stand();
        using var turn = busy.Enter();

        var text = await runner.RunAsync(LocalActionCatalog.NotesTags, "теги", fallbackModel: "haiku");

        Assert.Empty(local.Calls);
        Assert.Equal("CLAUDE[haiku]", text);
    }

    // Контроль к предыдущему тесту: без хода то же самое место идёт на локаль. Без этой
    // пары первый тест проходил бы и на наглухо выключенной локали.
    [Fact]
    public async Task БезХода_ДействиеИдётНаЛокаль()
    {
        var (runner, local, _) = Stand();

        var text = await runner.RunAsync(LocalActionCatalog.NotesTags, "теги", fallbackModel: "haiku");

        Assert.Single(local.Calls);
        Assert.Equal("LOCAL-TEXT", text);
    }

    [Fact]
    public async Task ПослеХода_ЛокальСноваДоступна()
    {
        var (runner, local, busy) = Stand();
        var turn = busy.Enter();
        await runner.RunAsync(LocalActionCatalog.NotesTags, "теги", fallbackModel: "haiku");
        Assert.Empty(local.Calls);

        turn.Dispose();
        var text = await runner.RunAsync(LocalActionCatalog.NotesTags, "теги", fallbackModel: "haiku");

        Assert.Single(local.Calls);
        Assert.Equal("LOCAL-TEXT", text);
    }

    // Второй ход на том же движке не должен «освобождать» его раньше времени: счётчик,
    // а не флаг.
    [Fact]
    public async Task ДваХода_ЛокальСвободнаТолькоПослеПоследнего()
    {
        var (runner, local, busy) = Stand();
        var first = busy.Enter();
        var second = busy.Enter();

        first.Dispose();
        await runner.RunAsync(LocalActionCatalog.NotesTags, "теги", fallbackModel: "haiku");
        Assert.Empty(local.Calls);

        second.Dispose();
        await runner.RunAsync(LocalActionCatalog.NotesTags, "теги", fallbackModel: "haiku");
        Assert.Single(local.Calls);
    }

    // Повторный Dispose одного токена не должен уводить счётчик в минус — иначе живой ход
    // перестал бы считаться занятостью.
    [Fact]
    public void ПовторныйDispose_НеЛомаетСчётчик()
    {
        var busy = new LocalEngineBusyTracker();
        var first = busy.Enter();
        var second = busy.Enter();

        first.Dispose();
        first.Dispose();

        Assert.True(busy.Busy);
        Assert.Equal(1, busy.Active);
        second.Dispose();
        Assert.False(busy.Busy);
    }

    // Выбор админа «пусть ходит вместе с ходом» — прежнее поведение.
    [Fact]
    public async Task ПолитикаLocal_ДействиеИдётНаЛокальДажеВоВремяХода()
    {
        var (runner, local, busy) = Stand(whileTurn: LocalActionRouter.WhileTurnLocal);
        using var turn = busy.Enter();

        var text = await runner.RunAsync(LocalActionCatalog.NotesTags, "теги", fallbackModel: "haiku");

        Assert.Single(local.Calls);
        Assert.Equal("LOCAL-TEXT", text);
    }

    // Действие без облачного шага: во время хода — null (вызывающий деградирует), а не
    // платный фолбэк.
    [Fact]
    public async Task RunLocalOnly_ВоВремяХода_ОтдаётNull()
    {
        var (runner, local, busy) = Stand();
        using var turn = busy.Enter();

        var text = await runner.RunLocalOnlyAsync(LocalActionCatalog.NotesTags, "теги");

        Assert.Null(text);
        Assert.Empty(local.Calls);
    }

    // «Не ломать владельцев без локали»: движок выключен — действие как и раньше идёт
    // сразу на облачный шаг, признак занятости ничего не меняет.
    [Fact]
    public async Task ЛокалиНет_ДействиеИдётНаОблакоИПриСвободномДвижке()
    {
        var (runner, local, busy) = Stand(localEnabled: false);

        var free = await runner.RunAsync(LocalActionCatalog.NotesTags, "теги", fallbackModel: "haiku");
        using var turn = busy.Enter();
        var busyText = await runner.RunAsync(LocalActionCatalog.NotesTags, "теги", fallbackModel: "haiku");

        Assert.Equal("CLAUDE[haiku]", free);
        Assert.Equal("CLAUDE[haiku]", busyText);
        Assert.Empty(local.Calls);
    }
}
