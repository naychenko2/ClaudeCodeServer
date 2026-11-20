using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// C3 (очередь 2, волна C): откаты для операции merge A → B. Два пути отката, других в очереди 2 нет:
//   1. «Откатить пакет» (POST packages/{id}/revert → WikiPackageRevert.RevertAsync);
//   2. «Откатить всё из источника» (POST packages/retract → WikiRetractService.RetractAsync).
// REST-слой — тонкая обёртка над этими двумя вызовами (ответ собирается из их результата), поэтому
// тест каждого пути идёт в сам сервис, а не в контроллер.
// Инвариант шага: ни один откат не оставляет строк merge на B и не теряет A.
//
// Страницы кладутся на диск напрямую вместе с записью манифеста (PutRaw), окна ставятся дисковым
// состоянием; Task.Delay в файле нет.
public class WikiMergeRevertTests : IDisposable
{
    private const string User = "u1";
    private const string Section = "common";
    private const string CardTitle = "Карточка чата";
    private const string CardPath = "Общее/Карточка-чата.md";
    private const string OtherCardTitle = "Другая карточка";
    private const string OtherCardPath = "Общее/Другая-карточка.md";
    private const string APath = "Общее/Сущность-А.md";
    private const string BPath = "Общее/Сущность-Б.md";
    private const string SourceRef = "session:chat-A";

    private const string ClaimS = "Контенер собран на второй машине";
    private const string ClaimOwn = "Прежний план был остаться на Windows";
    private const string ClaimB = "Решение: перенести сборку на Linux";
    private const string SourceText = ClaimB + ". " + ClaimOwn + ". " + ClaimS + ".";

    private static readonly WikiApplyService.ApplyContext Ctx = new(CardTitle, SourceText);

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly WikiApplyService _apply;
    private readonly WikiPackageRevert _revert;
    private readonly WikiRetractService _retract;

