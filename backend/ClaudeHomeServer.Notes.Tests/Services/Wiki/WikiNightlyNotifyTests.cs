using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Утреннее уведомление ночного разбора (Ф2, N7c). Отделён от WikiNightlyServiceTests (там идёт
// доработка N7b): фикстуры те же, проверяется только конец прохода — сообщение владельцу.
//
// Контракт: ровно одно уведомление за проход с человеческим текстом «Ночной разбор: применено N,
// во „Входящих” M» и названием пути «Вики → Журнал» (Url ведёт во вкладку «Вики», обещания перехода
// в тексте нет); при исходе, отличном от обычного, — причина простым
// языком. Молчание: analyzed=0 и completed; флаг/Enabled выключен (там и итога нет). Сбой
// доставки: предупреждение в журнале, итог ночи уже записан, исключение не выходит из службы.
public class WikiNightlyNotifyTests : IDisposable
{
    private const string User = "u1";
    private const string Section = "projects/p1";
    private const string PagePath = "Проекты/p1/Сущность.md";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly Sessions _sessions = new();
    private readonly Llm _llm = new();
    private readonly Flag _flag = new();
    private DateTimeOffset _now = At(4);

    public WikiNightlyNotifyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_nightly_notify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _notes = new NotesService(projects, _config, NullLogger<NotesService>.Instance, null, _flag);
        _keeper = new WikiKeeper(_notes, _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
        _keeper.WritePageAsync(User, Section, PagePath, "---\ntype: topic\naliases: []\nowns: []\nsources: []\nstatus: active\n---\n\n# Сущность\n\n## Сводка\n\n## Хронология\n\n## Противоречия\n\n## История\n")
            .GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static DateTimeOffset At(int hour, int day = 2) => new(2026, 11, day, hour, 0, 0, TimeSpan.Zero);
    private string Root(string owner = User) => WikiPaths.Root(_dir, owner);
    private string LastFile(string owner = User) => Path.Combine(Root(owner), "keeper", "nightly-last.json");

    // ─── фикстуры (те же, что у WikiNightlyServiceTests) ────────────────────

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

    private void AddChat(string id, int humans = 4, string? mark = WikiMarks.Important, string project = "p1",
        int ageMinutes = 0)
    {
        var s = new Session
        {
            Id = id,
            OwnerId = User,
            ProjectId = project,
            Name = id,
            WikiMark = mark,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-ageMinutes),
        };
        _sessions.All.Add(s);
        _sessions.History[id] = Hist(humans);
    }

    private WikiNightlyService Sut(
        Func<string, string, CancellationToken, Task<WikiChatIngestService.ChatResult>>? analyze = null,
        INotificationSender? notifier = null,
        ILogger<WikiNightlyService>? log = null) =>
        new(_sessions, new WikiChatRecalc(_sessions, _config), new WikiChatStage2(_llm, _sessions, _config),
            analyze ?? Done(), _llm, _config, log ?? NullLogger<WikiNightlyService>.Instance, _flag, () => _now, notifier, _keeper);

    private static Func<string, string, CancellationToken, Task<WikiChatIngestService.ChatResult>> Done() =>
        (owner, chat, ct) => Task.FromResult(new WikiChatIngestService.ChatResult(
            WikiChatIngestService.OutcomeNothingNew, null, []));

    private Task Tick(WikiNightlyService sut) => sut.RunNightTickAsync(_now);

    private WikiNightlyService.NightResult Last() => WikiNightlyService.ReadLast(Root()).Should().NotBeNull().And.Subject
        as WikiNightlyService.NightResult ?? throw new InvalidOperationException();

    private void AddOpenPackages(int n)
    {
        for (var i = 0; i < n; i++)
        {
            var r = WikiPackageStore.Create(Root(), new WikiPackage
            {
                Id = WikiPackageStore.NewId(),
                Section = Section,
                Sources = [new WikiPackage.SourceRef("note", "note:x")],
                Operations = [new WikiPackage.Operation
                {
                    Op = WikiPackage.OpAppend,
                    Page = PagePath,
                    Text = "утверждение " + i,
                    Class = WikiPackage.ClassKey("note", WikiPackage.OpAppend),
                    Decision = WikiPackage.DecisionInbox,
                    Reason = "спорное",
                    BaseHashes = new Dictionary<string, string>(),
                }],
                Status = WikiPackage.StatusChecking,
            });
            r.Ok.Should().BeTrue(r.Reason);
        }
    }

    // ─── c1: применено 3, во «Входящих» 1 → ровно одно уведомление ──────────

    [Fact]
    public async Task ПроходПрименено3ИОдноВоВходящих_РовноОдноУведомлениеСЧислами()
    {
        Configure();
        AddChat("c1", ageMinutes: 0);
        AddChat("c2", ageMinutes: 1);
        var notifier = new Notifier();
        Task<WikiChatIngestService.ChatResult> Analyze(string owner, string chat, CancellationToken ct) => chat switch
        {
            "c1" => Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeApplied, null, ["pkg-1", "pkg-2", "pkg-3"])),
            "c2" => Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeAwaitingHuman, null, [])),
            _ => Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeNothingNew, null, [])),
        };

        await Tick(Sut(Analyze, notifier));

        var last = Last();
        last.Applied.Should().Be(3);
        last.ToInbox.Should().Be(1);
        notifier.Sent.Should().HaveCount(1, "уведомление — одно на проход");
        var n = notifier.Sent[0];
        n.Title.Should().Contain("применено 3");
        n.Title.Should().Contain("во «Входящих» 1");
        // Путь назван текстом, а не обещан переходом: Url ведёт во вкладку «Вики» (подкладка
        // «Журнал» deep-link не имеет), текст — «во вкладке «Вики» → «Журнал»».
        n.Url.Should().Be("#/wiki");
        n.Body.Should().Contain("во вкладке «Вики» → «Журнал»");
        n.Body.Should().NotContain("в «Журнале»", "текст не обещает перехода, которого Url не делает");
        n.Kind.Should().Be("info", "обычный исход — не предупреждение");
    }

    // ─── B4b (ADR-038, предохранитель 6): сводные строки о человеке — в то же уведомление ───

    [Fact]
    public async Task ЗамениИИФормулировкиЧеловекаВДвухЧатах_ВУведомленииОднаСводнаяСтрока_СЧислом()
    {
        // Мутация «строка на каждый чат» (дописывать строку внутри цикла разборов): две строки
        // «ИИ заменил…» — Be(1) по числу вхождений краснеет. Мутация «не суммировать» (брать
        // последний чат): «5 ваших формулировок» краснеет. Страница b оспорена в обоих чатах — это
        // одна страница, не две: «на 2 страницах».
        Configure();
        AddChat("c1", ageMinutes: 0);
        AddChat("c2", ageMinutes: 1);
        var notifier = new Notifier();
        Task<WikiChatIngestService.ChatResult> Analyze(string owner, string chat, CancellationToken ct) => chat switch
        {
            "c1" => Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeApplied, null, ["pkg-1"], OverrodeHuman: 2, DisputedPages: ["a.md", "b.md"])),
            "c2" => Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeApplied, null, ["pkg-2"], OverrodeHuman: 3, DisputedPages: ["b.md"])),
            _ => Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeNothingNew, null, [])),
        };

        await Tick(Sut(Analyze, notifier));

        notifier.Sent.Should().HaveCount(1, "второго уведомления нет");
        var body = notifier.Sent[0].Body;
        body.Should().Contain("ИИ заменил 5 ваших формулировок.");
        body.Should().Contain("ИИ отметил противоречие на 2 страницах.");
        System.Text.RegularExpressions.Regex.Matches(body, "ИИ заменил").Count.Should().Be(1, "одна сводная строка за ночь");
        Last().OverrodeHuman.Should().Be(5);
        Last().DisputedPages.Should().Be(2);
    }

    [Fact]
    public async Task ЧеловекаНеТрогали_СтрокиОНёмВУведомленииНет()
    {
        Configure();
        AddChat("c1");
        var notifier = new Notifier();
        Task<WikiChatIngestService.ChatResult> Analyze(string owner, string chat, CancellationToken ct) =>
            Task.FromResult(new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeApplied, null, ["pkg-1"]));

        await Tick(Sut(Analyze, notifier));

        notifier.Sent[0].Body.Should().NotContain("ИИ заменил").And.NotContain("противоречие");
    }

    [Theory]
    [InlineData(1, "1 вашу формулировку", "1 странице")]
    [InlineData(2, "2 ваши формулировки", "2 страницах")]
    [InlineData(5, "5 ваших формулировок", "5 страницах")]
    [InlineData(11, "11 ваших формулировок", "11 страницах")]
    [InlineData(21, "21 вашу формулировку", "21 странице")]
    public void NotificationBody_ЧислоСклоняется(int n, string replaced, string pages)
    {
        var body = WikiNightlyService.NotificationBody(null, n, n);

        body.Should().Contain($"ИИ заменил {replaced}.");
        body.Should().Contain($"ИИ отметил противоречие на {pages}.");
    }

    // ─── c2: молчание ────────────────────────────────────────────────────────

    [Fact]
    public async Task НичегоНеРазобраноИНочьОбычная_УведомленийНет()
    {
        // Дельта 2 реплики — чат не отобран: проход обычный, analyzed=0. Мутация «слать всегда»
        // (убрать проверку Analyzed==0 && Completed) — этот тест краснеет.
        Configure();
        AddChat("c1", humans: 2);
        var notifier = new Notifier();

        await Tick(Sut(notifier: notifier));

        Last().Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
        Last().Analyzed.Should().Be(0);
        notifier.Sent.Should().BeEmpty("нечего сказать: ни разборов, ни отказа");
    }

    [Fact]
    public async Task ФлагВыключен_НиУведомленияНиИтога()
    {
        Configure();
        AddChat("c1");
        _flag.On = false;
        var notifier = new Notifier();

        await Tick(Sut(notifier: notifier));

        notifier.Sent.Should().BeEmpty();
        File.Exists(LastFile()).Should().BeFalse("пропуск без записи — и без сообщения");
    }

    // ─── исход, отличный от обычного: причина простым языком ────────────────

    [Fact]
    public async Task УпорВЛимитОткрытых_УведомлениеСЧеловеческойПричиной()
    {
        Configure();
        AddChat("c1");
        AddOpenPackages(WikiPackage.MaxOpenPackages);
        var notifier = new Notifier();

        await Tick(Sut(notifier: notifier));

        Last().Outcome.Should().Be(WikiNightlyService.OutcomeOpenLimit);
        notifier.Sent.Should().HaveCount(1);
        var n = notifier.Sent[0];
        n.Kind.Should().Be("warning", "прерванная ночь — предупреждение");
        n.Body.Should().Contain("лимит открытых пакетов");
        n.Body.Should().Contain("«Входящие»", "причина называет, что делать");
    }

    [Fact]
    public async Task ОшибкаПрохода_ПричинаГоворитВСледующуюНочь_БезВнутреннегоЖаргона()
    {
        // Маршрута места нет → outcome=error. Текст причины — человеческий: «в следующую ночь»,
        // а не «в следующий тик» (тик — внутренний жаргон службы).
        Configure();
        AddChat("c1");
        _llm.Route = false;
        var notifier = new Notifier();

        await Tick(Sut(notifier: notifier));

        Last().Outcome.Should().Be(WikiNightlyService.OutcomeError);
        notifier.Sent.Should().HaveCount(1);
        var n = notifier.Sent[0];
        n.Kind.Should().Be("warning");
        n.Body.Should().Contain("в следующую ночь");
        n.Body.Should().NotContain("тик", "тик — внутренний жаргон, в тексте его быть не должно");
    }

    // ─── c4: сбой доставки не рвёт службу и не отменяет итог ────────────────

    [Fact]
    public async Task ОтправительБросает_СлужбаЖива_ИтогЗаписан_ВЖурналеПредупреждение()
    {
        Configure();
        AddChat("c1");
        var log = new RecordingLog();

        var act = async () => await Tick(Sut(notifier: new ThrowingNotifier(), log: log));

        await act.Should().NotThrowAsync("исключение отправителя не выходит из службы");

        Last().Outcome.Should().Be(WikiNightlyService.OutcomeCompleted,
            "итог записан до попытки доставки: сбой сообщения его не отменяет");
        log.Rows.Should().Contain(r => r.Level == LogLevel.Warning && r.Message.Contains("не доставлено"),
            "сбой доставки назван в журнале — молчания нет");
    }

    // ─── подменные швы (те же, что у WikiNightlyServiceTests) ────────────────

    private static readonly string NothingJson =
        $$"""{"outcome":"{{WikiKeeperPrompts.NothingToAdd}}","reason":"не о чём писать"}""";

    private sealed class Llm : ICheapTextRunner
    {
        public readonly List<(string ActionKey, string Prompt)> Calls = [];
        public bool Route = true;

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => Route;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add((actionKey, prompt));
            return Task.FromResult(NothingJson);
        }

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add((actionKey, prompt));
            return Task.FromResult<string?>(NothingJson);
        }

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null, object? jsonFormat = null,
            CancellationToken ct = default)
        {
            lock (Calls) Calls.Add((actionKey, prompt));
            return Task.FromResult(new OneShotResult(NothingJson, new OneShotUsage(10, 0, 0, 5, 0.0, "fake"), 1));
        }
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

    private sealed class Flag : IFeatureFlagGate
    {
        public bool On = true;
        public bool IsEnabled(string userId, string key) => On && key == FeatureFlagKeys.LlmWiki;
    }

    private sealed class Notifier : INotificationSender
    {
        public List<CreateNotificationRequest> Sent { get; } = [];

        public Task<string> SendAsync(string userId, CreateNotificationRequest req, bool sendPush = false)
        {
            Sent.Add(req);
            return Task.FromResult("n-" + Sent.Count);
        }
    }

    private sealed class ThrowingNotifier : INotificationSender
    {
        public Task<string> SendAsync(string userId, CreateNotificationRequest req, bool sendPush = false) =>
            throw new InvalidOperationException("доставка отказала");
    }

    private sealed class RecordingLog : ILogger<WikiNightlyService>
    {
        public List<(LogLevel Level, string Message)> Rows { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Rows.Add((logLevel, formatter(state, exception)));
    }
}
