using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;

namespace ClaudeHomeServer.Tests.Services;

// S4f-2 (ADR-035, решение 12г, мера 5, второй абзац): снятие сносок источника на страницах
// пакетов, которые не удалось откатить побайтно. Утверждение с сносками на несколько источников
// остаётся с другими сносками; утверждение, потерявшее все сноски, убирается. Разбор — моделью
// (WikiPage), результат — через тот же гейт, что применение; запись — примитивами применения
// (прообраз, запись состояния применения, манифест, журнал, проекции). Механизм выбора пакетов и
// побайтного отката — S4f-1 (WikiRetractTests); здесь ровно второй абзац меры 5.
//
// Окна ставятся дисковым состоянием, а не гонкой: «менял ли кто-то страницу после» здесь делает
// более поздний применённый пакет чата B — окно честное и воспроизводимое.
public class WikiRetractFootnoteTests : IDisposable
{
    private const string User = "u1";
    private const string ChatA = "chat-A";
    private const string ChatB = "chat-B";
    private const string RefA = "session:" + ChatA;
    private const string RefB = "session:" + ChatB;
    private const string CardTitleA = "Карточка чата A";
    private const string CardTitleB = "Карточка чата B";
    private const string CardPathA = "Общее/Карточка-чата-A.md";
    private const string CardPathB = "Общее/Карточка-чата-B.md";
    private const string PagePath = "Общее/Сущность.md";

    // Утверждения — части текстов своих источников (мера 1: что применяется, дословно в своём).
    private const string ClaimAOnly = "Прежний план был остаться на Windows";
    private const string ClaimA2 = "Контенер собран на второй машине";
    private const string ClaimA3 = "Тест на границе интервала обязателен";
    private const string ClaimBoth = "Решение: перенести сборку на Linux";
    private const string ClaimBOnly = "Сборка на Linux проходит зелёной";
    private const string SourceTextA = ClaimAOnly + ". " + ClaimA2 + ". " + ClaimA3 + ". " + ClaimBoth + ".";
    private const string SourceTextB = ClaimBOnly + ". " + ClaimBoth + ".";

    private static readonly WikiApplyService.ApplyContext CtxA = new(CardTitleA, SourceTextA);
    private static readonly WikiApplyService.ApplyContext CtxB = new(CardTitleB, SourceTextB);

    private static string CardFile(string title, string sessionId) => $$"""
        ---
        type: source
        origin: session
        ref: session:{{sessionId}}#m1-9
        hash: 0123456789012345678901234567890123456789012345678901234567890123
        captured: 2026-10-30
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{title}}
        """;

    // Страница до всех пакетов: два утверждения — одно с сносками на оба источника (s1→A, s2→B),
    // второе только на A (s1). Ровно случай меры 5: «утверждения с другими источниками остаются».
    private static readonly string PageBase = $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Сущность

