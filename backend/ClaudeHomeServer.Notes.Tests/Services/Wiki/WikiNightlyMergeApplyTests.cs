using System.Security.Cryptography;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// D1 (QA-2, дефект 93572246): предложение merge из ночи обязано применяться. Сквозной путь:
// ночь (ProposeMergesForRoot) → пакет с источником-маркером «session:merge:{A}» → решение
// (человека — тем же входом, что REST decide, или судьи) → ApplyAsync БЕЗ контекста → файлы.
// Прежние тесты применяли merge с настоящим ref «session:chat-1» и карточкой — боевой путь
// ночи они не проверяли. Здесь пакет строит сама ночь (WikiNightlyService.NewMergePackage).
public class WikiNightlyMergeApplyTests : IDisposable
{
    private const string User = "u1";
    private const string Section = "common";
    private const string CardTitle = "Карточка чата";
    private const string CardPath = "Общее/Карточка-чата.md";
    private const string PageA = "Общее/Каталог-исходных.md";
    private const string PageB = "Общее/Содержание-каталога-исходных.md";
    private const string TitleA = "Каталог исходных";
    private const string TitleB = "Содержание каталога исходных";
    private const string QuoteA = "Каталог собирается из исходных файлов";
    private const string QuoteB1 = "Содержание строится по каталогу";
    private const string QuoteB2 = "Раздел хранит оглавление";
    private const string QuoteB3 = "Оглавление обновляется ночью";

    private readonly string _dir;
    private readonly IConfigurationRoot _config;
    private readonly WikiKeeper _keeper;
    private readonly WikiApplyService _apply;
    private readonly WikiRetractService _retract;
    private readonly Sessions _sessions = new();
    private readonly Flag _flag = new();
    private readonly DateTimeOffset _now = new(2026, 11, 20, 4, 0, 0, TimeSpan.Zero);

    public WikiNightlyMergeApplyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_nightly_merge_apply_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["Wiki:MergeProposals"] = "true",
            })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        var notes = new NotesService(projects, _config, NullLogger<NotesService>.Instance, null, flags: _flag);
        _keeper = new WikiKeeper(notes, _config);
        _apply = new WikiApplyService(notes, _keeper, new NoSpheres(), _config);
        _retract = new WikiRetractService(notes, _keeper, new WikiPackageRevert(notes, _keeper, _config),
            _sessions, new NoSpheres(), _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
        WikiSettingsStore.Mutate(_dir, User, s => s with
        {
            Nightly = new WikiSettingsStore.NightlyConfig(true, 3, 6, 20, 6, 10, 700_000),
            AutoApplyEnabled = true,
        });
        _sessions.All.Add(new Session { Id = "c1", OwnerId = User, ProjectId = "p1", Name = "c1", WikiMark = WikiMarks.Important, UpdatedAt = DateTime.UtcNow });
        _sessions.History["c1"] = [];
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Root() => WikiPaths.Root(_dir, User);
    private string PageFull(string key) => Path.Combine(WikiPaths.Pages(_dir, User), key);

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // Страницы кладутся на диск напрямую вместе с записью манифеста: нужны сноски на карточку.
    private void PutRaw(string key, string content)
    {
        WikiFileWriter.Write(Root(), "pages/" + key, content);
        WikiManifest.RecordApplied(Root(), Section,
            new Dictionary<string, string> { [key] = Sha(File.ReadAllBytes(PageFull(key))) });
    }

    private static string Card() => $$"""
        ---
        type: source
        origin: session
        ref: session:s1#m1-9
        hash: 0123456789012345678901234567890123456789012345678901234567890123
        captured: 2026-11-20
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{CardTitle}}
        """;

    private static string Page(string title, string[] lines, string[] quotes)
    {
        var sb = new StringBuilder("---\ntype: topic\naliases: []\nowns: []\nsources: []\nstatus: active\n---\n\n# ")
            .Append(title).Append("\n\n## Сводка\n");
        for (var i = 0; i < lines.Length; i++)
            sb.Append(lines[i]).Append(" [^s").Append(i + 1).Append("]\n");
        sb.Append("\n## Хронология\n\n## Противоречия\n\n## История\n\n");
        for (var i = 0; i < quotes.Length; i++)
            sb.Append($"[^s{i + 1}]: [[{CardTitle}]] — «{quotes[i]}»\n");
        return sb.ToString();
    }

    // A — 1 утверждение, B — 3: направление merge A → B (меньшее в большее).
    private void SeedDuplicatePair()
    {
        PutRaw(CardPath, Card());
        PutRaw(PageA, Page(TitleA, ["Каталог собирается из исходных файлов"], [QuoteA]));
        PutRaw(PageB, Page(TitleB,
            ["Содержание строится по каталогу", "Раздел хранит оглавление", "Оглавление обновляется ночью"],
            [QuoteB1, QuoteB2, QuoteB3]));
    }

    private WikiNightlyService Nightly() =>
        new(_sessions, new WikiChatRecalc(_sessions, _config), new WikiChatStage2(new Llm(), _sessions, _config),
            (o, c, e, ct) => Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeNothingNew, null, [])),
            new Llm(), _config, NullLogger<WikiNightlyService>.Instance, _flag,
            () => _now, null, _keeper, null);

    private async Task<WikiPackage> ProposeByNight()
    {
        SeedDuplicatePair();
        await Nightly().RunNightTickAsync(_now);
        return WikiPackageStore.List(Root()).Packages
            .Single(p => p.Operations.Any(o => o.Op == WikiPackage.OpMerge));
    }

    private WikiPackage Read(string id) => WikiPackageStore.TryRead(Root(), id).Package!;

    private void AssertMergedIntoB()
    {
        File.Exists(PageFull(PageA)).Should().BeFalse("файл A удалён слиянием");
        var b = WikiPage.Parse(File.ReadAllText(PageFull(PageB))).Model!;
        var lines = b.Sections.Single(s => s.Name == "Сводка").Lines;
        lines.Should().HaveCount(4, "3 строки B и 1 перенесённая строка A");
        lines.Should().Contain(l => l.StartsWith("Каталог собирается из исходных файлов", StringComparison.Ordinal));
        b.Footnotes.Select(f => f.Quote).Should().Contain(QuoteA, "сноска перенесённой строки живёт на B");
        b.Frontmatter.Aliases.Should().Contain(TitleA, "заголовок A — алиас B");
    }

    [Fact]
    public async Task НочнойПакет_ПринятьЧеловеком_БезКонтекста_Применён_AУдалена_СтрокиИСноскиНаB()
    {
        // Мутация: убрать пропуск FindSourceCards для пакета из одних merge (WikiApplyService, mergeOnly)
        // — применение отказывает apply_failed «карточка источника пакета не найдена».
        var pkg = await ProposeByNight();
        pkg.Sources.Should().ContainSingle().Which.Ref.Should().Be(WikiPackage.MergeSourcePrefix + PageA,
            "ночь ставит источник-маркер пары, а не настоящую карточку");

        // Тот же вход, что REST decide: DecideOperation → ApplyDecidedAsync без ApplyContext.
        var decided = WikiPackageStore.DecideOperation(Root(), pkg.Id, 0, accept: true);
        decided.Ok.Should().BeTrue(decided.Reason);
        var res = await _apply.ApplyDecidedAsync(User, Root(), decided.Package!);

        res.Outcome.Should().Be(WikiApplyService.DecidedOutcome.Ok);
        res.Apply.Should().NotBeNull();
        res.Apply!.Value.Applied.Should().BeTrue($"{res.Apply.Value.Code}: {res.Apply.Value.Reason}");
        Read(pkg.Id).Status.Should().Be(WikiPackage.StatusApplied);
        AssertMergedIntoB();
    }

    [Fact]
    public async Task НочнойПакет_ВердиктСудьиApply_Применён_AУдалена_СтрокиИСноскиНаB()
    {
        var pkg = await ProposeByNight();
        var llm = new FakeJudge("""{"verdicts":[{"index":0,"decision":"apply","why":"дубль по заголовкам"}]}""");

        var res = await new WikiJudgeSweep(llm, _apply, _config).RunAsync(User, Root());

        res.FirstFailure.Should().BeNull();
        res.Applied.Should().Be(1, "судья принял merge");
        Read(pkg.Id).Status.Should().Be(WikiPackage.StatusApplied,
            "применение идёт тем же ApplyDecidedAsync без контекста и без карточки источника");
        AssertMergedIntoB();
    }

    [Fact]
    public void ФормаПакета_MergeИCreate_ВОдномПакете_Отказ()
    {
        SeedDuplicatePair();
        var pkg = WikiNightlyService.NewMergePackage(Section, PageA, PageB, "h1", "h2");
        pkg = pkg with
        {
            Operations =
            [
                pkg.Operations[0],
                new WikiPackage.Operation
                {
                    Op = WikiPackage.OpCreate,
                    Page = "Общее/Новая.md",
                    Text = "Утверждение без карточки источника",
                    Class = WikiPackage.ClassKey("session", WikiPackage.OpCreate),
                    Decision = WikiPackage.DecisionInbox,
                    BaseHashes = new Dictionary<string, string>(),
                },
            ],
        };

        var res = WikiPackageStore.Create(Root(), pkg);

        res.Ok.Should().BeFalse("create в пакете с merge обошёл бы поиск карточки источника");
        res.Reason.Should().Contain("только merge");
    }

    [Fact]
    public async Task ОткатПоИсточнику_МаркерСлияния_Отказ_СлияниеНеТронуто()
    {
        var pkg = await ProposeByNight();
        var decided = WikiPackageStore.DecideOperation(Root(), pkg.Id, 0, accept: true);
        (await _apply.ApplyDecidedAsync(User, Root(), decided.Package!)).Apply!.Value.Applied.Should().BeTrue();

        var res = await _retract.RetractAsync(User, "session:" + WikiPackage.MergeSourcePrefix + PageA);

        res.Ok.Should().BeFalse("маркер слияния — не источник: второго пути отката того же пакета нет");
        res.Code.Should().Be(WikiRetractService.CodeShape);
        res.Reason.Should().Contain("маркер слияния");
        File.Exists(PageFull(PageA)).Should().BeFalse("откат не состоялся — слияние осталось");
    }

    private sealed class FakeJudge(string answer) : ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) => Task.FromResult(answer);

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) => Task.FromResult<string?>(answer);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null, object? jsonFormat = null,
            CancellationToken ct = default) =>
            Task.FromResult(new OneShotResult(answer, new OneShotUsage(10, 0, 0, 5, 0.012, "fake-judge"), 1));

        public IReadOnlyList<LocalEngineExecutor> LocalExecutorsFor(string actionKey) => [];

        public Task<bool> IsLocalEngineReadyAsync(string engine, CancellationToken ct = default) => Task.FromResult(true);
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

    private sealed class NoSpheres : ISphereDirectory
    {
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public bool Enabled(string ownerId) => false;
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }
}
