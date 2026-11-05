using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// FIN3-3: приватный чат (Session.PrivateToUserId) не читает ни один машинный потребитель вики
// (инвариант: приватный чат виден только автору; образец — автообучение памяти,
// TeamMemoryAutolearnService). Три пути проверки: очередь ночного разбора (BuildQueue),
// разбор чата (WikiChatIngestService.AnalyzeAsync — общий для кнопки «разобрать сейчас»,
// wiki_ingest session:{id} и ночи), и список отбора (GET /api/wiki/chats — List).
// Приватный чат отвечает как несуществующий: тот же код not_found и тот же текст, что у
// несуществующего — приватность не выдаёт существования чата вызывающему.
// Фикстуры — по образцу WikiNightlyServiceTests / WikiChatsControllerTests: цепки настоящие,
// модель и разбор — подмены; ResolveOwnerId фикстуры = OwnerId (механика делегирования —
// SessionManager, не этот тест).
public class WikiPrivateChatTests : IDisposable
{
    private const string User = "u1";          // владелец проекта
    private const string Participant = "u2";   // автор приватного чата в общем проекте
    private const string Project = "p1";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly Sessions _sessions = new();
    private readonly Llm _llm = new();
    private readonly Flag _flag = new();
    private readonly List<string> _analyzed = [];
    private DateTimeOffset _now = At(4);

