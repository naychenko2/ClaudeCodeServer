using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Темы чатов (В-2 ревью): разбор зовёт RunLocalOnlyAsync с jsonFormat на НАСТОЯЩЕМ
// CheapTextRunner. Маршрут сверяется на каждом вызове: место перевели на direct-модель
// агрегатора посреди прохода — следующий вызов отдаёт null, не трогая ни облако, ни локаль.
public class ChatSubjectsLocalOnlyRunnerTests : IDisposable
{
    private const string Action = LocalActionCatalog.ChatSubjects;

    // Временные каталоги стендов — чистятся в Dispose, в %TEMP%/cc-tests не копятся
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        foreach (var dir in _dirs) TestFs.DeleteDirectoryResilient(dir);
    }

    // Считает каждый HTTP-клиент: облачный адаптер без него наружу не сходит
    private sealed class CountingHttpFactory : IHttpClientFactory
    {
        public int Created;
        public HttpClient CreateClient(string name)
        {
            Interlocked.Increment(ref Created);
            return new HttpClient();
        }
    }

    private sealed class RecordingLocal : ILocalLlmClient
    {
        public int JsonCalls;
        public int TextCalls;
        // Ответ локали на JSON-вызов: null — движок не ответил (таймаут, обрыв)
        public string? JsonReply { get; set; } = "{\"assign\":[]}";

        public bool Enabled => true;
        public string BaseUrl => "http://localhost:8000";
        public string Model => "qwen3:32b";
        public string TextModel => "qwen3:32b";
        public string ProviderKey => "llama-server";

        public Task<string?> ChatJsonAsync(string systemPrompt, string userPrompt, object jsonFormat,
            CancellationToken ct = default, string? model = null, int? timeoutMs = null,
            int? numPredict = null, int? numCtx = null, string? ownerId = null, string? label = null)
        {
            JsonCalls++;
            return Task.FromResult(JsonReply);
        }

        public Task<string?> GenerateTextAsync(string prompt, string? model, TimeSpan timeout,
            int numPredict, int numCtx, string? ownerId = null, string? label = null,
            CancellationToken ct = default)
        {
            TextCalls++;
            return Task.FromResult<string?>("текст");
        }

        public Task<ChatTurnResult> ChatTurnAsync(IReadOnlyList<ChatMsg> messages, string? model,
            TimeSpan timeout, int numPredict, int numCtx, string? ownerId,
            Func<string, Task>? onDelta = null, CancellationToken ct = default)
            => Task.FromResult(new ChatTurnResult("", null));

        public Task WarmUpAsync(string? model, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class PaidOneShot : IOneShotRunner
    {
        public int Calls;
        public string? NormalizeModel(string? model) => model;
        public Task<string> RunAsync(string prompt, string? model = null, TimeSpan? timeout = null,
            CancellationToken ct = default, string? ownerId = null, string? effort = null, string? label = null)
        {
            Calls++;
            return Task.FromResult("claude");
        }
        public Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null,
            TimeSpan? timeout = null, CancellationToken ct = default, string? ownerId = null,
            string? effort = null, string? label = null)
        {
            Calls++;
            return Task.FromResult(new OneShotResult("claude", null, 0));
        }
    }

    private (ICheapTextRunner Runner, LocalActionOverridesStore Store, RecordingLocal Local,
        CountingHttpFactory Http, PaidOneShot Paid) Stand() => StandWithBusy().Stand;

    // Стенд со своим трекером занятости движка: LocalBlockedByTurn поднимает busy.Enter()
    private ((ICheapTextRunner Runner, LocalActionOverridesStore Store, RecordingLocal Local,
        CountingHttpFactory Http, PaidOneShot Paid) Stand, LocalEngineBusyTracker Busy) StandWithBusy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        var config = TestConfig.Build(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(dir, "projects.json"),
            ["Ollama:Model"] = "qwen3:32b",
        });
        var local = new RecordingLocal();
        var store = new LocalActionOverridesStore(config, NullLogger<LocalActionOverridesStore>.Instance);
        var busy = new LocalEngineBusyTracker();
        var router = new LocalActionRouter(local, store, config, NullLogger<LocalActionRouter>.Instance,
            specialty: null, busy: busy);
        var http = new CountingHttpFactory();
        var cloud = new CloudCheapClient(http, config, new LlmProviderRegistry(config),
            NullLogger<CloudCheapClient>.Instance);
        var paid = new PaidOneShot();
        return ((new CheapTextRunner(router, local, cloud, paid, NullLogger<CheapTextRunner>.Instance),
            store, local, http, paid), busy);
    }

    // На локальном движке идёт ход исполнителя (LocalBlockedByTurn): у места нет облачного
    // шага, поэтому вызов отдаёт null — ни локаль, ни облако, ни claude не тронуты
    [Fact]
    public async Task ЛокальЗанятаХодом_NullБезОблакаИБезЛокали()
    {
        var ((runner, _, local, http, paid), busy) = StandWithBusy();
        using var turn = busy.Enter();

        var answer = await runner.RunLocalOnlyAsync(Action, "реплики чатов", jsonFormat: "json", CancellationToken.None);

        answer.Should().BeNull();
        local.JsonCalls.Should().Be(0, "фон не лезет в занятый ходом движок");
        local.TextCalls.Should().Be(0);
        http.Created.Should().Be(0, "занятая локаль не повод уходить в облако");
        paid.Calls.Should().Be(0);
    }

    // Контроль к предыдущему: ход закончился — тот же вызов снова идёт на локаль. Без пары
    // первый тест проходил бы и на наглухо выключенной локали.
    [Fact]
    public async Task ХодЗакончился_ЛокальСноваОтвечает()
    {
        var ((runner, _, local, _, _), busy) = StandWithBusy();
        busy.Enter().Dispose();

        var answer = await runner.RunLocalOnlyAsync(Action, "реплики чатов", jsonFormat: "json", CancellationToken.None);

        answer.Should().Be("{\"assign\":[]}");
        local.JsonCalls.Should().Be(1);
    }

    // Сбой локали (таймаут, пустой ответ): null, и цепочка дальше не идёт — ни облака, ни claude
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ЛокальНеОтветила_NullБезОблака(string? reply)
    {
        var (runner, _, local, http, paid) = Stand();
        local.JsonReply = reply;

        var answer = await runner.RunLocalOnlyAsync(Action, "реплики чатов", jsonFormat: "json", CancellationToken.None);

        answer.Should().BeNull();
        local.JsonCalls.Should().Be(1);
        http.Created.Should().Be(0);
        paid.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ЛокальныйМаршрут_JsonИдётВStructuredOutputЛокали()
    {
        var (runner, _, local, http, paid) = Stand();

        var answer = await runner.RunLocalOnlyAsync(Action, "промпт", jsonFormat: "json", CancellationToken.None);

        answer.Should().Be("{\"assign\":[]}");
        local.JsonCalls.Should().Be(1, "jsonFormat уводит локальный шаг в ChatJsonAsync");
        local.TextCalls.Should().Be(0);
        http.Created.Should().Be(0);
        paid.Calls.Should().Be(0);
    }

    [Fact]
    public async Task МестоПеревелиНаDirectМодель_NullБезОблакаИБезЛокали()
    {
        var (runner, store, local, http, paid) = Stand();
        store.Set(Action, CloudCheapClient.RoutePrefix + "some/free-model").Should().BeTrue();

        var answer = await runner.RunLocalOnlyAsync(Action, "реплики чатов", jsonFormat: "json", CancellationToken.None);

        answer.Should().BeNull();
        runner.UsesLocal(Action).Should().BeFalse();
        runner.HasFreeRoute(Action).Should().BeTrue("ровно этот случай HasFreeRoute пропускал в облако");
        local.JsonCalls.Should().Be(0);
        http.Created.Should().Be(0, "реплики чатов наружу не уходят");
        paid.Calls.Should().Be(0);
    }
}