    public WikiMergeRevertTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_merge_revert_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _notes = new NotesService(projects, _config, NullLogger<NotesService>.Instance, new NoEvents(), flags: new WikiFlagOn());
        _keeper = new WikiKeeper(_notes, _config, new NoEvents());
        _apply = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config, new NoEvents());
        _revert = new WikiPackageRevert(_notes, _keeper, _config);
        _retract = new WikiRetractService(_notes, _keeper, _revert, new NoSessions(), new NoSpheres(), _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class WikiFlagOn : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
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

    private sealed class NoEvents : IProjectEventLogService
    {
        public ProjectEvent? Append(string projectId, string ownerId, string type, string actor,
            string summary, string? entityRef = null, string? actorUserId = null) => null;
    }

    private sealed class NoSessions : ISessionDirectory
    {
        public Session? GetById(string id) => null;
        public IReadOnlyCollection<Session> GetAll() => [];
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
        public bool SetWikiMark(string sessionId, string? mark) => false;
    }

    private string Root => WikiPaths.Root(_dir, User);
    private string PageFull(string key) => Path.Combine(WikiPaths.Pages(_dir, User), key);

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private void PutRaw(string key, string content)
    {
        WikiFileWriter.Write(Root, "pages/" + key, content);
        WikiManifest.RecordApplied(Root, Section,
            new Dictionary<string, string> { [key] = Sha(File.ReadAllBytes(PageFull(key))) });
    }

    private string ManifestHash(string key) => WikiManifest.TryRead(Root, Section).Manifest[key];

    private Dictionary<string, string> Snapshot()
    {
        var pagesDir = WikiPaths.Pages(_dir, User);
        return Directory.EnumerateFiles(pagesDir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(pagesDir, f).Replace('\\', '/'),
                f => Sha(File.ReadAllBytes(f)), StringComparer.Ordinal);
    }

    private List<string> LogLines(string op)
    {
        var p = Path.Combine(WikiPaths.Keeper(_dir, User), "log.jsonl");
        return File.Exists(p) ? File.ReadAllLines(p).Where(l => l.Contains($"\"op\":\"{op}\"")).ToList() : [];
    }

    private static string Card(string title, string refValue) => $$"""
        ---
        type: source
        origin: session
        ref: {{refValue}}
        hash: 0123456789012345678901234567890123456789012345678901234567890123
        captured: 2026-10-27
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{title}}
        """;

    private static string Page(string title, string[] summary, (string Card, string Quote)[] footnotes)
    {
        var sb = new StringBuilder("---\ntype: topic\naliases: []\nowns: []\nsources: []\nstatus: active\n---\n\n# ")
            .Append(title).Append("\n\n## Сводка\n");
        foreach (var l in summary) sb.Append(l).Append('\n');
        sb.Append("\n## Хронология\n\n## Противоречия\n\n## История\n\n");
        for (var i = 0; i < footnotes.Length; i++)
            sb.Append($"[^s{i + 1}]: [[{footnotes[i].Card}]] — «{footnotes[i].Quote}»\n");
        return sb.ToString();
    }

    // Источник S — карточка «Карточка чата» (session:chat-A); у A и B свои строки — из ДРУГОЙ карточки:
    // откат по S не должен их трогать, снимается только то, что держится на S.
    private void SeedBase()
    {
        PutRaw(CardPath, Card(CardTitle, "session:chat-A#m1-9"));
        PutRaw(OtherCardPath, Card(OtherCardTitle, "session:chat-B#m1-9"));
        PutRaw(APath, Page("Сущность-А", [$"{ClaimOwn} [^s1]"], [(OtherCardTitle, ClaimOwn)]));
        PutRaw(BPath, Page("Сущность-Б", ["Каменный кабинет хранит свой план [^s1]"],
            [(OtherCardTitle, "Каменный кабинет хранит свой план")]));
    }

    private static WikiPackage.Operation Op(string op, string page, string? text, string? target = null) => new()
    {
        Op = op,
        Page = page,
        Target = target,
        Text = text,
        Class = WikiPackage.ClassKey("session", op),
        Decision = WikiPackage.DecisionAuto,
        BaseHashes = new Dictionary<string, string>(),
    };

    private WikiPackage.Operation MergeOp() => Op(WikiPackage.OpMerge, APath, null, BPath) with
    {
        BaseHashes = new Dictionary<string, string> { [APath] = ManifestHash(APath), [BPath] = ManifestHash(BPath) },
    };

    private WikiPackage CreateAccepted(string sourceRef, params WikiPackage.Operation[] ops)
    {
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = Section,
            Sources = [new WikiPackage.SourceRef("session", sourceRef)],
            Operations = ops,
            Status = WikiPackage.StatusChecking,
        };
        WikiPackageStore.Create(Root, pkg).Ok.Should().BeTrue();
        WikiPackageStore.Transition(Root, pkg.Id, WikiPackage.StatusProposed).Ok.Should().BeTrue();
        WikiPackageStore.Transition(Root, pkg.Id, WikiPackage.StatusAccepted).Ok.Should().BeTrue();
        return WikiPackageStore.TryRead(Root, pkg.Id).Package!;
    }

    private async Task<WikiPackage> Applied(string sourceRef, params WikiPackage.Operation[] ops)
    {
        var pkg = CreateAccepted(sourceRef, ops);
        var r = await _apply.ApplyAsync(User, pkg, Ctx);
        r.Applied.Should().BeTrue(r.Reason);
        return WikiPackageStore.TryRead(Root, pkg.Id).Package!;
    }

    private string StatusOf(string id) => WikiPackageStore.TryRead(Root, id).Package!.Status;

    // ─── путь 1: «Откатить пакет» ───

    [Fact]
    public async Task RevertMerge_RestoresAAndBByteForByte()
    {
        SeedBase();
        var aBefore = File.ReadAllBytes(PageFull(APath));
        var bBefore = File.ReadAllBytes(PageFull(BPath));
        var manifestBefore = WikiManifest.TryRead(Root, Section).Manifest.ToDictionary(kv => kv.Key, kv => kv.Value);
        var merge = await Applied("chat-2", MergeOp());
        File.Exists(PageFull(APath)).Should().BeFalse("merge снял A");

        var r = await _revert.RevertAsync(User, merge.Id);

        r.Reverted.Should().BeTrue(r.Reason);
        r.RestoredPages.Should().BeEquivalentTo([APath, BPath]);
        File.ReadAllBytes(PageFull(APath)).Should().Equal(aBefore, "A вернулась из прообраза");
        File.ReadAllBytes(PageFull(BPath)).Should().Equal(bBefore, "B — как до merge: строк A на ней нет");
        WikiManifest.TryRead(Root, Section).Manifest.Should().Equal(manifestBefore);
        StatusOf(merge.Id).Should().Be(WikiPackage.StatusReverted);
        LogLines("revert").Should().ContainSingle();
    }

    [Fact]
    public async Task RevertMerge_RacesAppend_NoCorruption()
    {
        // Append на B успел раньше отката (оба под замком раздела — порядок задан: append применён
        // целиком, затем откат merge). B изменена после merge → откат отказан по сверке хэша B,
        // файлы не тронуты: чужая запись не стирается вместе с merge.
        SeedBase();
        var merge = await Applied("chat-2", MergeOp());
        await Applied("chat-3", Op(WikiPackage.OpAppend, BPath, ClaimB));
        var before = Snapshot();
        var manifestBefore = WikiManifest.TryRead(Root, Section).Manifest.ToDictionary(kv => kv.Key, kv => kv.Value);

        var r = await _revert.RevertAsync(User, merge.Id);

        r.Reverted.Should().BeFalse();
        r.Code.Should().Be(WikiPackageRevert.CodeChanged);
        r.ChangedPages.Should().Equal(BPath);
        Snapshot().Should().Equal(before, "на диске не тронуто ничего");
        WikiManifest.TryRead(Root, Section).Manifest.Should().Equal(manifestBefore);
        StatusOf(merge.Id).Should().Be(WikiPackage.StatusApplied);
        LogLines("revert").Should().BeEmpty();
    }

    [Fact]
    public async Task RevertMerge_ParallelWithAppend_StateStaysConsistent()
    {
        // Настоящая гонка: порядок любой, исход один из двух и без порчи.
        SeedBase();
        var aBefore = File.ReadAllBytes(PageFull(APath));
        var merge = await Applied("chat-2", MergeOp());
        var append = CreateAccepted("chat-3", Op(WikiPackage.OpAppend, BPath, ClaimB));

        var revertTask = Task.Run(() => _revert.RevertAsync(User, merge.Id));
        var appendTask = Task.Run(() => _apply.ApplyAsync(User, append, Ctx));
        var rev = await revertTask;
        await appendTask;

        // Манифест — о том, что реально лежит на диске, при любом исходе.
        foreach (var (key, hash) in WikiManifest.TryRead(Root, Section).Manifest)
            Sha(File.ReadAllBytes(PageFull(key))).Should().Be(hash, $"манифест и диск «{key}» разошлись");
        if (rev.Reverted)
        {
            File.ReadAllBytes(PageFull(APath)).Should().Equal(aBefore);
            StatusOf(merge.Id).Should().Be(WikiPackage.StatusReverted);
        }
        else
        {
            rev.Code.Should().Be(WikiPackageRevert.CodeChanged);
            File.Exists(PageFull(APath)).Should().BeFalse("откат отказан целиком: A остаётся снятой, не наполовину");
            StatusOf(merge.Id).Should().Be(WikiPackage.StatusApplied);
        }
    }

    [Fact]
    public async Task RevertMerge_Twice_SecondCallRefusedByStatus_NothingChanges()
    {
        SeedBase();
        var merge = await Applied("chat-2", MergeOp());
        (await _revert.RevertAsync(User, merge.Id)).Reverted.Should().BeTrue();
        var before = Snapshot();
        var manifestBefore = WikiManifest.TryRead(Root, Section).Manifest.ToDictionary(kv => kv.Key, kv => kv.Value);

        var again = await _revert.RevertAsync(User, merge.Id);

        again.Reverted.Should().BeFalse();
        again.Code.Should().Be(WikiPackageRevert.CodeStatus, "повторный откат отказан по статусу, а не падает");
        Snapshot().Should().Equal(before, "второй вызов файлов не трогает");
        WikiManifest.TryRead(Root, Section).Manifest.Should().Equal(manifestBefore);
        StatusOf(merge.Id).Should().Be(WikiPackage.StatusReverted);
        LogLines("revert").Should().ContainSingle("второй строки журнала нет");
    }

    [Fact]
    public async Task RevertMerge_AReCreatedManuallyAfterMerge_RefusedAsChanged_ManualEditKept()
    {
        SeedBase();
        var merge = await Applied("chat-2", MergeOp());
        // Человек завёл страницу A заново мимо Хранителя: содержимое ни прообраз, ни «файла нет».
        PutRaw(APath, Page("Сущность-А", ["Новая версия, написанная вручную"], []));
        var manual = File.ReadAllBytes(PageFull(APath));
        var before = Snapshot();
        var manifestBefore = WikiManifest.TryRead(Root, Section).Manifest.ToDictionary(kv => kv.Key, kv => kv.Value);

        var r = await _revert.RevertAsync(User, merge.Id);

        r.Reverted.Should().BeFalse();
        r.Code.Should().Be(WikiPackageRevert.CodeChanged);
        r.ChangedPages.Should().Equal(APath);
        File.ReadAllBytes(PageFull(APath)).Should().Equal(manual, "ручная правка не затёрта прообразом");
        Snapshot().Should().Equal(before, "на диске не тронуто ничего, в том числе B");
        WikiManifest.TryRead(Root, Section).Manifest.Should().Equal(manifestBefore);
        StatusOf(merge.Id).Should().Be(WikiPackage.StatusApplied);
        LogLines("revert").Should().BeEmpty();
    }

    // ─── путь 2: откат по источнику ───

    // Блокер волны C: строки источника S перенесены с A на B другим пакетом (merge, источник не S).
    // Поиск пакетов S идёт по их путям (A), а A после merge нет — строки на B остались бы навсегда.
    [Fact]
    public async Task RetractBySource_AfterMerge_RemovesMovedLinesFromB()
    {
        SeedBase();
        await Applied("chat-A", Op(WikiPackage.OpAppend, APath, ClaimS));
        var merge = await Applied("chat-2", MergeOp());
        File.ReadAllText(PageFull(BPath)).Should().Contain(ClaimS, "строка S переехала на B");

        var r = await _retract.RetractAsync(User, SourceRef);

        r.Ok.Should().BeTrue(r.Reason);
        var b = File.ReadAllText(PageFull(BPath));
        b.Should().NotContain(ClaimS, "откат по S снимает с B строки, перенесённые с A");
        b.Should().NotContain($"[[{CardTitle}]]", "сносок на карточку S на B не остаётся");
        b.Should().Contain("Каменный кабинет хранит свой план", "собственная строка B — из другого источника");
        b.Should().Contain(ClaimOwn, "строка A из другой карточки осталась на B");
        StatusOf(merge.Id).Should().Be(WikiPackage.StatusApplied, "merge — не пакет S и им не откатывается");
        LogLines("retract").Should().ContainSingle();
    }

    // Тот же блокер, когда A создана самим S: откат пакета S для A — «уже в состоянии до пакета»
    // (файла нет), и без обхода по merge строка на B осталась бы.
    [Fact]
    public async Task RetractBySource_AfterMerge_ACreatedBySource_RemovesLinesFromB()
    {
        PutRaw(CardPath, Card(CardTitle, "session:chat-A#m1-9"));
        PutRaw(BPath, Page("Сущность-Б", ["Каменный кабинет хранит свой план [^s1]"], [(CardTitle, ClaimOwn)]));
        // A создана пакетом S (create, цитата — из источника).
        await Applied("chat-A", Op(WikiPackage.OpCreate, APath, ClaimS));
        var merge = await Applied("chat-2", MergeOp());
        File.ReadAllText(PageFull(BPath)).Should().Contain(ClaimS);

        var r = await _retract.RetractAsync(User, SourceRef);

        r.Ok.Should().BeTrue(r.Reason);
        File.ReadAllText(PageFull(BPath)).Should().NotContain(ClaimS);
        StatusOf(merge.Id).Should().Be(WikiPackage.StatusApplied);
    }

    // Merge — пакет того же источника: откат по S сперва откатывает merge (A и B возвращаются),
    // затем пакет, записавший A; обход по merge уже не нужен, и цепочка целиком возвращается.
    [Fact]
    public async Task RetractBySource_MergeOfSameSource_RevertsWholeChain()
    {
        SeedBase();
        var aSeed = File.ReadAllBytes(PageFull(APath));
        var bSeed = File.ReadAllBytes(PageFull(BPath));
        var first = await Applied("chat-A", Op(WikiPackage.OpAppend, APath, ClaimS));
        var merge = await Applied("chat-A", MergeOp());

        var r = await _retract.RetractAsync(User, SourceRef);

        r.Ok.Should().BeTrue(r.Reason);
        r.Outcomes.Should().OnlyContain(o => o.Reverted);
        File.ReadAllBytes(PageFull(APath)).Should().Equal(aSeed, "A возвращена к состоянию до всех пакетов S");
        File.ReadAllBytes(PageFull(BPath)).Should().Equal(bSeed);
        StatusOf(merge.Id).Should().Be(WikiPackage.StatusReverted);
        StatusOf(first.Id).Should().Be(WikiPackage.StatusReverted);
    }

    [Fact]
    public async Task RetractBySource_Twice_SecondCallIsEmptyOutcome_NothingChanges()
    {
        SeedBase();
        await Applied("chat-A", Op(WikiPackage.OpAppend, APath, ClaimS));
        await Applied("chat-A", MergeOp());
        (await _retract.RetractAsync(User, SourceRef)).Ok.Should().BeTrue();
        var before = Snapshot();
        var manifestBefore = WikiManifest.TryRead(Root, Section).Manifest.ToDictionary(kv => kv.Key, kv => kv.Value);

        var again = await _retract.RetractAsync(User, SourceRef);

        again.Ok.Should().BeTrue(again.Reason);
        again.Outcomes.Should().BeEmpty("applied-пакетов источника больше нет");
        again.RemovedChronicles.Should().BeEmpty();
        Snapshot().Should().Equal(before);
        WikiManifest.TryRead(Root, Section).Manifest.Should().Equal(manifestBefore);
        LogLines("retract").Should().ContainSingle("второй строки журнала нет");
    }

    // Фиксация ТЕКУЩЕГО поведения (хвост R-C3 п.3), не желаемого: пакет S откатан побайтно, а снятие
    // строк S с B упало (на B сноска с неопознанной целью — след не атрибутируется). Статус S уже «reverted», и повторный откат
    // по источнику пакета не подбирает (Candidates берёт только applied) — строки S на B остаются
    // навсегда, хотя ответ первого вызова называет их в Failed. Вариант правки — пометка пакета
    // NeedsUnlink: решение Александра. Когда поведение поменяют, этот тест должен покраснеть.
    [Fact]
    public async Task RetractBySource_UnlinkOfBFailed_ReRetractDoesNotPickUpPackage_CurrentBehavior()
    {
        PutRaw(CardPath, Card(CardTitle, "session:chat-A#m1-9"));
        PutRaw(BPath, Page("Сущность-Б", ["Каменный кабинет хранит свой план [^s1]"], [(CardTitle, ClaimOwn)]));
        var s = await Applied("chat-A", Op(WikiPackage.OpCreate, APath, ClaimS));
        await Applied("chat-2", MergeOp());
        File.ReadAllText(PageFull(BPath)).Should().Contain(ClaimS);
        // На B появилась сноска на неизвестную карточку (правка мимо Хранителя): снять её нельзя.
        var bGood = File.ReadAllText(PageFull(BPath));
        var withUnknown = bGood.TrimEnd('\n') + "\n[^s9]: [[Неведомая карточка]] — «чужая цитата»\n";
        File.WriteAllText(PageFull(BPath), withUnknown);
        WikiPage.Parse(withUnknown).Model.Should().NotBeNull("подготовка: страница с лишней сноской разбирается");

        var first = await _retract.RetractAsync(User, SourceRef);

        first.Ok.Should().BeTrue(first.Reason);
        StatusOf(s.Id).Should().Be(WikiPackage.StatusReverted, "побайтный откат пакета S прошёл");
        first.Failed.Should().NotBeEmpty("снятие строк S с B названо неудавшимся");
        File.ReadAllText(PageFull(BPath)).Should().Contain(ClaimS, "строки S на B остались");

        // Человек убирает лишнюю сноску и повторяет откат по источнику.
        File.WriteAllText(PageFull(BPath), bGood);
        var second = await _retract.RetractAsync(User, SourceRef);

        second.Ok.Should().BeTrue();
        second.Outcomes.Should().BeEmpty("пакет S уже «reverted» и выборкой не подбирается");
        File.ReadAllText(PageFull(BPath)).Should().Contain(ClaimS, "повтор строки с B не снял");
    }
}
