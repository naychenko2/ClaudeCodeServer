using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Координатор ночи (W4a2, разрез «ночь вики на нескольких локальных исполнителях»): слоты
// исполнителей, сродство с разделом, счётчики и резерв бюджета на координаторе, WhenAll по
// слотам. Шов разбора подменён фейком с гейтами: чат «вошёл в разбор» (entered) и «держится в
// полёте» (его гейт не открыт). Параллельность доказывается самим протоколом: оба entered
// наступили до первого освобождения — значит оба разбора были в полёте одновременно; часы
// прохода заморожены, поэтому мерить пересечение интервалов нельзя — меряется факт полёта.
// Остальной смысл ночи (окно, лимиты, инварианты) — в WikiNightlyServiceTests на
// последовательном пути; здесь — поведение при исполнителях и путь без них.
public class WikiNightlyParallelTests : IDisposable
{
    private const string User = "u1";
    private const string SectionA = "projects/p1";
    private const string SectionB = "projects/p2";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly Sessions _sessions = new();
    private readonly Llm _llm = new();
    private readonly Flag _flag = new();
    // Журнал входов в разбор: чат и названный исполнитель (null — прежний маршрут места).
    private readonly List<(string Chat, string? Engine)> _calls = [];
    private readonly Dictionary<string, TaskCompletionSource> _entered = new(StringComparer.Ordinal);
    private DateTimeOffset _now = At(4);

