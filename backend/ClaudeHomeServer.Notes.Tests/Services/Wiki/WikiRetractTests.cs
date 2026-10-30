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

// S4f-1 (ADR-035, решение 12г, мера 5): откат по источнику — «Откатить всё из этого чата».
// Механизм отката одного пакета — S4b-3 (WikiRevertTests); здесь то, что над ним: выборка
// применённых пакетов источника, их откат в обратном порядке применения (цепочка разворачивается),
// снятие хроники чата целиком, пометка «не разбирать» через шов S6i и строка op=retract в журнале.
//
// Окна ставятся дисковым состоянием, а не гонкой: чужая правка кладется между применением и
// откатом явно; Task.Delay в этом файле нет. Встречная блокировка двух разделов держится гейтом-
// держателем тех же замков, что берёт операция.
public class WikiRetractTests : IDisposable
{
    private const string User = "u1";
    private const string ChatId = "chat-A";
    private const string SourceRef = "session:" + ChatId;
    private const string CardTitle = "Карточка чата";
    private const string CardPath = "Общее/Карточка-чата.md";
    private const string PagePath = "Общее/Сущность.md";
    private const string ChroniclePath = "Личное/Хроники/Чат-A.md";

    // Текст источника: по нему выводится цитата (мера 1). Утверждения тестов — его части.
    private const string ClaimA = "Контенер собран на второй машине";
    private const string ClaimB = "Решение: перенести сборку на Linux";
    private const string SourceText = ClaimB + ". Прежний план был остаться на Windows. " + ClaimA + ".";

    private static readonly WikiApplyService.ApplyContext Ctx = new(CardTitle, SourceText);