        ## Сводка
        {{ClaimBoth}} [^s1][^s2]
        {{ClaimAOnly}} [^s1]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[{{CardTitleA}}]] — «{{ClaimAOnly}}»
        [^s2]: [[{{CardTitleB}}]] — «{{ClaimBoth}}»
        """;

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

    public WikiRetractFootnoteTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_retract_fn_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, _events, new WikiFlagOn());
        _keeper = new WikiKeeper(_notes, _config, _events);
        _apply = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config, _events);
        _revert = new WikiPackageRevert(_notes, _keeper, _config);
        _sut = new WikiRetractService(_notes, _keeper, _revert, _sessions, new NoSpheres(), _config);
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

    private string Root => WikiPaths.Root(_dir, User);
    private string PageFull(string pageKey) => Path.Combine(WikiPaths.Pages(_dir, User), pageKey);

    private List<string> LogLines(string op)
    {
        var p = Path.Combine(WikiPaths.Keeper(_dir, User), "log.jsonl");
        return File.Exists(p)
            ? File.ReadAllLines(p).Where(l => l.Contains($"\"op\":\"{op}\"")).ToList()
            : [];
    }

    private async Task WriteViaKeeper(string section, string pageKey, string content)
    {
        var r = await _keeper.WritePageAsync(User, section, pageKey, content);
        r.Status.Should().Be(WikiOpStatus.Applied, r.Reason);
    }

    private async Task WriteCardsAndPage()
    {
        await WriteViaKeeper(WikiSections.Common, CardPathA, CardFile(CardTitleA, ChatA));
        await WriteViaKeeper(WikiSections.Common, CardPathB, CardFile(CardTitleB, ChatB));
        await WriteViaKeeper(WikiSections.Common, PagePath, PageBase);
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

    // Пакет источника sourceRef, применённый через тот же ход, что в WikiRetractTests.
    private async Task<WikiPackage> Applied(string sourceRef, WikiApplyService.ApplyContext ctx,
        params WikiPackage.Operation[] ops)
    {
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = WikiSections.Common,
            Sources = [new WikiPackage.SourceRef("session", sourceRef[(sourceRef.IndexOf(':') + 1)..])],
            Operations = ops,
            Status = WikiPackage.StatusChecking,
        };
        WikiPackageStore.Create(Root, pkg).Ok.Should().BeTrue();
        WikiPackageStore.Transition(Root, pkg.Id, WikiPackage.StatusProposed).Ok.Should().BeTrue();
        WikiPackageStore.Transition(Root, pkg.Id, WikiPackage.StatusAccepted).Ok.Should().BeTrue();
        var applied = await _apply.ApplyAsync(User, pkg, ctx);
        applied.Applied.Should().BeTrue(applied.Reason);
        return WikiPackageStore.TryRead(Root, pkg.Id).Package!;
    }

    private (string Status, string? Reason) ReadStatus(string id)
    {
        var read = WikiPackageStore.TryRead(Root, id);
        read.State.Should().Be(WikiPackageFileState.Valid);
        return (read.Package!.Status, read.Package!.StatusReason);
    }

    // Ход меры 5: пакет A применил утверждение, после него страницу изменил пакет B — побайтный
    // откат A невозможен (окно честное: диск отличается от записи состояния применения A),
    // и A обрабатывается снятием сносок.
    private async Task<(WikiPackage PkgA, WikiPackage PkgB)> ApplyAThenB()
    {
        var pkgA = await Applied(RefA, CtxA, Op(WikiPackage.OpAppend, PagePath, ClaimA2));
        var pkgB = await Applied(RefB, CtxB, Op(WikiPackage.OpAppend, PagePath, ClaimBOnly));
        return (pkgA, pkgB);
    }

    // ─── 1. c1: утверждение с двумя источниками остаётся с другой сноской; только с A — удалено ─

    [Fact]
    public async Task Retract_УтверждениеДвухИсточников_ОстаётсяСОднойСноской_ТолькоСА_Удалено()
    {
        // Мутация «удалять утверждение целиком» (в UnlinkPageUnderLock убирать строку при любом
        // снятом id, а не когда не осталось ни одного): первое утверждение пропало бы — Contain
        // краснеет. Мутация «не убирать утверждение вовсе» (снять ветку удаления строки):
        // NotContain(ClaimAOnly) и NotContain(ClaimA2) краснеют: утверждение без сносок осталось
        // бы в «Сводке» — его отверг бы и гейт (мера 1).
        await WriteCardsAndPage();
        await ApplyAThenB();

        var r = await _sut.RetractAsync(User, RefA);

        r.Ok.Should().BeTrue(r.Reason);
        var pageText = File.ReadAllText(PageFull(PagePath));

        // Осталось с другой сноской: сноска на A снята, на B — жива, номер перенумерован (s2→s1).
        pageText.Should().Contain(ClaimBoth + " [^s1]");
        // Утверждения, у которых не осталось ни одной сноски, убраны.
        pageText.Should().NotContain(ClaimAOnly).And.NotContain(ClaimA2);
        // Утверждение чата B не тронуто (перенумеровано: s4→s2).
        pageText.Should().Contain(ClaimBOnly + " [^s2]");

        // Ссылки в тексте и список сносок сходятся: страница читается моделью, обе оставшиеся
        // сноски — на карточку B.
        var model = WikiPage.Parse(pageText).Model;
        model.Should().NotBeNull("страница после снятия сносок обязана оставаться валидной");
        model!.Footnotes.Should().HaveCount(2);
        model.Footnotes.Should().OnlyContain(f => f.Target.Text == CardTitleB);
    }

    // ─── 2. c2: на странице после retract — ни одной сноски на источник A ──────────────────────

    [Fact]
    public async Task Retract_ПослеСнятияСносок_НаСтраницеНольСносокНаИсточникA()
    {
        // Мутация «снимать только определения, не ссылки в тексте» (снять переписывание строк):
        // «[^s1]» остаётся ссылкой на несуществующую сноску — Parse даёт null, HasCount(0) краснеет.
        await WriteCardsAndPage();
        await ApplyAThenB();

        var r = await _sut.RetractAsync(User, RefA);

        r.Ok.Should().BeTrue(r.Reason);
        var pageText = File.ReadAllText(PageFull(PagePath));
        pageText.Should().NotContain(CardTitleA, "карточка снятого источника не остаётся целью сноски");

        var model = WikiPage.Parse(pageText).Model;
        model.Should().NotBeNull();
        var toA = model!.Footnotes.Count(f =>
        {
            var card = WikiPage.Parse(File.ReadAllText(PageFull(CardPathA))).Model!;
            return f.Target.Text == card.Title;
        });
        toA.Should().Be(0, "мера 5: на снятой странице сносок этого источника не остаётся");
    }

    // ─── 3. Обработанный пакет — «reverted»; повтор безопасен и не двоит журнал ────────────────

    [Fact]
    public async Task Retract_ОбработанныйСнятиемПакет_Reverted_ПовторНеДвоитЖурнал()
    {
        // Мутация «без перехода статуса» (снять Transition в UnlinkPhase): ReadStatus и
        // Empty(Outcomes) повтором краснеют. Мутация «без журнала» (снять строку op=retract):
        // ContainSingle краснеет.
        await WriteCardsAndPage();
        _sessions.Known[ChatA] = new Session { Id = ChatA, OwnerId = User };
        var pkgA = await Applied(RefA, CtxA, Op(WikiPackage.OpAppend, PagePath, ClaimA2));
        await Applied(RefB, CtxB, Op(WikiPackage.OpAppend, PagePath, ClaimBOnly));

        var r = await _sut.RetractAsync(User, RefA);

        r.Ok.Should().BeTrue(r.Reason);
        r.Outcomes.Should().ContainSingle().Which.Code.Should().Be(WikiPackageRevert.CodeChanged);
        r.Unlinked.Select(u => u.PackageId).Should().Equal(pkgA.Id);
        r.Unlinked.Single().Pages.Should().Equal(PagePath);
        // Единственный переход из «applied» (мера 4): обработанный пакет закрыт, новым не является.
        ReadStatus(pkgA.Id).Item1.Should().Be(WikiPackage.StatusReverted);
        r.NeedsUnlink.Should().BeEmpty();
        r.MarkedSkip.Should().BeTrue("с диска снято — своему чату поставлена пометка");

        // Факт — в журнале: страница и id пакета; строки op=revert по другому пакету не тронуты.
        LogLines("retract").Should().ContainSingle()
            .Which.Should().Contain(PagePath).And.Contain(pkgA.Id).And.Contain(RefA);

        var before = File.ReadAllText(PageFull(PagePath));
        var linesBefore = LogLines("retract").Count;
        var again = await _sut.RetractAsync(User, RefA);
        again.Ok.Should().BeTrue();
        again.Outcomes.Should().BeEmpty("пакет уже «reverted» — из выбора он выпадает");
        again.Unlinked.Should().BeEmpty();
        File.ReadAllText(PageFull(PagePath)).Should().Be(before);
        LogLines("retract").Should().HaveCount(linesBefore, "повтор не дописывает журнал");
    }

    // ─── 4. Страницу нельзя снять валидной: она названа, пакет цел, диск не тронут ──────────────

    [Fact]
    public async Task Retract_РезультатНеПроходитГейт_СтраницаНазвана_ПакетApplied_ДискНеТронут()
    {
        // Мутация «писать и так» (снять проверку WikiGate): страница с утверждением без сноски
        // записалась бы — NotContain(«не проходит гейт») и BeEqual(before) краснеют.
        await WriteCardsAndPage();
        var pkgA = await Applied(RefA, CtxA, Op(WikiPackage.OpAppend, PagePath, ClaimA2));

        // Правка человека после применения, мимо Хранителя: строка без сноски — страница, которую
        // снятие не может оставить валидной (мера 1: утверждение обязано иметь сноску).
        var foreign = File.ReadAllText(PageFull(PagePath)) + "\nправка человека без сноски.\n";
        File.WriteAllText(PageFull(PagePath), foreign, new UTF8Encoding(false));
        var before = File.ReadAllText(PageFull(PagePath));

        var r = await _sut.RetractAsync(User, RefA);

        r.Ok.Should().BeTrue(r.Reason, "неснимаемая страница — частичный исход, не отказ операции");
        r.Failed.Should().ContainSingle()
            .Which.Should().Contain(pkgA.Id).And.Contain(PagePath).And.Contain("не проходит гейт");
        // О битой странице решает человек: правка её не трогает, пакет остаёт «applied» и видим.
        File.ReadAllText(PageFull(PagePath)).Should().Be(before);
        ReadStatus(pkgA.Id).Item1.Should().Be(WikiPackage.StatusApplied);
        r.NeedsUnlink.Select(o => o.PackageId).Should().Equal(pkgA.Id);
        LogLines("retract").Should().BeEmpty("на диске не тронуто ничего — строки журнала нет");
        r.MarkedSkip.Should().BeFalse();
    }

    // ─── 5. Два пакета одного источника: первый снимает следы, второй лишь доводится ────────────

    [Fact]
    public async Task Retract_СледовИсточникаНаСтраницахУжеНет_НичегоНеПишет_СтатусДоведён()
    {
        // Мутация «отказ при пустом drop» (снять return (false, null) при отсутствии снятых
        // сносок и считать это отказом): второй пакет не дошёл бы до «reverted» — ReadStatus и
        // BeEmpty(Pages) краснеют. Сценарий: два пакета источника A на одной странице, после
        // них страницу изменил пакет B — побайтный откат A невозможен; снятие делает поздний
        // (обратный порядок), и он снимает со страницы все сноски A — ранний уже ничего не находит.
        await WriteCardsAndPage();
        var pkgA1 = await Applied(RefA, CtxA, Op(WikiPackage.OpAppend, PagePath, ClaimA2));
        var pkgA2 = await Applied(RefA, CtxA, Op(WikiPackage.OpAppend, PagePath, ClaimA3));
        await Applied(RefB, CtxB, Op(WikiPackage.OpAppend, PagePath, ClaimBOnly));
        var linesBefore = LogLines("retract").Count;

        var r = await _sut.RetractAsync(User, RefA);

        r.Ok.Should().BeTrue(r.Reason);
        r.Outcomes.Should().HaveCount(2, "оба пакета A остались с изменёнными страницами");
        // Порядок — обратный порядку применения: обрабатывает поздний, он и снимает следы раннего.
        r.Unlinked.Select(u => u.PackageId).Should().Equal(pkgA2.Id, pkgA1.Id);
        r.Unlinked.First().Pages.Should().Equal(PagePath);
        r.Unlinked.Last().Pages.Should().BeEmpty("сносок источника на странице уже нет — ничего не писали");
        ReadStatus(pkgA1.Id).Item1.Should().Be(WikiPackage.StatusReverted);
        ReadStatus(pkgA2.Id).Item1.Should().Be(WikiPackage.StatusReverted);

        var pageText = File.ReadAllText(PageFull(PagePath));
        pageText.Should().NotContain("[^s1]: [[" + CardTitleA).And.NotContain(CardTitleA);
        pageText.Should().Contain(ClaimBoth + " [^s1]").And.Contain(ClaimBOnly + " [^s2]");

        // Строка журнала — одна, с id того, кто реально правил; пустая правка строки не требует.
        LogLines("retract").Should().HaveCount(linesBefore + 1);
        LogLines("retract").Single().Should().Contain(pkgA2.Id).And.NotContain(pkgA1.Id);
    }
}