    public WikiNightlyParallelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_nightly_par_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, null, flags: _flag);
        _keeper = new WikiKeeper(_notes, _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static DateTimeOffset At(int hour, int day = 2) => new(2026, 11, day, hour, 0, 0, TimeSpan.Zero);
    private string Root(string owner = User) => WikiPaths.Root(_dir, owner);

    // ─── фикстуры (образец WikiNightlyServiceTests) ─────────────────────────

    private void Configure(bool enabled = true, int start = 3, int end = 6, int perNight = 20, int perSection = 6,
        int chatsPerSection = 10, int budget = 700_000) =>
        WikiSettingsStore.Mutate(_dir, User, s => s with
        {
            Nightly = new WikiSettingsStore.NightlyConfig(enabled, start, end, perNight, perSection, chatsPerSection, budget),
        });

    private static List<StoredMessage> Hist(int humans, int len = 100)
    {
        var h = new List<StoredMessage>();
        for (var i = 0; i < humans; i++)
        {
            h.Add(new StoredUserMessage($"Реплика {i} " + new string('а', len)));
            h.Add(new StoredTextMessage("Принято."));
        }
        return h;
    }

    // ageMinutes — возраст UpdatedAt: очередь строится по нему убыв., тест задаёт порядок
    // очереди явными возрастами (меньший возраст — раньше в очереди).
    private void AddChat(string id, string project, int ageMinutes, int humans = 4, int len = 100)
    {
        var s = new Session
        {
            Id = id,
            OwnerId = User,
            ProjectId = project,
            Name = id,
            WikiMark = WikiMarks.Important,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-ageMinutes),
        };
        _sessions.All.Add(s);
        _sessions.History[id] = Hist(humans, len);
    }

    private WikiNightlyService Sut(
        Func<string, string, string?, CancellationToken, Task<WikiChatIngestService.ChatResult>> analyze) =>
        new(_sessions, new WikiChatRecalc(_sessions, _config), new WikiChatStage2(_llm, _sessions, _config),
            analyze, _llm, _config, NullLogger<WikiNightlyService>.Instance, _flag,
            () => _now, null, _keeper, null);

    private Task Tick(WikiNightlyService sut) => sut.RunNightTickAsync(_now);

    private WikiNightlyService.NightResult Last() => WikiNightlyService.ReadLast(Root()).Should().NotBeNull().And.Subject
        as WikiNightlyService.NightResult ?? throw new InvalidOperationException();

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Гейт входа чата в разбор: тест берёт его ДО прохода, фейк ставит результат при входе в шов.
    private TaskCompletionSource EnteredOf(string chat)
    {
        lock (_entered)
        {
            if (!_entered.TryGetValue(chat, out var t)) _entered[chat] = t = NewGate();
            return t;
        }
    }

    // Гейт открылся за предел: чат вошёл в разбор. Предел — не пауза в норме, а явный провал
    // там, где выдача не случилась (иначе тест висел бы до таймаута коллекции).
    private static async Task GateOpened(TaskCompletionSource entered) =>
        (await Task.WhenAny(entered.Task, Task.Delay(5000))).Should().BeSameAs(entered.Task, "чат не вошёл в разбор");

    // Фейк разбора: вход записан (и signal entered поднят), выход — после открытия гейта.
    // gateFor(null) — выходит сразу (когда полёт не нужен).
    private Func<string, string, string?, CancellationToken, Task<WikiChatIngestService.ChatResult>> GateDone(
        Func<string, TaskCompletionSource?>? gateFor) =>
        async (owner, chat, engine, ct) =>
        {
            lock (_calls) _calls.Add((chat, engine));
            EnteredOf(chat).TrySetResult();
            if (gateFor?.Invoke(chat) is { } gate) await gate.Task.ConfigureAwait(false);
            return new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, []);
        };

    // ─── сродство с разделом и параллельность разделов ──────────────────────

    [Fact]
    public async Task SameSection_NeverConcurrent()
    {
        Configure();
        // Два слота одного исполнителя свободны — и всё равно второй чат того же раздела
        // не стартует, пока первый в полёте.
        _llm.Executors = [new LocalEngineExecutor("local", "local", 2)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionA, ageMinutes: 20);
        var e1 = EnteredOf("c1");
        var g1 = NewGate();
        var g2 = NewGate();
        var sut = Sut(GateDone(chat => chat == "c1" ? g1 : g2));

        var tick = Tick(sut);
        await GateOpened(e1); // c1 в полёте — c2 не выдан: его раздел занят, хоть слот и свободен
        _calls.Select(c => c.Chat).Should().Equal(new[] { "c1" }, "второй чат того же раздела не выходит, пока первый в полёте");

        g2.SetResult(); // c2 выйдет сразу, как его отдадут
        g1.SetResult();
        await tick;

        _calls.Select(c => c.Chat).Should().Equal("c1", "c2");
        _calls.Select(c => c.Engine).Should().Equal("local", "local");
        Last().Analyzed.Should().Be(2);
        Last().Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
    }

    [Fact]
    public async Task TwoSections_RunConcurrently()
    {
        Configure();
        _llm.Executors = [new LocalEngineExecutor("local", "local", 2)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);
        var e1 = EnteredOf("c1");
        var e2 = EnteredOf("c2");
        var g1 = NewGate();
        var g2 = NewGate();
        var sut = Sut(GateDone(chat => chat == "c1" ? g1 : g2));

        var tick = Tick(sut);
        // Оба входа состоялись до первого освобождения: оба разбора в полёте одновременно.
        await GateOpened(e1);
        await GateOpened(e2);
        _calls.Count.Should().Be(2);

        g1.SetResult();
        g2.SetResult();
        await tick;

        Last().Analyzed.Should().Be(2);
        Last().Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
    }

    [Fact]
    public async Task ThreeChatsTwoSlots_SecondOfSectionWaits()
    {
        Configure();
        _llm.Executors = [new LocalEngineExecutor("local", "local", 2)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionA, ageMinutes: 20);
        AddChat("c3", SectionB, ageMinutes: 30);
        var e1 = EnteredOf("c1");
        var e3 = EnteredOf("c3");
        var g1 = NewGate();
        var g3 = NewGate();
        var sut = Sut(GateDone(chat => chat == "c1" ? g1 : chat == "c3" ? g3 : null));

        var tick = Tick(sut);
        await GateOpened(e1);
        await GateOpened(e3);
        // Слот два: не c2 (его раздел занят c1), а c3 — следующий чат со свободным разделом.
        _calls.Select(c => c.Chat).Should().Equal("c1", "c3");

        g1.SetResult(); // освободились раздел A и слот — только теперь берётся c2
        g3.SetResult();
        await tick;

        _calls.Select(c => c.Chat).Should().Equal("c1", "c3", "c2");
        Last().Analyzed.Should().Be(3);
    }

    // ─── резерв бюджета до выдачи ───────────────────────────────────────────

    [Fact]
    public async Task BudgetReservedBeforeIssue()
    {
        // Оценка чата резервируется ДО выдачи: при двух свободных слотах второй чат, не влезающий
        // в остаток с учётом оценки первого (в полёте), не выходит вовсе. При одном слоте эта
        // разница не видна — там резерв до выдачи и после завершения совпадают.
        Configure(budget: 300); // бюджет чатов = 210: оценка каждого 110, остатка после первого не хватает
        _llm.Executors = [new LocalEngineExecutor("local", "local", 2)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);
        var e1 = EnteredOf("c1");
        var g1 = NewGate();
        var sut = Sut(GateDone(chat => chat == "c1" ? g1 : null));

        var tick = Tick(sut);
        await GateOpened(e1);
        _calls.Select(c => c.Chat).Should().Equal(new[] { "c1" }, "второй чат не проходит по остатку бюджета и не выдаётся");

        g1.SetResult();
        await tick;

        Last().Outcome.Should().Be(WikiNightlyService.OutcomeBudget);
        Last().Selected.Should().Be(2); // второй чат был выбран до упора — как в последовательном пути
        Last().Analyzed.Should().Be(1);
    }

    // ─── путь без исполнителей: он прежний ──────────────────────────────────

    [Fact]
    public async Task EmptyExecutors_SequentialWithNullEngine()
    {
        // Исполнителей нет (пустой список стора) — прежний последовательный проход: engine=null,
        // порядок очереди сохранён, один разбор в полёте, итог тот же, что был до шага.
        Configure();
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionA, ageMinutes: 20);
        AddChat("c3", SectionB, ageMinutes: 30);
        var e1 = EnteredOf("c1");
        var e2 = EnteredOf("c2");
        var g1 = NewGate();
        // c1 держится в полёте, пока его гейт закрыт: при пустом списке исполнителей c2 не может
        // войти, пока c1 не вышел, — путь строго последовательный.
        var sut = Sut(GateDone(chat => chat == "c1" ? g1 : null));

        var tick = Tick(sut);
        await GateOpened(e1);
        _calls.Select(c => c.Chat).Should().Equal(new[] { "c1" }, "без исполнителей второй чат не стартует рядом с первым");
        g1.SetResult();
        await GateOpened(e2);
        await tick;

        _calls.Select(c => c.Chat).Should().Equal("c1", "c2", "c3");
        _calls.Select(c => c.Engine).Should().Equal(null, null, null);
        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
        last.Selected.Should().Be(3);
        last.Analyzed.Should().Be(3);
    }

    // ─── сбой слота не рвёт проход ──────────────────────────────────────────

    [Fact]
    public async Task SlotError_KillsNotPass()
    {
        Configure();
        _llm.Executors = [new LocalEngineExecutor("local", "local", 2)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);
        var e2 = EnteredOf("c2");
        var g2 = NewGate();
        // c1 падает исключением внутри слота: это отказ его разбора (тот же смысл, что прежний
        // catch) — координатор и соседний слот живы: c2 дорабатывается, в analyzed только он.
        var sut = Sut(async (owner, chat, engine, ct) =>
        {
            if (chat == "c1") throw new InvalidOperationException("движок не принял вызов");
            lock (_calls) _calls.Add((chat, engine));
            EnteredOf(chat).TrySetResult();
            await g2.Task.ConfigureAwait(false);
            return new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, []);
        });

        var tick = Tick(sut);
        await GateOpened(e2); // соседний слот жив: c2 в полёте, хотя слот c1 уже отказал
        g2.SetResult();
        await tick;

        _calls.Select(c => c.Chat).Should().Equal("c2");
        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
        last.Selected.Should().Be(2); // c1 был выбран, но не разобран — как прежний catch
        last.Analyzed.Should().Be(1);
    }

    // ─── подмены ─────────────────────────────────────────────────────────────

    private sealed class Llm : ICheapTextRunner
    {
        public bool Route = true;
        // Список исполнителей места (W4a2): что вернул бы шов после урезания по maxTotal —
        // сюда его кладет тест. Пусто — прежний путь без исполнителей.
        public IReadOnlyList<LocalEngineExecutor> Executors = [];

        public bool UsesLocal(string actionKey) => true;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => Route;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult("{}");

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult<string?>("{}");

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null, object? jsonFormat = null,
            CancellationToken ct = default) =>
            Task.FromResult(new OneShotResult("{}", null, 0));

        public Task<OneShotResult?> RunLocalOrChosenDetailedAsync(string actionKey, string prompt,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult<OneShotResult?>(new OneShotResult("{}", null, 0));

        public IReadOnlyList<LocalEngineExecutor> LocalExecutorsFor(string actionKey) =>
            actionKey == LocalActionCatalog.WikiNightly ? Executors : [];
    }

    private sealed class Sessions : ISessionDirectory
    {
        public readonly List<Session> All = [];
        public readonly Dictionary<string, List<StoredMessage>> History = [];

        public Session? GetById(string id) => All.FirstOrDefault(s => s.Id == id);
        public IReadOnlyCollection<Session> GetAll() => All;
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>(History.TryGetValue(sessionId, out var h) ? h : []);
        public bool SetWikiMark(string sessionId, string? mark) => true;
    }

    private sealed class Flag : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool On = true;

        public bool IsEnabled(string userId, string key) => On && key == FeatureFlagKeys.LlmWiki;
    }
}
