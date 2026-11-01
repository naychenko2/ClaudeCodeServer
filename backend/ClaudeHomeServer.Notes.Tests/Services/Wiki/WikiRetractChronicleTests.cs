using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;

namespace ClaudeHomeServer.Tests.Services;

// H2-1 (дефект H2 отчёта QA P1 .qa/llm-wiki-qa-p1-2026-11-01.md, хвост Глеба dc2b724a,
// ревью 72ea3763 п.2): откат по источнику обязан снять хронику чата, даже когда её написал
// разбор вне пакета (AppendChronicleAsync, S6e-2) — create-операции хроники ни в одном
// пакете нет, и поиск только по операциям до такой хроники не доходил. Вторая часть —
// строка «Продолжение чата: [[T]]» в хрониках-продолжениях: после удаления цели любая
// следующая запись раздела отвергается гейтом как ссылка на несуществующую страницу
// (целью проверки применения весь список состояния раздела, WikiApplyService.cs:711).
//
// Хроника ставится тем же методом, которым её пишет разбор (WritePageAsync — ровно тот,
// что зовёт AppendChronicleAsync): тест повторяет дисковое состояние разбора, а не всю
// цепочку с моделью. Окна — дисковое состояние, а не гонка: Task.Delay в этом файле нет.
public class WikiRetractChronicleTests : IDisposable
{
    private const string User = "u1";
    private const string ChatA = "chat-A";
    private const string ChatB = "chat-B";
    private const string ChatC = "chat-C";
    private const string SourceRefA = "session:" + ChatA;
    private const string SourceRefB = "session:" + ChatB;

    private const string CardTitleA = "Карточка чата A";
    private const string CardTitleB = "Карточка чата B";
    private const string TitleA = "Хроника чата A 2026-10-30";
    private const string TitleB = "Хроника чата B 2026-10-31";

    // Текст источника: по нему выводится цитата (мера 1). Утверждения тестов — его части.
    private const string ClaimA = "Контенер собран на второй машине";
    private const string ClaimB = "Решение: перенести сборку на Linux";
    private const string SourceTextA = ClaimB + ". Прежний план был остаться на Windows. " + ClaimA + ".";
    private const string SourceTextB = ClaimB + ". Прежний план был остаться на Windows.";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly RecordingEventLog _events = new();
    private readonly WikiApplyService _apply;
    private readonly WikiPackageRevert _revert;
    private readonly SessionsStub _sessions = new();
    private readonly WikiRetractService _sut;
    private readonly Project _project;

