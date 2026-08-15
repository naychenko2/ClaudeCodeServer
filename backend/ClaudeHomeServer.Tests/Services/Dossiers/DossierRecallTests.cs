using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Dossiers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.Dossiers;

// Recall паспортов изменений (этап 2, ADR-004 §5): пассивный канал в промпт персон —
// бюджет (3 × 300, ≤1 КБ), исключение скелетов summaryFailed, изоляция per-owner/project,
// статусы active/degraded/archived с ленивым пересчётом и кешем на (HEAD, сигнатура графа),
// приоритет усечения (отказы/грабли режутся последними), поиск для dossier_lookup.
// git-вызовы и снимок CodeGraph подменяются наследником с счётчиками (protected virtual).
public class DossierRecallTests : IDisposable
{
    private readonly string _temp;
    private readonly DossierStore _store;
    private const string Owner = "ownerA";
    private const string Project = "project1";
    private const string Root = "C:/repo";   // фейк не ходит на диск (кроме CountLines — не задаём файлов)

    public DossierRecallTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "dossier_recall_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_temp, "projects.json"),
        }).Build();
        _store = new DossierStore(config, null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best-effort */ }
    }

    private static ChangeDossier New(string sha, string file = "src/Program.cs",
        DateTimeOffset? committedAt = null, string? owner = Owner, string? projectId = Project) => new()
    {
        OwnerId = owner!,
        ProjectId = projectId!,
        CommitSha = sha,
        CommitSubject = "subj " + sha,
        CommittedAt = committedAt ?? DateTimeOffset.UtcNow,
        Why = "зачем " + sha,
        Decisions = ["решили " + sha],
        Files = [file],
    };

    private static DossierRecallRequest Request(string file = "src/Program.cs", string text = "") =>
        new(Project, Root, null, [file], text);

    // Наследник с подменёнными git/графом; счётчики — для теста кеша статусов
    private sealed class FakeRecall : DossierRecallService
    {
        public int GitLogCalls;
        private readonly string? _head;
        private readonly string? _logOutput;
        private readonly IReadOnlySet<string>? _fqns;

        public FakeRecall(DossierStore store, string? head = null, string? logOutput = null,
            IReadOnlySet<string>? fqns = null) : base(store)
        {
            _head = head;
            _logOutput = logOutput;
            _fqns = fqns;
        }

        protected override Task<string?> ResolveHeadAsync(string ownerId, string root) =>
            Task.FromResult(_head);

        protected override Task<string?> GitLogNumstatAsync(
            string ownerId, string root, string sha, IReadOnlyList<string> files)
        {
            GitLogCalls++;
            return Task.FromResult(_logOutput);
        }

        protected override Task<IReadOnlySet<string>?> LoadAliveFqnsAsync(string root) =>
            Task.FromResult<IReadOnlySet<string>?>(_fqns);

        protected override int CountLines(string root, string relFile) => 1000;
    }

    // Без кеша и без git (head=null): ход должен работать на сохранённых статусах — деградация
    private static readonly DateTimeOffset Recent = DateTimeOffset.UtcNow;

    [Fact]
    public async Task RecallПоЯкорю_ВозвращаетПаспорт()
    {
        _store.Add(New("aaaa1111"));

        var result = await new FakeRecall(_store).BuildRecallBlockAsync(Owner, Request());

        result.Text.Should().NotBeNull("якорь хода совпал с файлом паспорта");
        result.Used.Should().ContainSingle(d => d.CommitSha == "aaaa1111");
        result.Text.Should().Contain("dossier_get(");
    }

    [Fact]
    public async Task Бюджет_ДесятьПодходящих_НеБольшеТрёхИ1КБ()
    {
        for (var i = 0; i < 10; i++)
            _store.Add(New("sha" + i, committedAt: Recent.AddMinutes(-i)));

        var result = await new FakeRecall(_store).BuildRecallBlockAsync(Owner, Request());

        result.Used.Should().HaveCount(3, "бюджет пассивного канала — до 3 паспортов (ADR-004 §5)");
        result.Text!.Length.Should().BeLessOrEqualTo(DossierRecallService.BlockCharLimit,
            "суммарный бюджет секции — 1 КБ");
        // Самые свежие поверх (ранжирование по score: якорь × свежесть)
        result.Used.Should().OnlyContain(d => d.CommitSha == "sha0" || d.CommitSha == "sha1" || d.CommitSha == "sha2");
    }

    [Fact]
    public async Task КаждаяЗапись_НеДлиннееТрёхсотСимволов()
    {
        var d = New("aaaa1111");
        d.Why = new string('з', 400);
        d.Rejected = [new string('о', 150), new string('т', 150)];
        _store.Add(d);

        var result = await new FakeRecall(_store).BuildRecallBlockAsync(Owner, Request());

        result.Used.Should().ContainSingle();
        var line = result.Text!.Split('\n').First(l => l.StartsWith("- "));
        line.Length.Should().BeLessOrEqualTo(DossierRecallService.EntryCharLimit);
    }

    [Fact]
    public async Task SummaryFailed_НеПопадаетВПассивныйКанал()
    {
        var skeleton = New("aaaa1111");
        skeleton.SummaryFailed = true;
        _store.Add(skeleton);

        var result = await new FakeRecall(_store).BuildRecallBlockAsync(Owner, Request());

        result.Text.Should().BeNull("скелет без выжимки в recall не участвует");
        result.Used.Should().BeEmpty();
    }

    [Fact]
    public async Task Изоляция_ЧужойВладелецИПроект_НеПросачиваются()
    {
        _store.Add(New("mine1"));
        _store.Add(New("otherOwner", owner: "ownerB"));
        _store.Add(New("otherProject", projectId: "project2"));

        var result = await new FakeRecall(_store).BuildRecallBlockAsync(Owner, Request());

        result.Used.Should().ContainSingle(d => d.CommitSha == "mine1",
            "паспорта чужого владельца и чужого проекта в recall не видны (guard этапа 1 — стор ключуется owner:project)");
    }

    [Fact]
    public async Task СимволУдалёнИзГрафа_Archived_ВПассивныйКаналНеПопадает()
    {
        var d = New("aaaa1111");
        d.Symbols = ["ClaudeHomeServer.Services.Gone"];
        _store.Add(d);
        var recall = new FakeRecall(_store, head: "head1", fqns: new HashSet<string> { "Other.Type" });

        await recall.RefreshStatusesAsync(Owner, Project, Root, _store.List(Owner, Project));

        d.Status.Should().Be(DossierStatus.Archived, "узла с таким FQN в снимке графа нет");
        var result = await recall.BuildRecallBlockAsync(Owner, Request());
        result.Used.Should().BeEmpty("archived — множитель 0, в пассивный канал не идёт (доступен через dossier_lookup)");
    }

    [Fact]
    public async Task ФайлСущественноПереписан_Degraded_СПометкойИПоловиннымВесом()
    {
        // 10 коммитов, 900 изменённых строк из 1000 (90% > 30%) — переписан
        var log = string.Join("\n", Enumerable.Range(0, 10).Select(_ => new string('a', 40)))
                  + "\n400\t500\tsrc/Program.cs";
        var d = New("aaaa1111");
        _store.Add(d);
        var recall = new FakeRecall(_store, head: "head1", logOutput: log);

        await recall.RefreshStatusesAsync(Owner, Project, Root, _store.List(Owner, Project));

        d.Status.Should().Be(DossierStatus.Degraded);
        var result = await recall.BuildRecallBlockAsync(Owner, Request());
        result.Text.Should().Contain("[код с тех пор менялся]", "пометка устаревания — в промпте");
    }

    [Fact]
    public async Task КешСтатусов_ПовторныйRecall_БезПовторныхGitВызовов()
    {
        var d = New("aaaa1111");
        _store.Add(d);
        var recall = new FakeRecall(_store, head: "head1", logOutput: new string('a', 40) + "\n1\t1\tsrc/Program.cs");

        // Первый recall без кеша — пересчёт, кеш наполнился
        await recall.RefreshStatusesAsync(Owner, Project, Root, _store.List(Owner, Project));
        var afterRefresh = recall.GitLogCalls;
        afterRefresh.Should().BeGreaterThan(0);

        // Два recall в том же «ходу» (тот же HEAD, та же сигнатура графа) — git log не зовётся
        await recall.BuildRecallBlockAsync(Owner, Request());
        await recall.BuildRecallBlockAsync(Owner, Request());

        recall.GitLogCalls.Should().Be(afterRefresh,
            "кеш на (HEAD, сигнатура графа) обязан гасить статусные git log между ходами с тем же HEAD");
    }

    // --- Чистая логика ---

    [Fact]
    public void ClassifyFile_МалоКоммитов_Active_ДажеПриБольшомDiff()
    {
        DossierRecallService.ClassifyFile(commits: 2, changedLines: 1000, linesNow: 10)
            .Should().Be(DossierStatus.Active, "≤3 коммитов — ИЛИ-условие таблицы §5");
    }

    [Fact]
    public void ClassifyFile_МалоПроцентов_Active()
    {
        DossierRecallService.ClassifyFile(commits: 10, changedLines: 200, linesNow: 1000)
            .Should().Be(DossierStatus.Active, "≤30% строк — ИЛИ-условие таблицы §5");
    }

    [Fact]
    public void ClassifyFile_СущественнаяПереписка_Degraded()
    {
        DossierRecallService.ClassifyFile(commits: 10, changedLines: 500, linesNow: 1000)
            .Should().Be(DossierStatus.Degraded);
    }

    [Fact]
    public void ParseNumstat_СчитаетКоммитыИСтроки()
    {
        var sha = new string('f', 40);
        var output = $"{sha}\n12\t3\tsrc/A.cs\n0\t5\tsrc/B.cs\n{sha}\n7\t0\tsrc/A.cs\n\nмусорная строка";

        var (commits, changed) = DossierRecallService.ParseNumstat(output);

        commits.Should().Be(2);
        changed.Should().Be(12 + 3 + 5 + 7);
    }

    [Fact]
    public void Score_Archived_Ноль_Degraded_Половина()
    {
        var now = DateTimeOffset.UtcNow;
        var active = DossierRecallService.Score(1.0, true, DossierStatus.Active, now, now);
        var degraded = DossierRecallService.Score(1.0, true, DossierStatus.Degraded, now, now);
        var archived = DossierRecallService.Score(1.0, true, DossierStatus.Archived, now, now);

        active.Should().Be(1.0);
        degraded.Should().BeApproximately(0.5, 1e-9);
        archived.Should().Be(0.0);
    }

    [Fact]
    public void Score_СвежийВажнеСтарогоПриПрочихРавных()
    {
        var now = DateTimeOffset.UtcNow;
        var fresh = DossierRecallService.Score(0.5, false, DossierStatus.Active, now - TimeSpan.FromDays(1), now);
        var old = DossierRecallService.Score(0.5, false, DossierStatus.Active, now - TimeSpan.FromDays(365), now);
        fresh.Should().BeGreaterThan(old);
    }

    [Fact]
    public void FormatEntry_ДлинноеЗачем_РежетсяПервым_ОтказыОстаются()
    {
        var d = New("aaaa1111");
        d.Why = new string('з', 400);
        d.Rejected = ["прямой маппинг строк ломает переякорение"];
        d.Decisions = [];

        var line = DossierRecallService.FormatEntry(d);

        line.Length.Should().BeLessOrEqualTo(DossierRecallService.EntryCharLimit);
        line.Should().Contain("отвергли: прямой маппинг строк ломает переякорение",
            "отказ — уникальная ценность паспорта, его не режем");
        line.Should().NotContain(new string('з', 200),
            "пересказ «зачем» (400 символов) режется первым — целиком в бюджет не влезает");
        line.Should().Contain($"dossier_get({d.Id})");
    }

    [Fact]
    public void FormatEntry_МногоПунктов_БерутсяДваВПорядкеЦенности()
    {
        var d = New("aaaa1111");
        d.Why = "коротко";
        d.Rejected = ["отказ раз"];
        d.Pitfalls = ["грабли раз"];
        d.Decisions = ["решение раз", "решение два"];

        var line = DossierRecallService.FormatEntry(d);

        line.Should().Contain("отвергли: отказ раз").And.Contain("грабли: грабли раз");
        line.Should().NotContain("решение", "decisions — последний по ценности, в два пункта не влезли");
    }

    [Fact]
    public void ExtractPathsFromText_ПутиСРазделителемИРасширением()
    {
        var paths = DossierRecallService.ExtractPathsFromText(
            "Правлю backend/ClaudeHomeServer/Services/Foo.cs и mcp\\memory-server\\index.js:42, а ещё index.ts");

        paths.Should().Contain(new[] { "backend/ClaudeHomeServer/Services/Foo.cs", "mcp/memory-server/index.js" });
        paths.Should().NotContain("index.ts", "голое имя файла без разделителя пути — не якорь");
    }

    [Fact]
    public async Task Lookup_ПоСимволу_НаходитВключаяArchived()
    {
        var d = New("aaaa1111");
        d.Symbols = ["Ns.Type"];
        d.Status = DossierStatus.Archived;
        _store.Add(d);

        var found = await Task.FromResult(new FakeRecall(_store).Lookup(Owner, Project, symbol: "Ns.Type"));

        found.Should().ContainSingle(x => x.Id == d.Id, "archived недоступен в пассивном канале, но dossier_lookup его находит");
    }
}
