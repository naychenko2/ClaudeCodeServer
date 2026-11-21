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
        Func<string, string, IReadOnlySet<string>?, CancellationToken, Task<WikiJudgeSweep.JudgeSweepResult>>? judge = null) =>
        new(_sessions, new WikiChatRecalc(_sessions, _config), new WikiChatStage2(_llm, _sessions, _config),
            (o, c, e, ct) => Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeNothingNew, null, [])),
            _llm, _config, log ?? NullLogger<WikiNightlyService>.Instance, _flag,
            () => _now, null, _keeper, judge);

    private WikiNightlyService.NightResult Last() => WikiNightlyService.ReadLast(Root()).Should().NotBeNull().And.Subject
        as WikiNightlyService.NightResult ?? throw new InvalidOperationException();

    private string AddOpenPackage(string tag)
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
        return pkg.Id;
    }

    private List<WikiPackage> MergePackages() => WikiPackageStore.List(Root()).Packages
        .Where(p => p.Operations.Any(o => o.Op == WikiPackage.OpMerge)).ToList();

    [Fact]
    public async Task Тумблер_ПоУмолчаниюВыключен_ПакетовНет_ПолеНоль()
    {
        WriteDuplicatePair();

        await Sut().RunNightTickAsync(_now);

        MergePackages().Should().BeEmpty();
        Last().MergeProposed.Should().Be(0, "тумблер выключен");
    }

    [Fact]
    public async Task Тумблер_Включён_ПакетMerge_ПослеПолногоПрохода_ДоборТолькоПоНему()
    {
        WriteDuplicatePair();
        EnableMerge();
        var log = new RecordingLog();
        var calls = new List<(IReadOnlySet<string>? Only, int Merge)>();
        var sut = Sut(log, judge: (o, root, only, ct) =>
        {
            calls.Add((only, MergePackages().Count));
            return Task.FromResult(new WikiJudgeSweep.JudgeSweepResult(1, 0.01m, 1, 0, 0, 0, null));
        });

        await sut.RunNightTickAsync(_now);

        calls.Should().HaveCount(2, "полный проход, затем добор");
        calls[0].Only.Should().BeNull("первый проход — полный");
        calls[0].Merge.Should().Be(0, "порядок: окна → судья → merge — полный проход merge-пакетов ещё не видит");
        calls[1].Only.Should().BeEquivalentTo(new[] { MergePackages().Single().Id }, "добор — ровно по merge-пакету ночи");
        Last().JudgeCalls.Should().Be(2, "счётчики двух проходов складываются");
        Last().JudgeApplied.Should().Be(2);
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
    public async Task ДвенадцатьСтраницОдногоРаздела_ТриПакета_СтрокаПередДобором()
    {
        // 6 пар «дубль» (A — 1 утверждение, B — 2): Pick берёт ≤ 3 на раздел.
        string[] stems = ["замок вишневый", "река мутная", "поле зелёное", "город старый", "лес тёмный", "гора высокая"];
        for (var i = 0; i < stems.Length; i++)
        {
            WritePage($"Общее/П{i}-а.md", stems[i], 1);
            WritePage($"Общее/П{i}-б.md", "Большой " + stems[i], 2);
        }
        EnableMerge();
        var log = new RecordingLog();
        var sut = Sut(log, judge: (o, root, only, ct) => Task.FromResult(
            new WikiJudgeSweep.JudgeSweepResult(0, 0m, 0, 0, 0, 0, only is null ? "полный-сбой" : "добор-сбой")));

        await sut.RunNightTickAsync(_now);

        var packages = MergePackages();
        packages.Should().HaveCount(3);
        packages.Select(p => p.Operations.Single().ReasonCode).Should().AllBe(WikiApplyPolicy.CodeMergeProposal);
        Last().MergeProposed.Should().Be(3);
        var messages = log.Rows.Select(x => x.Message).ToList();
        var merge = messages.FindIndex(m => m.Contains("предложения merge — создано 3"));
        var full = messages.FindIndex(m => m.Contains("полный-сбой"));
        var catchUp = messages.FindIndex(m => m.Contains("добор-сбой"));
        merge.Should().BeGreaterThanOrEqualTo(0);
        full.Should().BeInRange(0, merge - 1, "строка полного прохода судьи раньше merge-предложений");
        catchUp.Should().BeGreaterThan(merge, "строка добора — после merge-предложений; FirstFailure пишется по каждому проходу");
    }

    [Fact]
    public async Task Гейт_СчитаетОткрытыеПослеСудьи_ХвостЧатовНеЗапираетMerge()
    {
        // Суть M1: 15 открытых до судьи, судья закрыл их — merge предлагается. До M1 гейт видел 15 и закрывался.
        WriteDuplicatePair();
        EnableMerge();
        IReadOnlySet<string>? second = null;
        var ids = new List<string>();
        for (var i = 0; i < WikiNightlyService.MergeOpenLimit; i++)
            ids.Add(AddOpenPackage("o" + i));
        var sut = Sut(judge: (o, root, only, ct) =>
        {
            if (only is null)
                foreach (var id in ids)
                    WikiPackageStore.Transition(Root(), id, WikiPackage.StatusRejected).Ok.Should().BeTrue();
            else
                second = only;
            return Task.FromResult(new WikiJudgeSweep.JudgeSweepResult(0, 0m, 0, 0, 0, 0, null));
        });

        await sut.RunNightTickAsync(_now);

        MergePackages().Should().ContainSingle();
        second.Should().BeEquivalentTo(new[] { MergePackages().Single().Id }, "добор — ровно по id созданных merge-пакетов");
        Last().MergeProposed.Should().Be(1);
    }

    [Fact]
    public async Task СуммыДвухПроходовСудьи_ВИтогеИЖурнале_ПакетыПослеДобораНеМенялись()
    {
        WriteDuplicatePair();
        EnableMerge();
        var log = new RecordingLog();
        var calls = 0;
        var openAtSecond = -1;
        var sut = Sut(log, judge: (o, root, only, ct) =>
        {
            if (++calls == 1)
                return Task.FromResult(new WikiJudgeSweep.JudgeSweepResult(2, 0.03m, 1, 1, 0, 1, null));
            openAtSecond = WikiPackageStore.List(Root()).Packages.Count;
            return Task.FromResult(new WikiJudgeSweep.JudgeSweepResult(3, 0.02m, 2, 0, 1, 2, null));
        });

        await sut.RunNightTickAsync(_now);

        var r = Last();
        r.JudgeCalls.Should().Be(5);
        r.JudgeCostUsd.Should().Be(0.05m);
        r.JudgeApplied.Should().Be(3);
        r.JudgeRejected.Should().Be(1);
        r.JudgeDeferred.Should().Be(1);
        r.CodeRejected.Should().Be(3);
        log.Rows.Select(x => x.Message).Should().Contain(m => m.Contains("на судье 3/1/1") && m.Contains("отклонено кодом 3") && m.Contains("судья 5 выз."));
        WikiPackageStore.List(Root()).Packages.Count.Should().Be(openAtSecond, "после последнего прохода судьи ночь пакетов не пишет");
    }

    [Fact]
    public async Task СудьяЛежит_ГейтЗакрыт_ДобораНет_ИтогНеПадает()
    {
        WriteDuplicatePair();
        EnableMerge();
        for (var i = 0; i < WikiNightlyService.MergeOpenLimit; i++)
            AddOpenPackage("o" + i);
        var calls = 0;
        var sut = Sut(judge: (o, root, only, ct) =>
        {
            calls++;
            return Task.FromResult(new WikiJudgeSweep.JudgeSweepResult(0, 0m, 0, 0, 0, 0, "судья недоступен"));
        });

        await sut.RunNightTickAsync(_now);

        calls.Should().Be(1, "merge 0 — добора нет");
        MergePackages().Should().BeEmpty();
        Last().MergeProposed.Should().Be(0);
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
    public async Task ЖурналНечитаем_ПредложенийНет_FailClosed()
    {
        // Мутация: убрать `is not { } adopted → return 0` в ProposeMergesForRoot — пакет создаётся.
        // Нечитаемость — эксклюзивная блокировка файла журнала (IOException на чтении; права не годятся:
        // от root они не действуют).
        WriteDuplicatePair();
        EnableMerge();
        var log = Path.Combine(Root(), "keeper", "log.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        using var hold = new FileStream(log, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        WikiApplyService.ReadAdoptedPages(Root()).Should().BeNull("предусловие: журнал нечитаем");

        await Sut().RunNightTickAsync(_now);

        MergePackages().Should().BeEmpty("журнал нечитаем — fail-closed");
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
        // Pick по Direction (A — с меньшим числом утверждений) отдаёт Каталог→Подробное и
        // Содержание→Подробное: одна B в двух парах (в других раскладках — цепочка A→B, B→C).
        // Мутация: убрать фильтр `taken` — пакетов два, и второй при применении откажет («другой открытый пакет»).
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

        created.Should().HaveCount(1, "второй пакет упёрся в лимит, добавленный ручным разбором");
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