    public WikiRetractChronicleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_retract_chronicle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));
        // Флаг llm-wiki включён: состояние читается моделью (BuildFresh), источник wiki в модель
        // попадает только с ним (тот же приём, что в WikiRetractTests).
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, _events, new WikiFlagOn());
        _keeper = new WikiKeeper(_notes, _config, _events);
        _apply = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config, _events);
        _revert = new WikiPackageRevert(_notes, _keeper, _config);
        _sut = new WikiRetractService(_notes, _keeper, _revert, _sessions, new NoSpheres(), _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
        // Дом хроник — раздел проекта: ChroniclePath живёт только в проектном доме
        // (личный — с Ф2), поэтому фикстура строится на проекте, как живой разбор чата проекта.
        _project = CreateServerProject("Проект H2");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Section => WikiSections.ProjectsPrefix + _project.Id;
    private string Folder => WikiSections.Folder(Section)!;
    private string Root => WikiPaths.Root(_dir, User);
    private string PageFull(string pageKey) => Path.Combine(WikiPaths.Pages(_dir, User), pageKey);

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private List<string> LogLines(string op)
    {
        var p = Path.Combine(WikiPaths.Keeper(_dir, User), "log.jsonl");
        return File.Exists(p)
            ? File.ReadAllLines(p).Where(l => l.Contains($"\"op\":\"{op}\"")).ToList()
            : [];
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

    private sealed class RecordingEventLog : IProjectEventLogService
    {
        public ProjectEvent? Append(string projectId, string ownerId, string type, string actor,
            string summary, string? entityRef = null) => null;
    }

    private sealed class SessionsStub : ISessionDirectory
    {
        public readonly Dictionary<string, Session> Known = new();

        public Session? GetById(string id) => Known.TryGetValue(id, out var s) ? s : null;
        public IReadOnlyCollection<Session> GetAll() => Known.Values;
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
        public bool SetWikiMark(string sessionId, string? mark) => Known.ContainsKey(sessionId);
    }

    private async Task WriteViaKeeper(string owner, string section, string pageKey, string content)
    {
        var r = await _keeper.WritePageAsync(owner, section, pageKey, content);
        r.Status.Should().Be(WikiOpStatus.Applied, r.Reason);
    }

    private static string CardFile(string title, string ref_) => $$"""
        ---
        type: source
        origin: session
        ref: {{ref_}}
        hash: 0123456789012345678901234567890123456789012345678901234567890123
        captured: 2026-10-27
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{title}}
        """;

    private static string PageBaseFor(string title) => $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{title}}

        ## Сводка
        Прежний план был остаться на Windows [^s1]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[{{CardTitleA}}]] — «Прежний план был остаться на Windows»
        """;

    /// <summary>Хроника в каноническом рендере — ровно той формы, которую выдаёт
    /// AppendChronicleAsync (WikiPage.Render): тест держит дисковое состояние разбора,
    /// а не копирует его формат руками.</summary>
    private static string ChroniclePage(string title, string[] about, string[] found, (string Card, string Quote)[] footnotes)
    {
        var page = new WikiPage
        {
            Title = title,
            Frontmatter = new WikiFrontmatter { Type = WikiFrontmatter.TypeChronicle },
            Sections =
            [
                new() { Name = "О чём", Lines = about },
                new() { Name = "Что решили", Lines = [] },
                new() { Name = "Что выяснили", Lines = found },
                new() { Name = "Открытые вопросы", Lines = [] },
            ],
            Footnotes = [.. footnotes.Select(f => new WikiPage.WikiFootnote(new(true, f.Card), f.Quote))],
        };
        return page.Render();
    }

    private static WikiPackage.Operation Op(string op, string page, string? text) => new()
    {
        Op = op,
        Page = page,
        Text = text,
        Class = WikiPackage.ClassKey("session", op),
        Decision = WikiPackage.DecisionAuto,
        BaseHashes = new Dictionary<string, string>(),
    };

    private WikiPackage CreateAccepted(string section, string sourceRef, params WikiPackage.Operation[] ops)
    {
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = section,
            Sources = [new WikiPackage.SourceRef("session", sourceRef)],
            Operations = ops,
            Status = WikiPackage.StatusChecking,
        };
        var created = WikiPackageStore.Create(Root, pkg);
        created.Ok.Should().BeTrue(created.Reason);
        WikiPackageStore.Transition(Root, pkg.Id, WikiPackage.StatusProposed).Ok.Should().BeTrue();
        WikiPackageStore.Transition(Root, pkg.Id, WikiPackage.StatusAccepted).Ok.Should().BeTrue();
        return WikiPackageStore.TryRead(Root, pkg.Id).Package!;
    }

    private async Task<WikiPackage> Applied(string section, string sourceRef, string cardTitle, string sourceText,
        params WikiPackage.Operation[] ops)
    {
        var pkg = CreateAccepted(section, sourceRef, ops);
        var applied = await _apply.ApplyAsync(User, pkg, new WikiApplyService.ApplyContext(cardTitle, sourceText));
        applied.Applied.Should().BeTrue(applied.Reason);
        return WikiPackageStore.TryRead(Root, pkg.Id).Package!;
    }

    private (string Status, string? Reason) ReadStatus(string id)
    {
        var read = WikiPackageStore.TryRead(Root, id);
        read.State.Should().Be(WikiPackageFileState.Valid);
        return (read.Package!.Status, read.Package!.StatusReason);
    }

    private Dictionary<string, string> SnapshotPages(string owner = User)
    {
        var pagesDir = WikiPaths.Pages(_dir, owner);
        var snap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(pagesDir, "*", SearchOption.AllDirectories))
            snap[Path.GetRelativePath(pagesDir, f).Replace('\\', '/')] = Sha256Of(File.ReadAllBytes(f));
        return snap;
    }

    private Project CreateServerProject(string name, string owner = User)
    {
        var root = Path.Combine(_dir, "repo", owner, name);
        Directory.CreateDirectory(root);
        return _projects.Create(name, root, owner, owner);
    }

    // Карточка + страница знания + применённый пакет чата A: та же база, что в WikiRetractTests.
    // Возвращает путь страницы знания.
    private async Task<string> ChatAFixtureAsync()
    {
        var pagePath = Folder + "Сущность.md";
        await WriteViaKeeper(User, Section, Folder + "Карточка-A.md", CardFile(CardTitleA, "session:chat-A#m1-9"));
        await WriteViaKeeper(User, Section, pagePath, PageBaseFor("Сущность"));
        await Applied(Section, SourceRefA, CardTitleA, SourceTextA, Op(WikiPackage.OpAppend, pagePath, ClaimA));
        return pagePath;
    }

    // ─── (а) хроника написана ингестом, не пакетом: откат снимает её целиком ──────────────────

    [Fact]
    public async Task Retract_ХроникуНаписалИнгест_ОткатСнимаетЦеликом_МанифестИПроекцииЧисты()
    {
        // Мутация «убрать ChroniclePath из набора путей» (в ChroniclePathsOf оставить только
        // create-операции): хроника написана вне пакета, её create-операции ни в одном пакете
        // нет — RemovedChronicles, NotExist и строка журнала краснеют (сам дефект H2).
        await ChatAFixtureAsync();

        // Хроника — работа разбора: та же WritePageAsync, что зовёт AppendChronicleAsync,
        // путь — та же ChroniclePath. Пакета с create-операцией хроники в фикстуре нет.
        var chroniclePath = WikiChatIngestService.ChroniclePath(Section, ChatA)!;
        await WriteViaKeeper(User, Section, chroniclePath,
            ChroniclePage(TitleA, [], [ClaimA + " [^s1]"], [(CardTitleA, ClaimA)]));
        File.Exists(PageFull(chroniclePath)).Should().BeTrue("разбор написал хронику");

        var r = await _sut.RetractAsync(User, SourceRefA);

        r.Ok.Should().BeTrue(r.Reason);
        r.Failed.Should().BeEmpty();
        r.Outcomes.Should().ContainSingle().Which.Reverted.Should().BeTrue();
        r.RemovedChronicles.Should().Equal(chroniclePath);

        // Файла и его записи манифеста не остаётся; проекции пересобраны: в каталоге раздела
        // строки снятой хроники нет.
        File.Exists(PageFull(chroniclePath)).Should().BeFalse();
        var manifest = WikiManifest.TryRead(Root, Section);
        manifest.State.Should().Be(WikiManifestState.Valid);
        manifest.Manifest.Should().NotContainKey(chroniclePath);
        File.ReadAllText(PageFull(Folder + "_index.md"))
            .Should().NotContain(TitleA).And.NotContain("Хроники/" + ChatA);

        // Факт снятия — в журнале: страница хроники и источник в строке op=retract.
        LogLines("retract").Should().ContainSingle()
            .Which.Should().Contain(chroniclePath).And.Contain(SourceRefA);
        ReadStatus(r.Outcomes[0].PackageId).Item1.Should().Be(WikiPackage.StatusReverted);
    }

    // ─── (б) цепочка A→B: откат A убирает строку продолжения из хроники B, раздел жив ────────

    [Fact]
    public async Task Retract_ЦепочкуAИB_ИзХроникиBСнимаетСтрокуПродолжения_СледующаяЗаписьПроходитГейт()
    {
        // Мутация «не удалять строку продолжения» (RemoveContinuationLinks вернуть пустой
        // список): NotContain(«Продолжение чата») краснеет; и следующая запись раздела
        // отвергается гейтом («ссылка … не найдена в разделе и вверх по цепочке» — цель
        // проверки весь список раздела) — Applied краснеет вторым.
        var pagePath = await ChatAFixtureAsync();
        var chronicleA = WikiChatIngestService.ChroniclePath(Section, ChatA)!;
        await WriteViaKeeper(User, Section, chronicleA,
            ChroniclePage(TitleA, [], [ClaimA + " [^s1]"], [(CardTitleA, ClaimA)]));

        // Хроника продолжения: строка в «О чём» — ровно формат ContinuationLinkLine
        // (одна копия: ContinuationLinkPrefix).
        var chronicleB = WikiChatIngestService.ChroniclePath(Section, ChatB)!;
        await WriteViaKeeper(User, Section, chronicleB,
            ChroniclePage(TitleB, [WikiChatIngestService.ContinuationLinkPrefix + "[[" + TitleA + "]]"], [], []));

        var r = await _sut.RetractAsync(User, SourceRefA);

        r.Ok.Should().BeTrue(r.Reason);
        r.Failed.Should().BeEmpty();
        r.RemovedChronicles.Should().Equal(chronicleA);

        // Продолжение не остаётся со ссылкой на снятое: убирается ровно строка ссылки,
        // страница остаётся валидной хроникой со своим заголовком.
        var textB = File.ReadAllText(PageFull(chronicleB));
        textB.Should().NotContain("Продолжение чата", "цели ссылки больше нет — ссылка снимается с ней");
        var pageB = WikiPage.Parse(textB).Model;
        pageB.Should().NotBeNull("переписанная хроника остаётся разобранной страницей");
        pageB!.Title.Should().Be(TitleB);
        pageB.Frontmatter.Type.Should().Be(WikiFrontmatter.TypeChronicle);
        pageB.Sections.First(s => s.Name == "О чём").Lines.Should().BeEmpty();

        // Манифест следует за файлом: после переписи записи соответствуют диску (дрейфа нет).
        var manifest = WikiManifest.TryRead(Root, Section);
        manifest.Manifest[chronicleB].Should().Be(Sha256Of(File.ReadAllBytes(PageFull(chronicleB))));

        // Обе хроники — в строке журнала: снятая и переписанная.
        LogLines("retract").Should().ContainSingle()
            .Which.Should().Contain(chronicleA).And.Contain(chronicleB);

        // Раздел остаётся рабочим: следующая запись чата-продолжения проходит гейт применения
        // (цель проверки — всё состояние раздела, висящая ссылка в хронике её бы отвергла).
        await WriteViaKeeper(User, Section, Folder + "Карточка-B.md", CardFile(CardTitleB, "session:chat-B#m1-4"));
        var next = await Applied(Section, SourceRefB, CardTitleB, SourceTextB, Op(WikiPackage.OpAppend, pagePath, ClaimB));
        ReadStatus(next.Id).Item1.Should().Be(WikiPackage.StatusApplied);
    }

    // ─── (в) повтор отката ничего не двоит ───────────────────────────────────────────────────

    [Fact]
    public async Task Retract_ПовторПоСнятойХронике_НичегоНеДвоит()
    {
        // Мутация «без снятия с уже удалённого пути» (убрать File.Exists / seen): вторая
        // операция дописала бы строку журнала и RemovedChronicles — BeEmpty краснеют.
        await ChatAFixtureAsync();
        var chroniclePath = WikiChatIngestService.ChroniclePath(Section, ChatA)!;
        await WriteViaKeeper(User, Section, chroniclePath,
            ChroniclePage(TitleA, [], [ClaimA + " [^s1]"], [(CardTitleA, ClaimA)]));

        var first = await _sut.RetractAsync(User, SourceRefA);
        first.Ok.Should().BeTrue(first.Reason);
        first.RemovedChronicles.Should().Equal(chroniclePath);

        var before = SnapshotPages();
        var linesBefore = LogLines("revert").Count + LogLines("retract").Count;

        var again = await _sut.RetractAsync(User, SourceRefA);

        // Пакеты «reverted» из выбора выпадают: пустой исход, диск и журнал не тронуты,
        // хроника второй раз не «снимается», пометки нет (из чата уже ничего не снимают).
        again.Ok.Should().BeTrue(again.Reason);
        again.Outcomes.Should().BeEmpty();
        again.RemovedChronicles.Should().BeEmpty();
        again.Failed.Should().BeEmpty();
        again.MarkedSkip.Should().BeFalse();
        SnapshotPages().Should().Equal(before);
        (LogLines("revert").Count + LogLines("retract").Count).Should().Be(linesBefore);
    }

    // ─── (г) хроника другого чата, другого раздела и другого владельца не трогается ───────────

    [Fact]
    public async Task Retract_ХроникиДругогоЧатаРазделаВладельца_НеТронуты()
    {
        // Мутация «чистить весь раздел» (RemoveContinuationLinks обойти все страницы, а не
        // строки нужной хроники, или снять сверку exact-строки): чужие хроники изменились бы —
        // Equal(байтам) краснеют. Чужой владелец — другой per-owner корень: до него операция
        // не доходит ни при какой реализации; здесь это закреплено как неснимаемый факт.
        await ChatAFixtureAsync();
        var chronicleA = WikiChatIngestService.ChroniclePath(Section, ChatA)!;
        await WriteViaKeeper(User, Section, chronicleA,
            ChroniclePage(TitleA, [], [ClaimA + " [^s1]"], [(CardTitleA, ClaimA)]));

        // Свой раздел, другой чат: своя хроника без ссылки на A — не участник операции.
        var otherChat = WikiChatIngestService.ChroniclePath(Section, ChatC)!;
        await WriteViaKeeper(User, Section, otherChat, ChroniclePage("Хроника чата C 2026-11-01", [], [], []));

        // Другой раздел (другой проект): своя хроника того же id-формата.
        var project2 = CreateServerProject("Проект H2-2");
        var section2 = WikiSections.ProjectsPrefix + project2.Id;
        var foreign = WikiSections.Folder(section2)! + "Хроники/chat-D.md";
        await WriteViaKeeper(User, section2, foreign, ChroniclePage("Хроника чата D 2026-11-01", [], [], []));

        // Другой владелец: свой корень вики, тот же путь хроники — вне досягаемости per-owner
        // операции.
        const string other = "u2";
        await _keeper.EnsureInitializedAsync(other);
        var otherForeign = "Общее/Хроники/" + ChatA + ".md";
        await WriteViaKeeper(other, WikiSections.Common, otherForeign,
            ChroniclePage("Чужая хроника чата A", [], [], []));

        var otherChatBytes = File.ReadAllBytes(PageFull(otherChat));
        var foreignBytes = File.ReadAllBytes(PageFull(foreign));
        var otherOwnerPage = Path.Combine(WikiPaths.Pages(_dir, other), otherForeign);
        var otherBytes = File.ReadAllBytes(otherOwnerPage);

        var r = await _sut.RetractAsync(User, SourceRefA);

        r.Ok.Should().BeTrue(r.Reason);
        r.RemovedChronicles.Should().Equal(chronicleA);
        File.Exists(PageFull(chronicleA)).Should().BeFalse();

        File.ReadAllBytes(PageFull(otherChat)).Should().Equal(otherChatBytes,
            "хроника другого чата своего раздела без ссылки на снятую не тронута");
        File.ReadAllBytes(PageFull(foreign)).Should().Equal(foreignBytes,
            "хроника другого раздела — другая папка, другая операция её не касается");
        File.ReadAllBytes(otherOwnerPage).Should().Equal(otherBytes,
            "чужой per-owner корень для этой операции недостижим");
    }
}
