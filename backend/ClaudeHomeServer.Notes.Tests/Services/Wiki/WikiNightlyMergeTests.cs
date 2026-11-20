using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// C6 (очередь 2, выкат 2): предложения merge в ночи за тумблером Wiki:MergeProposals.
// Разбор и судья подменены фейками (образец WikiNightlyConnectivityTests); пакеты и страницы —
// настоящие, на диске. Пара «дубль»: заголовок «Каталог исходных» вложен в «Содержание каталога
// исходных» (WikiTopicScore.TitleSubset), A — страница с меньшим числом утверждений.
public class WikiNightlyMergeTests : IDisposable
{
    private const string User = "u1";
    private const string PageA = "Общее/Каталог-исходных.md";
    private const string PageB = "Общее/Содержание-каталога-исходных.md";

    private readonly string _dir;
    private readonly IConfigurationRoot _config;
    private readonly WikiKeeper _keeper;
    private readonly Sessions _sessions = new();
    private readonly Llm _llm = new();
    private readonly Flag _flag = new();
    private DateTimeOffset _now = new(2026, 11, 2, 4, 0, 0, TimeSpan.Zero);

    public WikiNightlyMergeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_nightly_merge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        var notes = new NotesService(projects, _config, NullLogger<NotesService>.Instance, null, flags: _flag);
        _keeper = new WikiKeeper(notes, _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
        WikiSettingsStore.Mutate(_dir, User, s => s with
        {
            Nightly = new WikiSettingsStore.NightlyConfig(true, 3, 6, 20, 6, 10, 700_000),
        });
        _sessions.All.Add(new Session { Id = "c1", OwnerId = User, ProjectId = "p1", Name = "c1", WikiMark = WikiMarks.Important, UpdatedAt = DateTime.UtcNow });
        _sessions.History["c1"] = [];
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Root() => WikiPaths.Root(_dir, User);

    private static string Content(string title, int statements)
    {
        var sb = new StringBuilder("---\ntype: topic\nsummary: краткое описание " + title + "\n---\n\n# ").Append(title).Append("\n\n## Сводка\n");
        for (var i = 0; i < statements; i++)
            sb.Append("- Факт ").Append(i + 1).Append(".\n");
        sb.Append("\n## Хронология\n\n## Противоречия\n\n## История\n");
        return sb.ToString();
    }

    private void WritePage(string path, string title, int statements)
    {
        var full = Path.Combine(Root(), "pages", path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, Content(title, statements));
    }

    private void WriteDuplicatePair()
    {
        WritePage(PageA, "Каталог исходных", 1);
        WritePage(PageB, "Содержание каталога исходных", 3);
    }

    private void EnableMerge() => _config["Wiki:MergeProposals"] = "true";

    private WikiNightlyService Sut(ILogger<WikiNightlyService>? log = null,
        Func<string, string, CancellationToken, Task<WikiJudgeSweep.JudgeSweepResult>>? judge = null) =>
        new(_sessions, new WikiChatRecalc(_sessions, _config), new WikiChatStage2(_llm, _sessions, _config),
            (o, c, e, ct) => Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeNothingNew, null, [])),
            _llm, _config, log ?? NullLogger<WikiNightlyService>.Instance, _flag,
            () => _now, null, _keeper, judge);

    private WikiNightlyService.NightResult Last() => WikiNightlyService.ReadLast(Root()).Should().NotBeNull().And.Subject
        as WikiNightlyService.NightResult ?? throw new InvalidOperationException();

    private void AddOpenPackage(string tag)
    {
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = "common",
            Sources = [new WikiPackage.SourceRef("session", "chat-" + tag)],
            Operations =
            [
                new WikiPackage.Operation
                {
                    Op = WikiPackage.OpAppend,
                    Page = "Общее/Чужая-" + tag + ".md",
                    Text = "утверждение из чата",
                    Class = WikiPackage.ClassKey("session", WikiPackage.OpAppend),
                    Decision = WikiPackage.DecisionAuto,
                },
            ],
            Status = WikiPackage.StatusChecking,
        };
        WikiPackageStore.Create(Root(), pkg).Ok.Should().BeTrue();
        WikiPackageStore.Transition(Root(), pkg.Id, WikiPackage.StatusProposed).Ok.Should().BeTrue();
    }

    private List<WikiPackage> MergePackages() => WikiPackageStore.List(Root()).Packages
        .Where(p => p.Operations.Any(o => o.Op == WikiPackage.OpMerge)).ToList();

    [Fact]
    public async Task Тумблер_ПоУмолчаниюВыключен_ПакетовНет_ПолеNull()
    {
        WriteDuplicatePair();

        await Sut().RunNightTickAsync(_now);

        MergePackages().Should().BeEmpty();
        Last().MergeProposed.Should().BeNull("тумблер выключен — «не измерено»");
    }

    [Fact]
    public async Task Тумблер_Включён_ПакетMerge_ДоСудьи_ВоВходящих()
    {
        WriteDuplicatePair();
        EnableMerge();
        var log = new RecordingLog();
        List<WikiPackage>? seenByJudge = null;
        var sut = Sut(log, judge: (o, root, ct) =>
        {
            seenByJudge = MergePackages();
            return Task.FromResult(new WikiJudgeSweep.JudgeSweepResult(0, 0m, 0, 0, 0, 0, null));
        });

        await sut.RunNightTickAsync(_now);

        seenByJudge.Should().ContainSingle("порядок: окна → merge → судья — судья видит предложение");
        var pkg = MergePackages().Single();
        pkg.Status.Should().Be(WikiPackage.StatusProposed);
        pkg.Author.Should().Be(WikiPackage.AuthorAi);
        var op = pkg.Operations.Single();
        op.Op.Should().Be(WikiPackage.OpMerge);
        op.Page.Should().Be(PageA, "A — страница с меньшим числом утверждений");
        op.Target.Should().Be(PageB);
        op.ReasonCode.Should().Be(WikiApplyPolicy.CodeMergeProposal);
        op.Decision.Should().Be(WikiPackage.DecisionInbox);
        op.BaseHashes.Keys.Should().BeEquivalentTo(PageA, PageB);
        Last().MergeProposed.Should().Be(1);
        log.Rows.Select(x => x.Message).Should().Contain(m => m.Contains("предложено merge 1"));
    }

    [Fact]
    public async Task СтраницаAПринятаЧеловеком_Adopt_ВКарантине_ПакетаНет()
    {
        // Мутация: убрать quarantined.UnionWith(adopted) в ProposeMergesForRoot — пакет создаётся.
        WriteDuplicatePair();
        EnableMerge();
        WikiManifest.AppendLog(Root(), WikiPackage.ActionAdopt, "common", [PageA], null, "user", null);

        await Sut().RunNightTickAsync(_now);

        MergePackages().Should().BeEmpty("применение отказало бы: A принята человеком");
        Last().MergeProposed.Should().Be(0);
    }

    [Fact]
    public async Task ДвеМелкиеСВложениемЗаголовков_ОдинПакет()
    {
        WritePage(PageA, "Каталог исходных", 1);
        WritePage(PageB, "Содержание каталога исходных", 1);
        EnableMerge();

        await Sut().RunNightTickAsync(_now);

        MergePackages().Should().ContainSingle();
    }

    [Fact]
    public async Task ЦепочкаДублей_ВторойПакетСТойЖеСтраницейНеСоздаётся()
    {
        // Pick отдаёт 1→3 и 2→3 (одна B в двух парах; в других раскладках — цепочка 1→2→3). Мутация: убрать фильтр `taken` —
        // пакетов два, и второй при применении откажет («другой открытый пакет»).
        const string pageC = "Общее/Подробное-содержание-каталога-исходных.md";
        WritePage(PageA, "Каталог исходных", 1);
        WritePage(PageB, "Содержание каталога исходных", 2);
        WritePage(pageC, "Подробное содержание каталога исходных", 3);
        EnableMerge();

        await Sut().RunNightTickAsync(_now);

        var pkg = MergePackages().Should().ContainSingle().Subject;
        pkg.Operations.Single().Page.Should().Be(PageA);
        pkg.Operations.Single().Target.Should().Be(pageC, "первая пара по порядку Pick; вторая делит с ней B");
        Last().MergeProposed.Should().Be(1);
    }

    [Fact]
    public async Task ОткрытыхНеМеньшеПятнадцати_Merge_НеПредлагается()
    {
        WriteDuplicatePair();
        EnableMerge();
        for (var i = 0; i < WikiNightlyService.MergeOpenLimit; i++)
            AddOpenPackage("o" + i);

        await Sut().RunNightTickAsync(_now);

        MergePackages().Should().BeEmpty();
        Last().MergeProposed.Should().Be(0, "тумблер включён, но лимит открытых исчерпан — измерено: ноль");
    }

    [Fact]
    public void MergeProposals_RaceManualIngest_OpenLimitHeld()
    {
        // Мутация: убрать перепроверку `CountOpen >= MergeOpenLimit` внутри цикла ProposeMerges —
        // после «ручного разбора» ночь создаст все пары, открытых станет больше лимита.
        var pairs = new List<(string A, string B)>();
        for (var i = 0; i < 4; i++)
        {
            WritePage($"Общее/Альфа{i}.md", $"Альфа{i}", 1);
            WritePage($"Общее/Бета{i}.md", $"Бета{i}", 2);
            pairs.Add(($"Общее/Альфа{i}.md", $"Общее/Бета{i}.md"));
        }
        for (var i = 0; i < WikiNightlyService.MergeOpenLimit - 2; i++)
            AddOpenPackage("o" + i);

        var created = WikiNightlyService.ProposeMerges(Root(), pairs, afterCreate: n =>
        {
            // «Ручной Разобрать» добавил пакет между двумя созданиями ночи.
            if (n == 1) AddOpenPackage("manual");
        });

        created.Should().Be(1, "второй пакет упёрся в лимит, добавленный ручным разбором");
        WikiPackageStore.CountOpen(Root()).Should().BeLessThanOrEqualTo(WikiNightlyService.MergeOpenLimit);
    }

    private sealed class RecordingLog : ILogger<WikiNightlyService>
    {
        public List<(LogLevel Level, string Message)> Rows { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Rows.Add((logLevel, formatter(state, exception)));
    }

    private sealed class Llm : ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) => Task.FromResult("{}");

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) => Task.FromResult<string?>("{}");

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null, object? jsonFormat = null,
            CancellationToken ct = default) =>
            Task.FromResult(new OneShotResult("{}", new OneShotUsage(10, 0, 0, 5, 0.0, "fake"), 1));

        public IReadOnlyList<LocalEngineExecutor> LocalExecutorsFor(string actionKey) => [];

        public Task<bool> IsLocalEngineReadyAsync(string engine, CancellationToken ct = default) => Task.FromResult(true);
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
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }
}