    private static readonly string CardFile = $$"""
        ---
        type: source
        origin: session
        ref: session:chat-A#m1-9
        hash: 0123456789012345678901234567890123456789012345678901234567890123
        captured: 2026-10-27
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{CardTitle}}
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

        [^s1]: [[{{CardTitle}}]] — «Прежний план был остаться на Windows»
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

    public WikiRetractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_retract_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));
        // Флаг llm-wiki включён: состояние читается моделью (BuildFresh), источник wiki в модель
        // попадает только с ним (тот же приём, что в WikiRevertTests).
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, _events, new WikiFlagOn());
        _keeper = new WikiKeeper(_notes, _config, _events);
        _apply = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config, _events);
        _revert = new WikiPackageRevert(_notes, _keeper, _config);
        _sut = new WikiRetractService(_notes, _keeper, _revert, _sessions, _config);
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

    // Шов S6i (ISessionDirectory): тест видит каждый вызов SetWikiMark и состав каталога.
    private sealed class SessionsStub : ISessionDirectory
    {
        public readonly List<(string Id, string? Mark)> MarkCalls = new();
        public readonly Dictionary<string, Session> Known = new();

        public Session? GetById(string id) => Known.TryGetValue(id, out var s) ? s : null;
        public IReadOnlyCollection<Session> GetAll() => Known.Values;
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
        public bool SetWikiMark(string sessionId, string? mark)
        {
            MarkCalls.Add((sessionId, mark));
            return Known.ContainsKey(sessionId);
        }
    }

    private string Root => WikiPaths.Root(_dir, User);
    private string PageFull(string pageKey) => Path.Combine(WikiPaths.Pages(_dir, User), pageKey);
    private string PreimageOf(string packageId, string pageKey) =>
        Path.Combine(WikiPaths.Keeper(_dir, User), "preimages", packageId, pageKey);

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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

    private async Task WriteCardAndPage()
    {
        await WriteViaKeeper(WikiSections.Common, CardPath, CardFile);
        await WriteViaKeeper(WikiSections.Common, PagePath, PageBaseFor("Сущность"));
    }

    // Снимок всего дерева pages/ — путь → sha256 байтов: «на диске не тронуто ничего» проверяется
    // по файлам, а не по признаку «ничего не написали».
    private Dictionary<string, string> SnapshotPages()
    {
        var pagesDir = WikiPaths.Pages(_dir, User);
        var snap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(pagesDir, "*", SearchOption.AllDirectories))
            snap[Path.GetRelativePath(pagesDir, f).Replace('\\', '/')] = Sha256Of(File.ReadAllBytes(f));
        return snap;
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

    // Пакет с источником sourceRef, доведённый до «accepted»: тот же ход, что в WikiRevertTests.
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

    private async Task<WikiPackage> Applied(string section, params WikiPackage.Operation[] ops) =>
        await Applied(section, SourceRef, ops);

    private async Task<WikiPackage> Applied(string section, string sourceRef, params WikiPackage.Operation[] ops)
    {
        var pkg = CreateAccepted(section, sourceRef, ops);
        var applied = await _apply.ApplyAsync(User, pkg, Ctx);
        applied.Applied.Should().BeTrue(applied.Reason);
        return WikiPackageStore.TryRead(Root, pkg.Id).Package!;
    }

    private (string Status, string? Reason) ReadStatus(string id)
    {
        var read = WikiPackageStore.TryRead(Root, id);
        read.State.Should().Be(WikiPackageFileState.Valid);
        return (read.Package!.Status, read.Package!.StatusReason);
    }

    // ─── 1. c1: пакеты чата без последующих правок откатываются побайтно, хроника снята ──────

    [Fact]
    public async Task Retract_ПакетыЧатаБезПравок_ОткатПобайтно_ХроникаСнята()
    {
        // Мутация «без отката по источнику» (вернуть пустой исход из RetractBody): страница
        // остаётся применённой, хроника — на диске: Equal(preimageBytes) и NotExist краснеют.
        await WriteCardAndPage();
        var knowledge = await Applied(WikiSections.Common, Op(WikiPackage.OpAppend, PagePath, ClaimA));
        var chronicle = await Applied(WikiSections.Personal, Op(WikiPackage.OpCreate, ChroniclePath, ClaimB));
        File.Exists(PageFull(ChroniclePath)).Should().BeTrue("применение создало страницу хроники");
        var preimageBytes = File.ReadAllBytes(PreimageOf(knowledge.Id, PagePath));

        var r = await _sut.RetractAsync(User, SourceRef);

        r.Ok.Should().BeTrue(r.Reason);
        r.Outcomes.Should().HaveCount(2);
        r.Outcomes.Should().OnlyContain(o => o.Reverted, "цепочка целая — откатывается каждый пакет источника");
        // Порядок — обратный порядку применения: хроника (применена позже) откатывается первой.
        r.Outcomes.Select(o => o.PackageId).Should().Equal(chronicle.Id, knowledge.Id);

        // Страница знания — байт в байт состояние до пакета (тот же механизм, что S4b-3).
        File.ReadAllBytes(PageFull(PagePath)).Should().Equal(preimageBytes);

        // Хроника снята целиком: её файла и его записи манифеста нет (create — пустой прообраз).
        File.Exists(PageFull(ChroniclePath)).Should().BeFalse();
        WikiManifest.TryRead(Root, WikiSections.Personal).Manifest.Should().NotContainKey(ChroniclePath);

        // Манифест согласован с диском: запись оставленной страницы — хэш её байтов.
        WikiManifest.TryRead(Root, WikiSections.Common).Manifest[PagePath]
            .Should().Be(Sha256Of(File.ReadAllBytes(PageFull(PagePath))));

        foreach (var id in new[] { knowledge.Id, chronicle.Id })
            ReadStatus(id).Item1.Should().Be(WikiPackage.StatusReverted);

        // Повтор безопасен и не двоит журнал: откатанные пакеты из выбора выпадают.
        var linesBefore = LogLines("revert").Count + LogLines("retract").Count;
        var again = await _sut.RetractAsync(User, SourceRef);
        again.Ok.Should().BeTrue();
        again.Outcomes.Should().BeEmpty();
        (LogLines("revert").Count + LogLines("retract").Count).Should().Be(linesBefore);
    }

    // ─── 2. Цепочка пакетов одной страницы разворачивается в обратном порядке применения ─────

    [Fact]
    public async Task Retract_ЦепочкуПакетовОднойСтраницы_РазворачиваетВОбратномПорядке()
    {
        // Мутация «прямой порядок применения» (OrderByDescending → OrderBy в Candidates):
        // первый откат (ранний пакет) не проходит сверку — диск уже «после позднего»: он идёт
        // в NeedsUnlink, поздний остаётся: HaveCount(2), OnlyContain и Equal(baseBytes) краснеют.
        await WriteCardAndPage();
        var baseBytes = File.ReadAllBytes(PageFull(PagePath));
        var first = await Applied(WikiSections.Common, Op(WikiPackage.OpAppend, PagePath, ClaimA));
        var second = await Applied(WikiSections.Common, Op(WikiPackage.OpAppend, PagePath, ClaimB));

        var r = await _sut.RetractAsync(User, SourceRef);

        r.Ok.Should().BeTrue(r.Reason);
        r.Outcomes.Should().HaveCount(2);
        r.Outcomes.Should().OnlyContain(o => o.Reverted,
            "обратный порядок и есть то, что разворачивает цепочку: поздний первым, сверка раннего проходит");
        r.Outcomes.Select(o => o.PackageId).Should().Equal(second.Id, first.Id);
        // Диск — состояние до всей цепочки: то, что было до первого из её пакетов.
        File.ReadAllBytes(PageFull(PagePath)).Should().Equal(baseBytes);
    }

    // ─── 3. c2: пометка «не разбирать» через шов S6i; строка op=retract со списком пакетов ──

    [Fact]
    public async Task Retract_ПослеОтката_ЧатПомеченSkip_СтрокаOpRetractСоСпискомПакетов()
    {
        // Мутация «без пометки» (TryMarkSkip вернуть false): MarkCalls краснеет. Мутация
        // «без строк retract» (снять цикл журнала): HaveCount(2) и _log.md краснеют.
        await WriteCardAndPage();
        _sessions.Known[ChatId] = new Session { Id = ChatId, OwnerId = User };
        var knowledge = await Applied(WikiSections.Common, Op(WikiPackage.OpAppend, PagePath, ClaimA));
        var chronicle = await Applied(WikiSections.Personal, Op(WikiPackage.OpCreate, ChroniclePath, ClaimB));

        var r = await _sut.RetractAsync(User, SourceRef);

        r.Ok.Should().BeTrue(r.Reason);
        r.MarkedSkip.Should().BeTrue();
        // Пометка — ровно одна, Skip, своему чату (иначе следующая ночь вернёт откатанное, 12а).
        _sessions.MarkCalls.Should().HaveCount(1);
        _sessions.MarkCalls[0].Should().Be((ChatId, (string?)WikiMarks.Skip));

        // Строка op=retract — на каждый затронутый раздел: источник, страницы, id откатанных
        // пакетов, автор — человек. Строки op=revert по пакетам (S4b-3) при этом целы.
        var lines = LogLines("retract");
        lines.Should().HaveCount(2, "по строке на раздел, где диск изменился");
        lines[0].Should().Contain("\"section\":\"common\"")
            .And.Contain(PagePath).And.Contain(knowledge.Id).And.Contain(SourceRef).And.Contain("\"actor\":\"user\"");
        lines[1].Should().Contain("\"section\":\"personal\"")
            .And.Contain(ChroniclePath).And.Contain(chronicle.Id);
        LogLines("revert").Should().HaveCount(2, "откат каждого пакета остаётся в журнале своим фактом");

        // _log.md — производная от log.jsonl: строка retract в ней (порядок: журнал до проекций).
        File.ReadAllText(PageFull("Общее/_log.md")).Should().Contain("retract");
    }

    // ─── 4. c3: встречный откат двух разделов не встаёт в взаимной блокировке (за 5 с) ───────

    [Fact]
    public async Task RetractДвухРазделовВстречный_ЗавершаетсяЗа5Секунд()
    {
        // Свойство, которое держит тест: встречный откат не держит второй раздел, пока ждёт
        // первый. Тест держатель «common»; оба встречных отката стоят на входе в него; встречная
        // попытка взять «projects/…» свободна — ни один из откатов его не держит. Свободна она и
        // при правильном порядке — откат дождёт и завершится за 5 с.
        // Мутация «блокировки в порядке аргументов» (снять OrderBy в WithSectionsLockedAsync,
        // прогон только этого теста): X берёт «projects/…» первым и стоит на «common» — встречная
        // попытка не получает замок за 2 с, WaitAsync падает TimeoutException'ом, тест красный
        // по таймауту.
        await WriteCardAndPage();
        var project = CreateServerProject("Проект X");
        var projSection = WikiSections.ProjectsPrefix + project.Id;
        var xCommon = "Общее/Сущность-X.md";
        var yCommon = "Общее/Сущность-Y.md";
        var xProj = $"Проекты/{project.Id}/Сущность-X.md";
        var yProj = $"Проекты/{project.Id}/Сущность-Y.md";
        await WriteViaKeeper(WikiSections.Common, xCommon, PageBaseFor("Сущность-X"));
        await WriteViaKeeper(WikiSections.Common, yCommon, PageBaseFor("Сущность-Y"));
        await WriteViaKeeper(projSection, xProj, PageBaseFor("Сущность-X"));
        await WriteViaKeeper(projSection, yProj, PageBaseFor("Сущность-Y"));
        var baseCommon = File.ReadAllBytes(PageFull(xCommon));
        var baseProj = File.ReadAllBytes(PageFull(yProj));

        // Встречные порядки применения: X — сначала common, потом проект → его откат приходит к
        // замкам в порядке аргументов [projects, common]; Y — наоборот → [common, projects].
        await Applied(WikiSections.Common, "session:chat-X", Op(WikiPackage.OpAppend, xCommon, ClaimA));
        await Applied(projSection, "session:chat-X", Op(WikiPackage.OpAppend, xProj, ClaimA));
        await Applied(projSection, "session:chat-Y", Op(WikiPackage.OpAppend, yProj, ClaimB));
        await Applied(WikiSections.Common, "session:chat-Y", Op(WikiPackage.OpAppend, yCommon, ClaimB));

        // Держатель «common» — тем же механизмом, что сама операция (Task.Delay в этом файле нет).
        var gateC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdingCommon = _keeper.WithSectionsLockedAsync(User, [WikiSections.Common],
            async () => { startedC.SetResult(); await gateC.Task; return true; });
        await startedC.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var rx = Task.Run(() => _sut.RetractAsync(User, "session:chat-X"));
        var ry = Task.Run(() => _sut.RetractAsync(User, "session:chat-Y"));

        // Дать обоим поискам дойти до входа в замок: это НЕ ожидание результата (результат
        // ждётся WaitAsync) — спин на процессоре, чтобы встречная попытка ниже проверяла
        // держателя, а не гонку «кто раньше успел».
        Thread.SpinWait(5_000_000);

        // Встречная попытка «projects/…»: при каноническом порядке замок свободен (оба отката
        // ждут «common», ничего не держат). При порядке аргументов его держит X — таймаут 2 с.
        var gateP = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedP = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdingProj = _keeper.WithSectionsLockedAsync(User, [projSection],
            async () => { startedP.SetResult(); await gateP.Task; return true; });
        await startedP.Task.WaitAsync(TimeSpan.FromSeconds(2));

        gateC.SetResult();
        gateP.SetResult();
        var resX = await rx.WaitAsync(TimeSpan.FromSeconds(5));
        var resY = await ry.WaitAsync(TimeSpan.FromSeconds(5));
        await holdingCommon;
        await holdingProj;

        resX.Ok.Should().BeTrue(resX.Reason);
        resY.Ok.Should().BeTrue(resY.Reason);
        resX.Outcomes.Should().HaveCount(2);
        resX.Outcomes.Should().OnlyContain(o => o.Reverted);
        resY.Outcomes.Should().HaveCount(2);
        resY.Outcomes.Should().OnlyContain(o => o.Reverted);
        // Обе страницы вернулись к состоянию до своих пакетов: встречные операции не мешали
        // друг другу и не оставили раздела в рассогласовании.
        File.ReadAllBytes(PageFull(xCommon)).Should().Equal(baseCommon);
        File.ReadAllBytes(PageFull(yProj)).Should().Equal(baseProj);
    }

    // ─── 5. c4: страницу после пакета правили — пакет цел и в списке «требует снятия сносок» ─

    [Fact]
    public async Task Retract_СтраницуПослеПакетаПравляли_ПакетЦел_ВСпискеТребуетсяСнятиеСносок()
    {
        // Мутация «откат поверх чужой правки» (в S4b-3 снять сверку applied-записи): страница
        // откаталась бы поверх правки человека — Changed/NeedsUnlink/Snapshot краснеют.
        await WriteCardAndPage();
        _sessions.Known[ChatId] = new Session { Id = ChatId, OwnerId = User };
        var pkg = await Applied(WikiSections.Common, Op(WikiPackage.OpAppend, PagePath, ClaimA));

        // Правка человека после применения, мимо Хранителя.
        var foreign = File.ReadAllText(PageFull(PagePath)) + "\nправка человека после применения.\n";
        File.WriteAllText(PageFull(PagePath), foreign, new UTF8Encoding(false));
        var before = SnapshotPages();

        var r = await _sut.RetractAsync(User, SourceRef);

        r.Ok.Should().BeTrue(r.Reason, "отказ по чужой правке — частичный исход меры 5, не отказ операции");
        var outcome = r.Outcomes.Should().ContainSingle().Which;
        outcome.Reverted.Should().BeFalse();
        outcome.Code.Should().Be(WikiPackageRevert.CodeChanged);
        outcome.ChangedPages.Should().Equal(new[] { PagePath });
        r.NeedsUnlink.Select(o => o.PackageId).Should().Equal(pkg.Id);
        // «Требует снятия сносок» — это список меры 5 для человека и S4f-2, а не откат.
        r.MarkedSkip.Should().BeFalse("с диска ничего не снято — пометки нет");
        _sessions.MarkCalls.Should().BeEmpty();

        // Пакет и его страницы — целы: на диске не тронуто ничего.
        SnapshotPages().Should().Equal(before);
        File.ReadAllText(PageFull(PagePath)).Should().Be(foreign);
        ReadStatus(pkg.Id).Item1.Should().Be(WikiPackage.StatusApplied);
        LogLines("retract").Should().BeEmpty("отката не было — строки retract нет");
    }

    // ─── 6. Хроника, правленная после своего пакета, снимается целиком (мера 5) ──────────────

    [Fact]
    public async Task Retract_ХроникуПослеПакетаПравляли_СнимаетЦеликом_ПакетВСпискеСносок()
    {
        // Мутация «без снятия хроники» (снять цикл safety-net в RetractBody): страница хроники
        // осталась бы на диске — NotExist и RemovedChronicles краснеют.
        await WriteCardAndPage();
        var pkg = await Applied(WikiSections.Personal, Op(WikiPackage.OpCreate, ChroniclePath, ClaimB));

        // Правка после применения: откат пакета по хэшах не пройдёт — он в списке сносок.
        var foreign = File.ReadAllText(PageFull(ChroniclePath)) + "\nправка человека после применения.\n";
        File.WriteAllText(PageFull(ChroniclePath), foreign, new UTF8Encoding(false));

        var r = await _sut.RetractAsync(User, SourceRef);

        r.Ok.Should().BeTrue(r.Reason);
        var outcome = r.Outcomes.Should().ContainSingle().Which;
        outcome.Reverted.Should().BeFalse();
        outcome.Code.Should().Be(WikiPackageRevert.CodeChanged);
        // Хроника — не «страница со снятием сносок»: всё её содержание — этот чат, она снимается
        // целиком: файл и его запись манифеста.
        r.RemovedChronicles.Should().Equal(ChroniclePath);
        File.Exists(PageFull(ChroniclePath)).Should().BeFalse();
        WikiManifest.TryRead(Root, WikiSections.Personal).Manifest.Should().NotContainKey(ChroniclePath);
        // Остальные страницы — целы.
        File.Exists(PageFull(PagePath)).Should().BeTrue();

        // Факт снятия — в журнале и в проекции раздела; сам пакет — в списке «снятия сносок».
        LogLines("retract").Should().ContainSingle()
            .Which.Should().Contain(ChroniclePath).And.Contain("\"section\":\"personal\"");
        File.ReadAllText(PageFull("Личное/_log.md")).Should().Contain("retract");
        ReadStatus(pkg.Id).Item1.Should().Be(WikiPackage.StatusApplied);
    }

    // ─── 7. Чужой чат: пакеты своего владельца откатываются, пометка чужому каталогу — нет ──

    [Fact]
    public async Task Retract_ИсточникЧужогоЧата_ПакетыСвоиОткатаны_ПометкиЧужомуНет()
    {
        // Мутация «без проверки владельца пометки» (снять сверку ResolveOwnerId в TryMarkSkip):
        // MarkCalls получает чужой чат — BeEmpty краснеет.
        const string foreignRef = "session:chat-F";
        _sessions.Known["chat-F"] = new Session { Id = "chat-F", OwnerId = "u2" };
        await WriteCardAndPage();
        var pkg = await Applied(WikiSections.Common, foreignRef, Op(WikiPackage.OpAppend, PagePath, ClaimA));

        var r = await _sut.RetractAsync(User, foreignRef);

        // Пакет — в своей вики (per-owner корень): он свой, и он откатывается.
        r.Ok.Should().BeTrue(r.Reason);
        r.Outcomes.Should().ContainSingle().Which.Reverted.Should().BeTrue();
        File.ReadAllBytes(PageFull(PagePath)).Should().Equal(
            File.ReadAllBytes(PreimageOf(pkg.Id, PagePath)));
        // Пометка — настройка чужого каталога чатов: её нет, и вызова шва тоже.
        r.MarkedSkip.Should().BeFalse();
        _sessions.MarkCalls.Should().BeEmpty();
    }

    // ─── 8. Недоверенный вход и пустой случай: честные исходы, диск не тронут ────────────────

    [Fact]
    public async Task Retract_НедоверенныйВходИПустойСлучай_ЧестныеИсходы_ДискНеТронут()
    {
        await WriteCardAndPage();
        var before = SnapshotPages();

        // Форма карточки обязательна: пустое, без префикса origin, с «..», с управляющими
        // символами, сверх длины — отказ с причиной, не поиск по мусору.
        foreach (var bad in new[] { "", "   ", "chat-A", "session:", "session:chat..A", "session:chat-\ta", "session:" + new string('x', 600) })
        {
            var r = await _sut.RetractAsync(User, bad);
            r.Ok.Should().BeFalse($"вход «{bad}» должен быть отказан");
            r.Code.Should().Be(WikiRetractService.CodeShape);
        }

        // Пустой случай: источник правильный, применённых пакетов с ним нет — пустой исход,
        // не отказ; пометки нет (из чата ничего не снималось).
        var empty = await _sut.RetractAsync(User, "session:chat-none");
        empty.Ok.Should().BeTrue();
        empty.Outcomes.Should().BeEmpty();
        empty.MarkedSkip.Should().BeFalse();

        // Вики не заведена — отказ по состоянию, не NRE.
        var other = Path.Combine(Path.GetTempPath(), "wiki_retract_off_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            var offCfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(other, "projects.json") })
                .Build();
            var off = new WikiRetractService(_notes, _keeper, _revert, _sessions, offCfg);
            var rOff = await off.RetractAsync("u9", SourceRef);
            rOff.Ok.Should().BeFalse();
            rOff.Code.Should().Be(WikiRetractService.CodeOff);
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }

        SnapshotPages().Should().Equal(before, "отказ — это отказ: файлов не тронуто");
    }

    // ─── Помощники ────────────────────────────────────────────────────────────────────────────

    private Project CreateServerProject(string name, string owner = User)
    {
        var root = Path.Combine(_dir, "repo", owner, name);
        Directory.CreateDirectory(root);
        return _projects.Create(name, root, owner, owner);
    }
}
