using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// C2 (очередь 2, волна C): операция merge A → B — перенос уже проверенных строк A в те же
// секции B, алиасы A → алиасы B, файл A удаляется (прообраз + основа). Инвариант шага:
// после merge любая ссылка на A ведёт в B (WikiGate.ResolveLink по алиасу, группы WikiRelated).
//
// Страницы кладутся на диск напрямую вместе с записью манифеста (PutRaw): тесту нужны
// состояния, которых Хранитель не создаст (карточка human, «Заметки», приёмыш).
// Ожидания событий — состоянием диска, Task.Delay нет.
public class WikiMergeApplyTests : IDisposable
{
    private const string User = "u1";
    private const string Section = "common";
    private const string CardTitle = "Карточка чата";
    private const string CardPath = "Общее/Карточка-чата.md";
    private const string HumanCardTitle = "Правка человека";
    private const string HumanCardPath = "Общее/Правка-человека.md";
    private const string APath = "Общее/Сущность-А.md";
    private const string BPath = "Общее/Сущность-Б.md";
    private const string ThirdPath = "Общее/Ссылка.md";

    private const string SourceText =
        "Решение: перенести сборку на Linux. Прежний план был остаться на Windows. Контенер собран на второй машине.";
    private const string Q1 = "Прежний план был остаться на Windows";
    private const string Q2 = "Контенер собран на второй машине";
    private const string Q3 = "Решение: перенести сборку на Linux";

    // Контекст (карточка и текст источника) нужен только append-пакетам; merge применяется без него —
    // боевым путём ApplyDecidedAsync: пакет из одних merge карточку источника не ищет.
    private static readonly WikiApplyService.ApplyContext Ctx = new(CardTitle, SourceText);

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly WikiApplyService _sut;