    public WikiPrivateChatTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_private_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, null, flags: _flag);
        _keeper = new WikiKeeper(_notes, _config);
        // Ночь перечисляет только владельцев с заведённой вики (EnumerateWikiOwners).
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── фикстуры ──────────────────────────────────────────────────────────

    private static DateTimeOffset At(int hour, int day = 2) => new(2026, 11, day, hour, 0, 0, TimeSpan.Zero);

    private void Configure() =>
        WikiSettingsStore.Mutate(_dir, User, s => s with
        {
            Nightly = new WikiSettingsStore.NightlyConfig(true, 3, 6, 20, 6, 10, 700_000),
        });

    private static List<StoredMessage> Hist(int humans)
    {
        var h = new List<StoredMessage>();
        for (var i = 0; i < humans; i++)
        {
            h.Add(new StoredUserMessage($"Реплика {i}"));
            h.Add(new StoredTextMessage("Принято."));
        }
        return h;
    }

    // Чат своего проекта; privateTo — признак приватности (null — обычный чат).
    // OwnerId остаётся владельцем проекта: так приватный чат участника и резолвится на него.
    private void AddChat(string id, string? privateTo = null)
    {
        _sessions.All.Add(new Session
        {
            Id = id,
            OwnerId = User,
            ProjectId = Project,
            Name = id,
            WikiMark = WikiMarks.Important, // минуя ступени 1–2: приватность — единственный различитель
            PrivateToUserId = privateTo,
            UpdatedAt = DateTime.UtcNow,
        });
        _sessions.History[id] = Hist(4); // дельта ≥ MinHumanDelta есть у всех трёх
    }

    private WikiNightlyService NightSut() =>
        new(_sessions, new WikiChatRecalc(_sessions, _config), new WikiChatStage2(_llm, _sessions, _config),
            (_, chat, _) =>
            {
                lock (_analyzed) _analyzed.Add(chat);
                return Task.FromResult(new WikiChatIngestService.ChatResult(
                    WikiChatIngestService.OutcomeNothingNew, null, []));
            },
            _llm, _config, NullLogger<WikiNightlyService>.Instance, _flag, () => _now);

    private WikiChatIngestService RealIngest() =>
        new(new WikiSourceCapture(_notes, _projects, _sessions, _config, _keeper),
            new WikiIngestService(_llm, new NoSpheres(),
                new WikiApplyService(_notes, _keeper, new NoSpheres(), _config), _config),
            _sessions, _keeper, _config);

    private WikiChatsController Controller()
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, User)], "test"));
        return new WikiChatsController(_sessions, RealIngest(), new WikiChatRecalc(_sessions, _config), _keeper, _config, _flag)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    // ─── 1. Очередь ночи (c1) ──────────────────────────────────────────────

    [Fact]
    public async Task Ночь_ПриватныеЧатыВОчередьНеБерёт_ОбычныйБерёт()
    {
        Configure();
        AddChat("c-open");
        AddChat("c-participant-private", privateTo: Participant); // чат участника, резолвится на владельца
        AddChat("c-owner-private", privateTo: User);              // и свой приватный — тот же отказ

        await NightSut().RunNightTickAsync(_now);

        // Мутация «убрать PrivateToUserId из BuildQueue»: оба приватных (пометка «важный»,
        // дельта есть) входят в очередь и разбираются — тест красный.
        _analyzed.Should().Equal("c-open");
    }

    // ─── 2. Разбор чата (c1) ───────────────────────────────────────────────

    [Fact]
    public async Task РазборЧата_Приватный_ОтказКакНесуществующий_БезОбращенияКМодели()
    {
        AddChat("c-participant-private", privateTo: Participant);
        AddChat("c-owner-private", privateTo: User);

        var sut = RealIngest();
        var foreignPrivate = await sut.AnalyzeAsync(User, "c-participant-private");
        foreignPrivate.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected);
        foreignPrivate.Code.Should().Be(WikiChatIngestService.CodeNotFound);
        foreignPrivate.Reason.Should().Be("чат не существует");

        // Свой приватный чат — тот же исход: машинный потребитель не читает его и у автора.
        var ownPrivate = await sut.AnalyzeAsync(User, "c-owner-private");
        ownPrivate.Code.Should().Be(WikiChatIngestService.CodeNotFound);
        ownPrivate.Reason.Should().Be("чат не существует");

        // Несуществующий чат неотличим: тот же код и тот же текст — приватность не выдаёт
        // существования чата вызывающему.
        var missing = await sut.AnalyzeAsync(User, "no-such-chat");
        (missing.Code, missing.Reason).Should().Be((foreignPrivate.Code, foreignPrivate.Reason));

        // Мутация «убрать проверку PrivateToUserId в AnalyzeAsync»: приватный чат доходит до
        // настоящей цепки и до подменённой модели — и Code, и BeEmpty краснеют.
        _llm.Calls.Should().BeEmpty("отказ — до обращения к модели");
    }

    // ─── 3. Список отбора (c2) ─────────────────────────────────────────────

    [Fact]
    public void Список_ПриватныеЧатыВыпадаютИзЗаписейИCounts()
    {
        AddChat("c-open");
        AddChat("c-participant-private", privateTo: Participant);
        AddChat("c-owner-private", privateTo: User);

        // Записи стора есть на все три чата (стор — про чаты, а не про приватность):
        // список обязан опустить приватные и на items, и на counts.
        var root = WikiPaths.Root(_dir, User);
        foreach (var id in new[] { "c-open", "c-participant-private", "c-owner-private" })
            WikiChatTriageStore.Upsert(root,
                new WikiChatTriageEntry(id, 0, null, WikiChatScorer.ZoneTriage, null, 0, default))
                .Ok.Should().BeTrue();

        var list = (WikiChatsListDto)((OkObjectResult)Controller().List()).Value!;

        list.Items.Select(i => i.SessionId).Should().Equal("c-open");
        list.Counts.Should().HaveCount(1);
        list.Counts[WikiChatScorer.ZoneTriage].Should().Be(1);

        // Мутация «убрать PrivateToUserId из List»: две приватные записи встают в items,
        // counts.triage = 3 — тест красный.
    }

    // ─── подмены (образец WikiNightlyServiceTests) ─────────────────────────

    private sealed class Llm : ICheapTextRunner
    {
        public readonly List<string> Calls = [];

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(Answer(actionKey));

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult<string?>(Answer(actionKey));

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null, object? jsonFormat = null,
            CancellationToken ct = default) =>
            Task.FromResult(new OneShotResult(Answer(actionKey), new OneShotUsage(10, 0, 0, 5, 0.0, "fake"), 1));

        private string Answer(string actionKey)
        {
            lock (Calls) Calls.Add(actionKey);
            // Всё, что доходит до модели, получает «нечего добавить»: разбор завершается
            // штатно, красный тест — про сам факт вызова.
            return $$"""{"outcome":"{{WikiKeeperPrompts.NothingToAdd}}","reason":"не о чём писать"}""";
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

    private sealed class Flag : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }

    private sealed class NoSpheres : ISphereDirectory
    {
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public bool Enabled(string ownerId) => false;
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }
}