    public WikiMergeApplyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_merge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _notes = new NotesService(projects, _config, NullLogger<NotesService>.Instance, new NoEvents(), flags: new WikiFlagOn());
        _keeper = new WikiKeeper(_notes, _config, new NoEvents());
        _sut = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config, new NoEvents());
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

    // ─── Фикстуры страниц ───

    private static string Card(string title, string origin, string refValue) => $$"""
        ---
        type: source
        origin: {{origin}}
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

    private static string Page(string title, string[] summary, string[] history, (string Card, string Quote)[] footnotes,
        string[]? aliases = null, string? notes = null)
    {
        var sb = new StringBuilder("---\ntype: topic\naliases: [")
            .Append(string.Join(", ", aliases ?? [])).Append("]\nowns: []\nsources: []\nstatus: active\n---\n\n# ")
            .Append(title).Append("\n\n## Сводка\n");
        foreach (var l in summary) sb.Append(l).Append('\n');
        sb.Append("\n## Хронология\n\n## Противоречия\n\n## История\n");
        foreach (var l in history) sb.Append(l).Append('\n');
        if (notes is not null)
            sb.Append("\n## Заметки\n").Append(notes).Append('\n');
        sb.Append('\n');
        for (var i = 0; i < footnotes.Length; i++)
            sb.Append($"[^s{i + 1}]: [[{footnotes[i].Card}]] — «{footnotes[i].Quote}»\n");
        return sb.ToString();
    }

    private static string A(string? notes = null, string cardForSecond = CardTitle) => Page("Сущность-А",
        [$"Прежний план был остаться на Windows [^s1]", "Контенер собран на второй машине [^s2]"], [],
        [(CardTitle, Q1), (cardForSecond, Q2)], notes: notes);

    private static string B() => Page("Сущность-Б",
        ["Каменный кабинет хранит план остаться на Windows [^s1]"], [], [(CardTitle, Q1)]);

    private static string Third() => Page("Ссылка",
        ["См. [[Сущность-А]] про сборку [^s1]"], [], [(CardTitle, Q3)]);

    private void SeedBase(string? aContent = null)
    {
        PutRaw(CardPath, Card(CardTitle, "session", "session:s1#m1-9"));
        PutRaw(APath, aContent ?? A());
        PutRaw(BPath, B());
    }

    // Пакет merge строит та же фабрика, что и ночь (WikiNightlyService.NewMergePackage): источник —
    // маркер пары «session:merge:{A}», карточки источника у пакета нет. Тест не обходит поиск карточки.
    private WikiPackage MergePackage(string page, string target, IReadOnlyDictionary<string, string>? baseHashes = null)
    {
        var pkg = WikiNightlyService.NewMergePackage(Section, page, target, ManifestHash(page), ManifestHash(target));
        return baseHashes is null ? pkg : pkg with { Operations = [pkg.Operations[0] with { BaseHashes = baseHashes }] };
    }

    private WikiPackage.Operation MergeOp(IReadOnlyDictionary<string, string>? baseHashes = null) =>
        MergePackage(APath, BPath, baseHashes).Operations[0];

    private static WikiPackage.Operation AppendOp(string page, string text) => new()
    {
        Op = WikiPackage.OpAppend,
        Page = page,
        Text = text,
        Class = WikiPackage.ClassKey("session", WikiPackage.OpAppend),
        Decision = WikiPackage.DecisionAuto,
        BaseHashes = new Dictionary<string, string>(),
    };

    // Для пакета из одних merge источники берутся из NewMergePackage(Section, APath, BPath,
    // "h1", "h2") с заглушечными хэшами: нужен только .Sources (маркер пары «merge:{A}»),
    // хэши-заглушки никуда не идут — базы реальных операций задаёт MergeOp.
    private WikiPackage CreateAccepted(params WikiPackage.Operation[] ops) =>
        CreateAcceptedPackage(new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = Section,
            Sources = ops.All(o => o.Op == WikiPackage.OpMerge)
                ? WikiNightlyService.NewMergePackage(Section, APath, BPath, "h1", "h2").Sources
                : [new WikiPackage.SourceRef("session", "chat-1")],
            Operations = ops,
            Status = WikiPackage.StatusChecking,
        });

    private WikiPackage CreateAcceptedPackage(WikiPackage pkg)
    {
        var created = WikiPackageStore.Create(Root, pkg);
        created.Ok.Should().BeTrue(created.Reason);
        WikiPackageStore.Transition(Root, pkg.Id, WikiPackage.StatusProposed).Ok.Should().BeTrue();
        // Ночной merge — спорная операция (inbox): применяется только по принятому решению человека.
        for (var i = 0; i < pkg.Operations.Count; i++)
            if (pkg.Operations[i].Decision == WikiPackage.DecisionInbox)
                WikiPackageStore.DecideOperation(Root, pkg.Id, i, accept: true).Ok.Should().BeTrue();
        if (WikiPackageStore.TryRead(Root, pkg.Id).Package!.Status != WikiPackage.StatusAccepted)
            WikiPackageStore.Transition(Root, pkg.Id, WikiPackage.StatusAccepted).Ok.Should().BeTrue();
        return WikiPackageStore.TryRead(Root, pkg.Id).Package!;
    }

    private string StatusOf(string id) => WikiPackageStore.TryRead(Root, id).Package!.Status;

    private List<WikiGate.Draft> AllDrafts() =>
        Directory.EnumerateFiles(WikiPaths.Pages(_dir, User), "*.md", SearchOption.AllDirectories)
            .Select(f => new WikiGate.Draft(Path.GetRelativePath(WikiPaths.Pages(_dir, User), f).Replace('\\', '/'),
                File.ReadAllText(f)))
            .Where(d => !WikiPaths.IsProjection(d.Path))
            .ToList();

    private static WikiPage Parsed(string text) => WikiPage.Parse(text).Model!;

    // ─── c1: перенос строк в те же секции ───

    [Fact]
    public async Task Merge_MovesStatementsToSameSections()
    {
        SeedBase();
        var aBytes = File.ReadAllBytes(PageFull(APath));
        var pkg = CreateAccepted(MergeOp());

        var r = await _sut.ApplyAsync(User, pkg);

        r.Applied.Should().BeTrue(r.Reason);
        File.Exists(PageFull(APath)).Should().BeFalse("файл A удалён");
        var b = Parsed(File.ReadAllText(PageFull(BPath)));
        var summary = b.Sections.Single(s => s.Name == "Сводка").Lines;
        summary.Should().HaveCount(2, "строка A с цитатой Q1, уже стоящей на B, не дублируется: +1 строка, а не +2");
        summary[1].Should().StartWith("Контенер собран на второй машине");
        // Сноски B сплошные s1..sN без дыр: свои + одна перенесённая; все ссылки строк — на существующие.
        b.Footnotes.Select(f => f.Quote).Should().Equal(Q1, Q2);
        summary[1].Should().EndWith("[^s2]");
        File.ReadAllText(PageFull(BPath)).Should().Contain("[^s1]: ").And.Contain("[^s2]: ");
        b.Frontmatter.Aliases.Should().Contain("Сущность-А", "заголовок A — алиас B: по нему резолвится ссылка");

        // Прообраз A — байты до пакета; основа в WikiBaselines — по хэшу тех же байт.
        File.ReadAllBytes(Path.Combine(WikiPaths.Keeper(_dir, User), "preimages", pkg.Id, APath)).Should().Equal(aBytes);
        WikiBaselines.TryLoad(Root, APath, Sha(aBytes)).Should().Equal(aBytes);
        WikiManifest.TryRead(Root, Section).Manifest.Should().NotContainKey(APath);
        StatusOf(pkg.Id).Should().Be(WikiPackage.StatusApplied);
    }

    [Fact]
    public async Task Merge_StatementsOfSecondSectionLandInTheSameSection()
    {
        // «История» → «История»: перенос по имени секции, а не всё в «Сводку».
        SeedBase(Page("Сущность-А", ["Контенер собран на второй машине [^s1]"],
            ["Решение: перенести сборку на Linux [^s2]"], [(CardTitle, Q2), (CardTitle, Q3)]));

        var r = await _sut.ApplyAsync(User, CreateAccepted(MergeOp()));

        r.Applied.Should().BeTrue(r.Reason);
        var b = Parsed(File.ReadAllText(PageFull(BPath)));
        b.Sections.Single(s => s.Name == "История").Lines.Should().ContainSingle().Which.Should().StartWith("Решение:");
        b.Sections.Single(s => s.Name == "Сводка").Lines.Should().HaveCount(2);
    }

    // ─── c2: ссылка на A ведёт в B ───

    [Fact]
    public async Task Merge_LinkToA_ResolvesToB_AndBLandsInRelatedGroups()
    {
        SeedBase();
        PutRaw(ThirdPath, Third());
        (await _sut.ApplyAsync(User, CreateAccepted(MergeOp()))).Applied.Should().BeTrue();

        var drafts = AllDrafts();
        var snapshot = drafts
            .Select(d => (WikiSections.Parse(d.Path)!, Parsed(d.Content), d.Path)).ToList();
        var res = WikiGate.ResolveLink("Сущность-А", Section, [], snapshot);
        res.Outcome.Should().Be(WikiGate.LinkOutcome.Resolved);
        res.Path.Should().Be(BPath);

        var third = drafts.Single(d => d.Path == ThirdPath);
        var bDraft = drafts.Single(d => d.Path == BPath);
        // Взгляд B: страница, ссылавшаяся на A, — её обратная ссылка (группа backlinks).
        WikiRelated.Build(bDraft, drafts).Single(g => g.Kind == WikiRelated.KindBacklinks)
            .Items.Select(i => i.PageKey).Should().Contain(ThirdPath);
        // Взгляд третьей страницы: B в упоминаниях быть не должен — ссылка есть и она живая.
        WikiRelated.Build(third, drafts).Where(g => g.Kind == WikiRelated.KindMentionedHere)
            .SelectMany(g => g.Items).Select(i => i.PageKey).Should().NotContain(BPath);
    }

    // Группы «Связанных» у B после merge: источник, хроника, упоминают, упомянуты здесь, обратные, по теме.
    [Fact]
    public async Task Merge_RelatedGroupsOfB_EachKindIsServed()
    {
        SeedBase();
        PutRaw(ThirdPath, Third());
        (await _sut.ApplyAsync(User, CreateAccepted(MergeOp()))).Applied.Should().BeTrue();

        var drafts = AllDrafts();
        drafts.Add(new WikiGate.Draft("Общее/Хроники/s1.md", Chronicle()));
        drafts.Add(new WikiGate.Draft("Общее/Упоминание.md",
            Page("Упоминание", ["Рядом пишут про сущность-б без ссылки [^s1]"], [], [(CardTitle, Q3)])));
        drafts.Add(new WikiGate.Draft("Общее/Контенер.md",
            Page("Контенер", ["Каменный кабинет и контенер: связка редких слов [^s1]"], [], [(CardTitle, Q3)])));
        var bDraft = drafts.Single(d => d.Path == BPath);

        var groups = WikiRelated.Build(bDraft, drafts);
        string[] Of(string kind) => groups.Where(g => g.Kind == kind).SelectMany(g => g.Items.Select(i => i.PageKey)).ToArray();

        Of(WikiRelated.KindSource).Should().Contain(CardPath);
        Of(WikiRelated.KindChronicle).Should().Contain("Общее/Хроники/s1.md");
        Of(WikiRelated.KindMentions).Should().Contain("Общее/Упоминание.md");
        Of(WikiRelated.KindMentionedHere).Should().Contain("Общее/Контенер.md",
            "перенесённая строка A стала текстом B: заголовок «Контенер» в ней — упомянут здесь");
        Of(WikiRelated.KindBacklinks).Should().Contain(ThirdPath);
        // «По теме» — оценка по редким общим основам; страница ссылается на B общими словами с A.
        var topicDrafts = AllDrafts();
        topicDrafts.Add(new WikiGate.Draft("Общее/Тема.md",
            Page("Каменный кабинет редкий", ["Каменный кабинет хранит план остаться на Windows редкий [^s1]"], [], [(CardTitle, Q1)])));
        for (var i = 0; i < 60; i++) // фон: df редких основ — малая доля снимка (как в WikiRelatedTests)
            topicDrafts.Add(new WikiGate.Draft($"Общее/Фон{i}.md", Page($"Фон{i}", [$"уникальное{i}слово{i} [^s1]"], [], [(CardTitle, Q3)])));
        var topic = WikiRelated.Build(topicDrafts.Single(d => d.Path == BPath), topicDrafts)
            .Where(g => g.Kind == WikiRelated.KindTopic).SelectMany(g => g.Items.Select(i => i.PageKey)).ToArray();
        topic.Should().Contain("Общее/Тема.md");
    }

    private static string Chronicle() => $$"""
        ---
        type: chronicle
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Хроника s1

        ## О чём
        Сборка.

        ## Что решили

        ## Что выяснили

        ## Открытые вопросы
        """;

    // ─── c3: предусловия ───

    private async Task AssertRefusedUntouched(WikiPackage pkg, string reasonPart)
    {
        var before = Snapshot();
        var r = await _sut.ApplyAsync(User, pkg);
        r.Applied.Should().BeFalse();
        r.Code.Should().Be(WikiApplyService.CodeMerge);
        r.Reason.Should().Contain(reasonPart);
        Snapshot().Should().Equal(before, "файлы A и B байт-в-байт как до пакета");
        StatusOf(pkg.Id).Should().Be(WikiPackage.StatusApplyFailed);
    }

    [Fact]
    public async Task Merge_RefusesWhenAHasHumanLine()
    {
        PutRaw(HumanCardPath, Card(HumanCardTitle, "human", "human:abc"));
        SeedBase(A(cardForSecond: HumanCardTitle));
        await AssertRefusedUntouched(CreateAccepted(MergeOp()), "строка человека");
    }

    [Fact]
    public async Task Merge_RefusesWhenAIsAdopted()
    {
        SeedBase();
        WikiManifest.AppendLog(Root, WikiPackage.ActionAdopt, Section, [APath], null, "user", null);
        await AssertRefusedUntouched(CreateAccepted(MergeOp()), "adopt");
    }

    [Fact]
    public async Task Merge_RefusesWhenAHasNotes()
    {
        SeedBase(A(notes: "Моя заметка"));
        await AssertRefusedUntouched(CreateAccepted(MergeOp()), WikiPage.NotesSectionName);
    }

    [Fact]
    public async Task Merge_RefusesWhenAnotherOpenPackageTouchesB()
    {
        SeedBase();
        CreateAccepted(AppendOp(BPath, Q3));
        await AssertRefusedUntouched(CreateAccepted(MergeOp()), "другой открытый пакет");
    }

    [Fact]
    public void Merge_AAndBInDifferentSections_RefusedByShapeBeforeApply()
    {
        SeedBase();
        var op = MergeOp() with { Target = "Проекты/p1/Сущность-Б.md" };
        var pkg = MergePackage(APath, BPath) with { Operations = [op] };
        var before = Snapshot();
        WikiPackageStore.Create(Root, pkg).Ok.Should().BeFalse("пакет — в пределах одного раздела: форма отбивает чужую B");
        Snapshot().Should().Equal(before);
    }

    [Fact]
    public void Merge_ShapeRequiresTargetAndNoText()
    {
        SeedBase();
        var bad = MergeOp() with { Target = null };
        var pkg = MergePackage(APath, BPath) with { Operations = [bad] };
        WikiPackage.ShapeErrors(pkg).Should().Contain(e => e.Contains("без цели"));
        WikiPackage.ShapeErrors(pkg with { Operations = [MergeOp() with { Text = "x" }] })
            .Should().Contain(e => e.Contains("нет текста"));
    }

    // ─── c4: гонка с append на B ───

    [Fact]
    public async Task Merge_RacesAppendOnTarget_OneSeesChangedBase()
    {
        SeedBase();
        // Разбор merge снял базу A и B ДО append; затем append лёг на B.
        var mergePkg = CreateAccepted(MergeOp());
        // Другой открытый пакет на B снимает предусловие «нет других открытых»: append применяется
        // первым, к моменту merge он уже applied — остаётся только изменившаяся база.
        var appendPkg = CreateAccepted(AppendOp(BPath, Q3));
        // Merge ждёт своей очереди: сначала append (под замком раздела), затем merge.
        (await _sut.ApplyAsync(User, appendPkg, Ctx)).Applied.Should().BeTrue();
        var bAfterAppend = File.ReadAllBytes(PageFull(BPath));
        var aBytes = File.ReadAllBytes(PageFull(APath));

        var merge = await _sut.ApplyAsync(User, mergePkg);

        merge.Applied.Should().BeFalse();
        merge.Code.Should().Be(WikiApplyService.CodeStale, "база B изменилась: пакет требует нового разбора, а не порчи");
        StatusOf(mergePkg.Id).Should().Be(WikiPackage.StatusStale);
        File.ReadAllBytes(PageFull(BPath)).Should().Equal(bAfterAppend);
        File.ReadAllBytes(PageFull(APath)).Should().Equal(aBytes);
    }

    [Fact]
    public async Task Merge_ChangeOfBBetweenPrepareAndWrite_StopsAsDrift()
    {
        SeedBase();
        var pkg = CreateAccepted(MergeOp());
        var prepared = await _sut.PrepareAsync(User, pkg, null);
        prepared.Ready.Should().BeTrue(prepared.Reason);
        // Чужая правка B мимо Хранителя в окне между проверкой и записью.
        File.AppendAllText(PageFull(BPath), "\n");
        var aBytes = File.ReadAllBytes(PageFull(APath));

        var r = await _sut.WriteAsync(User, pkg.Id, prepared, Ctx);

        r.Applied.Should().BeFalse();
        r.Code.Should().Be(WikiApplyService.CodeDrift);
        File.ReadAllBytes(PageFull(APath)).Should().Equal(aBytes, "A не удалена: сверка базы — до первой записи");
    }

    // ─── c5: форма секций при переносе (хвост R-C2, минор 1) ───

    // Пустая секция A (и секция, чьи строки целиком уже стоят на B) — не отказ и не новая секция B:
    // переносить нечего. Отказ по ИМЕНИ секции (:1061-1063) — второй рубеж за Plain()-guard
    // (:989-991) и «Заметки»-guard (:992-993), а не недостижимый код: за этими рубежами обе
    // стороны разбирает тот же парсер, который фиксирует порядок имён (WikiPage.cs:167-176)
    // и требует полного канонического набора (WikiPage.cs:198-199) — имена A и B всегда
    // совпадают, и сегодня отказ по имени не наступает; прямого теста его нет (допустимо).
    // Ниже — поведение на достаточных формах: пустая секция ничего не добавляет,
    // полностью дублирующаяся — тоже.

    [Fact]
    public async Task Merge_EmptySectionOfA_AddsNothingAndDoesNotRefuse()
    {
        // A: строки в «Сводке» и «Истории»; «Хронология» и «Противоречия» пустые. B — своей формы.
        SeedBase(Page("Сущность-А",
            ["Контенер собран на второй машине [^s1]"], ["Решение: перенести сборку на Linux [^s2]"],
            [(CardTitle, Q2), (CardTitle, Q3)]));

        var r = await _sut.ApplyAsync(User, CreateAccepted(MergeOp()));

        r.Applied.Should().BeTrue(r.Reason);
        var b = Parsed(File.ReadAllText(PageFull(BPath)));
        b.Sections.Single(s => s.Name == "Хронология").Lines.Should().BeEmpty("пустая секция A ничего не переносит");
        b.Sections.Single(s => s.Name == "Противоречия").Lines.Should().BeEmpty();
        b.Sections.Single(s => s.Name == "История").Lines.Should().ContainSingle().Which.Should().StartWith("Решение:");
        // Имена секций B — её собственные: пустая секция A не добавила в страницу ничего.
        b.Sections.Select(s => s.Name).Should().Equal("Сводка", "Хронология", "Противоречия", "История");
    }

    [Fact]
    public async Task Merge_SectionWhoseLinesAllDuplicateOnB_AddsNothing()
    {
        // «Хронология» A целиком уже стоит на B (та же пара «карточка + цитата»): секция пропускается,
        // переносится только то, чего на B нет.
        SeedBase(Page("Сущность-А",
            ["Контенер собран на второй машине [^s1]"], ["Каменный кабинет хранит план остаться на Windows [^s2]"],
            [(CardTitle, Q2), (CardTitle, Q1)]));

        var r = await _sut.ApplyAsync(User, CreateAccepted(MergeOp()));

        r.Applied.Should().BeTrue(r.Reason);
        var b = Parsed(File.ReadAllText(PageFull(BPath)));
        b.Sections.Single(s => s.Name == "Хронология").Lines.Should().BeEmpty("дублирующаяся секция ничего не добавляет");
        b.Sections.Single(s => s.Name == "Сводка").Lines.Should().HaveCount(2);
    }

    // ─── c6: цикл A→B→A, тип цели, повторное применение (хвост R-C2, минор 2) ───

    [Fact]
    public async Task Merge_CycleAfterApply_SecondPackageIsStale()
    {
        SeedBase();
        var aHash = ManifestHash(APath);
        var bHash = ManifestHash(BPath);
        var forward = CreateAccepted(MergeOp());
        (await _sut.ApplyAsync(User, forward)).Applied.Should().BeTrue();
        File.Exists(PageFull(APath)).Should().BeFalse();
        var bAfterMerge = File.ReadAllBytes(PageFull(BPath));

        // Пакет B → A после применения A → B: A больше нет на диске — база намерения исчезла.
        var back = CreateAcceptedPackage(
            WikiNightlyService.NewMergePackage(Section, BPath, APath, bHash, aHash));

        var r = await _sut.ApplyAsync(User, back);

        r.Applied.Should().BeFalse();
        r.Code.Should().Be(WikiApplyService.CodeStale);
        StatusOf(back.Id).Should().Be(WikiPackage.StatusStale, "цикл не порча: пакет требует нового разбора");
        // Текст причины общий для трёх стражей базы (:744-746, :748-749, :753-755) — тестом он
        // не различается: какой именно страж сработал здесь не проверяется, защита — в самих
        // проверках: CodeStale, статус пакета и неизменная байт B. Обе стороны цикла — база,
        // которой больше нет в том виде, на котором строился перенос (B переписана первым
        // merge, A удалена): любая из проверок базы даёт тот же исход.
        r.Reason.Should().Contain("требует нового разбора");
        File.ReadAllBytes(PageFull(BPath)).Should().Equal(bAfterMerge, "B не тронута повторным разбором");
    }

    // merge-операция с другой целью: база — хэши A и ЭТОЙ цели (сверка базы идёт до MergeInto,
    // WikiApplyService.cs:741-750), иначе пакет снялся бы как stale и типа цели не видно.
    private WikiPackage.Operation MergeTargetOf(string target) => MergeOp(new Dictionary<string, string>
    {
        [APath] = ManifestHash(APath),
        [target] = ManifestHash(target),
    }) with { Target = target };

    [Fact]
    public async Task Merge_TargetOfSourceOrChronicleType_Refused()
    {
        SeedBase();
        const string chronicleKey = "Общее/Хроники/s1.md";
        PutRaw(chronicleKey, Chronicle());

        await AssertRefusedUntouched(CreateAccepted(MergeTargetOf(chronicleKey)), "хроники не сливаются");

        // Цель — карточка источника: та же граница.
        await AssertRefusedUntouched(CreateAccepted(MergeTargetOf(CardPath)), "не сливаются");
    }

    [Fact]
    public async Task Merge_ReapplyOfAppliedPackage_RefusedByIdempotent()
    {
        SeedBase();
        var pkg = CreateAccepted(MergeOp());
        (await _sut.ApplyAsync(User, pkg)).Applied.Should().BeTrue();
        var after = Snapshot();

        // Тот же пакет ещё раз — по дисковому состоянию. «applied» — конечный статус:
        // не «applying» (тот отказ звучит иначе и оставил бы пакет в «applying»); второго
        // применения нет, состояние диска — ровно как после первого.
        var again = await _sut.ApplyAsync(User, WikiPackageStore.TryRead(Root, pkg.Id).Package!);

        again.Applied.Should().BeFalse();
        // Отказ терминальным гейтом (WikiApplyService.cs:312-313), не общим стражем
        // «не принят» (:314-317): для «applied» оба дают CodeStatus, поэтому стражи
        // различаются причиной — слово «конечном» стоит только в причине гейта. Снят
        // первый гейт — отказ даёт страж «не принят» с тем же кодом, и тест краснеет
        // по причине. CodeClaim был бы только при снятии обоих (заявка :333 из «applied»
        // не проходит: переход только в «reverted», WikiPackage.cs:111).
        again.Code.Should().Be(WikiApplyService.CodeStatus, "отказ по статусу, а не по базе или заявке");
        again.Reason.Should().Contain("конечном", "отказ именно гейтом о конечном статусе, не стражем «не принят»");
        // По существу: пакет остался в терминальном «applied» — не сдвинут в «applying»
        // (его применял бы другой вызов) и не переведён в «stale»/«apply_failed».
        StatusOf(pkg.Id).Should().Be(WikiPackage.StatusApplied, "applied — конечный: повтор статуса не трогает");
        Snapshot().Should().Equal(after, "повторный вызов не трогает состояние");
    }

    // ─── c7: самоссылка [[A]] на B после merge (хвост R-C2, минор 3) — фиксация ───

    // Строка самой B, ссылавшаяся на A, после merge остаётся [[A]]: она не «переносится», и
    // править её merge не вправе (на B могут стоять строки человека). Ссылка живая: A — алиас B,
    // резолв ведёт в саму B; в группы «Связанных» самоссылка не попадает (WikiRelated.cs:118).
    [Fact]
    public async Task Merge_SelfLinkOnB_RemainsAndResolvesToB()
    {
        SeedBase();
        PutRaw(ThirdPath, Third());
        PutRaw(BPath, Page("Сущность-Б",
            ["Каменный кабинет хранит план остаться на Windows [^s1]", "См. [[Сущность-А]] про прежний план [^s1]"],
            [], [(CardTitle, Q1)]));

        var r = await _sut.ApplyAsync(User, CreateAccepted(MergeOp()));

        r.Applied.Should().BeTrue(r.Reason);
        var b = Parsed(File.ReadAllText(PageFull(BPath)));
        b.OutboundLinks().Select(l => l.Text).Should().Contain("Сущность-А",
            "самоссылка осталась как была: merge её не переписывает");
        var drafts = AllDrafts();
        var res = WikiGate.ResolveLink("Сущность-А", Section, [],
            drafts.Select(d => (WikiSections.Parse(d.Path)!, Parsed(d.Content), d.Path)).ToList());
        res.Outcome.Should().Be(WikiGate.LinkOutcome.Resolved);
        res.Path.Should().Be(BPath, "по алиасу A ссылка ведёт в саму B");
        // Группы, которые строятся по ссылкам (упоминают / упомянуты здесь / обратные), саму B не
        // показывают: самоссылка отсечена в резолве (WikiRelated.cs:118) — видимого дефекта нет.
        WikiRelated.Build(drafts.Single(d => d.Path == BPath), drafts)
            .Where(g => g.Kind is WikiRelated.KindMentions or WikiRelated.KindMentionedHere or WikiRelated.KindBacklinks)
            .SelectMany(g => g.Items).Select(i => i.PageKey).Should().NotContain(BPath);
    }
}
