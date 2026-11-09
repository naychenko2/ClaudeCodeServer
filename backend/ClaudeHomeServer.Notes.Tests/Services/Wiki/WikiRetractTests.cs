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
// откатом явно; Task.Delay и пауз-спинов в этом файле нет. Встречная блокировка двух разделов
// держится гейтом-держателем тех же замков, что берёт операция, а окно «операция стоит на
// замке» — сигналом хука Хранителя (SectionLockRequestedHook), не временем.
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
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, _events, flags: new WikiFlagOn());
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
            string summary, string? entityRef = null, string? actorUserId = null) => null;
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

    // ─── 4. c3: встречный откат двух разделов не встаёт в взаимной блокировке ────────────────

    // Потолок ожидания — опция класса с прежним дефолтом (5 с). Это страховка зависания теста
    // (событие не пришёл), а не способ ждать: тест ждёт события — сигнал «операция подошла к
    // замку» (хук Хранителя) и завершение откатов. Ожидание реальное: под загрузкой параллельного
    // быстрого набора (load 16) тест стоит 6,7 с (TRX 23:47 01.11), прежняя форма с паузой-спином
    // краснела под той же загрузкой (дефект af499f3f, прогон 01.11); на спокойной машине весь
    // класс стоит 1,9 с — тест вне Fast.
    private static readonly TimeSpan WaitCeiling = TimeSpan.FromSeconds(5);

    [Fact]
    [Trait("Category", "Slow")]
    public async Task RetractДвухРазделовВстречный_ЗавершаетсяЗа5Секунд()
    {
        // Свойство, которое держит тест: встречный откат не держит второй раздел, пока ждёт
        // первый. Тест держатель «common»; окно проверки ставится сигналом: тест ждёт, пока
        // ОБА встречных отката не подойдут к «common», и только тогда берёт встречную попытку
        // «projects/…» — при каноническом порядке замок свободен (оба отката стоят на «common»
        // и ничего не держат), сигнал приходит синхронно, и ни один откат «projects/…» не просил.
        // Мутация «блокировки в порядке аргументов» (снять OrderBy в WithSectionsLockedAsync,
        // прогон только этого теста): X берёт свободный «projects/…» и встаёт на «common» рядом с
        // Y — сигнал обоих приходит, но встречная попытка теста стоит за X: её сигнала нет,
        // WaitAsync падает TimeoutException'ом, тест красный по потолку; на спокойной машине — за ~5 с.
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

        // Держатель «common» — тем же механизмом, что сама операция, и берётся ДО откатов: пока
        // тест ждёт сигнала, встречные операции гарантированно стоят на тестовом «common».
        var gateC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdingCommon = _keeper.WithSectionsLockedAsync(User, [WikiSections.Common],
            async () => { await gateC.Task; return new WikiOpResult(WikiOpStatus.Applied, null); });

        // Сигнал навешен после держателя и до откатов: в списке только их просьбы (сетап и
        // держатель уже прошли). Список и хук — на экземпляре теста (свой _keeper на каждый
        // тест), общих статиков нет.
        var requested = new List<string>();
        var atCommonLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _keeper.SectionLockRequestedHook = (_, key) =>
        {
            lock (requested)
            {
                requested.Add(key);
                if (key == WikiSections.Common && requested.Count(k => k == WikiSections.Common) >= 2)
                    atCommonLock.TrySetResult();
            }
        };

        var rx = Task.Run(() => _sut.RetractAsync(User, "session:chat-X"));
        var ry = Task.Run(() => _sut.RetractAsync(User, "session:chat-Y"));

        // Ожидание события, не паузы и не опроса: оба встречных отката подошли к «common».
        // Хук после сигнала снимается: просьбы самой попытки ниже — не просьбы откатов.
        await atCommonLock.Task.WaitAsync(WaitCeiling);
        _keeper.SectionLockRequestedHook = null;

        // Встречная попытка «projects/…» — держатель теста: при каноническом порядке замок
        // свободен (оба отката стоят на «common» и ничего не держат) — сигнал приходит синхронно.
        // При порядке аргументов «projects/…» держит X — попытка стоит за ним и сигнала не даёт.
        var startedP = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateP = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdingProj = _keeper.WithSectionsLockedAsync(User, [projSection],
            async () => { startedP.SetResult(); await gateP.Task; return new WikiOpResult(WikiOpStatus.Applied, null); });
        await startedP.Task.WaitAsync(WaitCeiling);
        requested.Should().NotContain(projSection,
            "ни один встречный откат не просил второй замок, стоя на первом");

        gateC.SetResult();
        gateP.SetResult();
        var resX = await rx.WaitAsync(WaitCeiling);
        var resY = await ry.WaitAsync(WaitCeiling);
        (await holdingCommon).Status.Should().Be(WikiOpStatus.Applied);
        (await holdingProj).Status.Should().Be(WikiOpStatus.Applied);

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
            var off = new WikiRetractService(_notes, _keeper, _revert, _sessions, new NoSpheres(), offCfg);
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

    // ─── 9. Пакет без записи состояния применения: в списке — и обработан снятием сносок (S4f-2) ──

    [Fact]
    public async Task Retract_НетЗаписиСостоянияПрименения_СнимаетСноски_ПакетReverted()
    {
        // Мутации: «фильтр только по CodeChanged» (прежний NeedsUnlink) — NoApplied не доходит
        // до снятия сносок: Unlinked и строка журнала краснеют. «не писать результат» (снять
        // запись в UnlinkPageUnderLock): NotContain("[^") и статус краснеют. Сценарий: вики
        // заведена до меры S4b-3, applied-записи нет — побайтный откат fail-closed, и ровно для
        // таких пакетов мера 5 второй абзац: сноски снимаются на месте.
        await WriteCardAndPage();
        var pkg = await Applied(WikiSections.Common, Op(WikiPackage.OpAppend, PagePath, ClaimA));
        File.Exists(AppliedRecordOf(pkg.Id)).Should().BeTrue("применение записало состояние применения");

        // Пакет «до меры S4b-3»: записи состояния применения на диске нет.
        File.Delete(AppliedRecordOf(pkg.Id));
        var preimageBefore = File.ReadAllBytes(PreimageOf(pkg.Id, PagePath));
        var diskBefore = File.ReadAllBytes(PageFull(PagePath));

        var r = await _sut.RetractAsync(User, SourceRef);

        r.Ok.Should().BeTrue(r.Reason, "отказ пакета — частичный исход меры 5, не отказ операции");
        var outcome = r.Outcomes.Should().ContainSingle().Which;
        outcome.Reverted.Should().BeFalse();
        outcome.Code.Should().Be(WikiPackageRevert.CodeNoApplied);

        // Инвариант меры + S4f-2: страницы остались на диске — пакет обработан на месте:
        // сносок источника на странице нет, утверждения без сносок убраны, страница валидна.
        r.Unlinked.Select(u => u.PackageId).Should().Equal(pkg.Id);
        r.Unlinked.Single().Pages.Should().Equal(PagePath);
        var pageText = File.ReadAllText(PageFull(PagePath));
        pageText.Should().NotContain("[^").And.NotContain(CardTitle);
        WikiPage.Parse(pageText).Model.Should().NotBeNull("страница после снятия сносок остаётся валидной");

        // Пункт 1(в) доработки ревью S4f-2: обработки страницы не превращаются в записи пакета.
        // Applied-записи как не было — снятие её не создаёт: «отката нет» остаётся fail-closed и
        // после того, как сноска снята. Прообраз — по-прежнему состояние до применения пакета,
        // состояние «до снятия» — отдельным файлом (его держит те же 90 дней).
        File.Exists(AppliedRecordOf(pkg.Id)).Should().BeFalse("снятие не создаёт запись состояния применения");
        File.ReadAllBytes(PreimageOf(pkg.Id, PagePath)).Should().Equal(preimageBefore);
        File.ReadAllBytes(RetractStateOf(pkg.Id, PagePath)).Should().Equal(diskBefore,
            "состояние «до снятия сносок» — отдельным файлом");

        // Обработанный пакет — «reverted»: он не остаётся в «оставшихся» и повтором не берётся.
        r.NeedsUnlink.Should().BeEmpty();
        ReadStatus(pkg.Id).Item1.Should().Be(WikiPackage.StatusReverted);

        // Факт снятия — в журнале: страница и id пакета.
        LogLines("retract").Should().ContainSingle()
            .Which.Should().Contain(PagePath).And.Contain(pkg.Id);
        r.MarkedSkip.Should().BeFalse("чата нет в каталоге — пометки чужому нет");
    }

    // ─── 10. доработка ревью S4f-1: повторный откат по откатанному — не в списке, идемпотентен ─

    [Fact]
    public async Task Retract_ПовторныйОткатПоОткатанномуПакету_НеВСписке_ИсходИдемпотентен()
    {
        // Мутация «выборка без фильтра по статусу» (снять Status == Applied в Candidates):
        // откатанный пакет вернулся бы в Outcomes с wiki_revert_status — BeEmpty(Outcomes)
        // краснеет; при «фильтре по всем кодам» он попал бы и в NeedsUnlink — BeEmpty краснеет.
        await WriteCardAndPage();
        var pkg = await Applied(WikiSections.Common, Op(WikiPackage.OpAppend, PagePath, ClaimA));

        var first = await _sut.RetractAsync(User, SourceRef);
        first.Outcomes.Should().ContainSingle().Which.Reverted.Should().BeTrue();
        ReadStatus(pkg.Id).Item1.Should().Be(WikiPackage.StatusReverted);

        var before = SnapshotPages();
        var linesBefore = LogLines("revert").Count + LogLines("retract").Count;

        var again = await _sut.RetractAsync(User, SourceRef);

        // Пакет в «reverted»: его страницы уже сняты прежним откатом — он не в Outcomes и не в
        // списке сносок; исход пустой, журнал не двоится, диск не тронут, пометки нет.
        again.Ok.Should().BeTrue(again.Reason);
        again.Outcomes.Should().BeEmpty();
        again.NeedsUnlink.Should().BeEmpty();
        again.MarkedSkip.Should().BeFalse();
        SnapshotPages().Should().Equal(before);
        (LogLines("revert").Count + LogLines("retract").Count).Should().Be(linesBefore);
    }

    // ─── 11. Контракт NeedsUnlink: ровно отказы, при которых страницы остались на диске ──────

    [Fact]
    public void NeedsUnlink_РовноОтказыСОставшимисяСтраницами()
    {
        // Мутация «фильтр только по CodeChanged» (прежний NeedsUnlink): NoApplied/NoPreimages
        // выпадают — Equal краснеет. Мутация «фильтр по всем кодам» (снять PagesRemained):
        // CodeStatus и CodeInternal попали бы в список — Equal краснеет.
        var outcomes = new[]
        {
            new WikiRetractService.PackageOutcome("p-ok", WikiSections.Common, true, null, null, [], []),
            new WikiRetractService.PackageOutcome("p-changed", WikiSections.Common, false,
                WikiPackageRevert.CodeChanged, null, [], ["Общее/Страница.md"]),
            new WikiRetractService.PackageOutcome("p-noapplied", WikiSections.Common, false,
                WikiPackageRevert.CodeNoApplied, null, [], []),
            new WikiRetractService.PackageOutcome("p-nopreimages", WikiSections.Common, false,
                WikiPackageRevert.CodeNoPreimages, null, [], []),
            // «reverted» — страницы уже сняты прежним откатом; внутренний сбой — не «страница со
            // снятием сносок», а сигнал неисправности раздела: ни тот, ни другой не в списке.
            new WikiRetractService.PackageOutcome("p-status", WikiSections.Common, false,
                WikiPackageRevert.CodeStatus, "пакет уже откатан", [], []),
            new WikiRetractService.PackageOutcome("p-internal", WikiSections.Common, false,
                WikiPackageRevert.CodeInternal, "внутренний сбой", [], []),
        };
        var result = new WikiRetractService.RetractResult(true, null, null, outcomes, [], false);

        result.NeedsUnlink.Select(o => o.PackageId)
            .Should().Equal("p-changed", "p-noapplied", "p-nopreimages");
    }

    // ─── 12. W3-2: конкурентность, точные причины, охват карточки, AlreadyPages ────────────────

    [Fact]
    public async Task Retract_ДваОдновременныхОткатаОдногоИсточника_ЖурналНеДвоится_ОбаOk()
    {
        // Мутация «тело не перечитывает пакеты под блоком» (взять candidates из первого чтения
        // и передать в тело): второй откат повторно откатывает уже «reverted» пакет — отказ
        // wiki_revert_status и/или вторая строка журнала: Outcomes и HaveCount(1) краснеют.
        await WriteCardAndPage();
        var pkg = await Applied(WikiSections.Common, Op(WikiPackage.OpAppend, PagePath, ClaimA));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = _keeper.WithSectionsLockedAsync(User, [WikiSections.Common],
            async () => { started.SetResult(); await gate.Task; return true; });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Вызовы идут напрямую, не через Task.Run: первое чтение каждого выполняется синхронно до
        // ожидания замка, поэтому оба заведомо видят пакет и оба встают в очередь за держателем.
        var t1 = _sut.RetractAsync(User, SourceRef);
        var t2 = _sut.RetractAsync(User, SourceRef);
        t1.IsCompleted.Should().BeFalse("раздел держит гейт");
        t2.IsCompleted.Should().BeFalse("раздел держит гейт");
        gate.SetResult();
        await holding;
        var r1 = await t1.WaitAsync(TimeSpan.FromSeconds(10));
        var r2 = await t2.WaitAsync(TimeSpan.FromSeconds(10));

        r1.Ok.Should().BeTrue(r1.Reason);
        r2.Ok.Should().BeTrue(r2.Reason);
        new[] { r1, r2 }.Count(r => r.Outcomes.Count == 1 && r.Outcomes[0].Reverted).Should().Be(1,
            "пакет откатан ровно одним из двух вызовов");
        new[] { r1, r2 }.Count(r => r.Outcomes.Count == 0).Should().Be(1,
            "второй под блоком не находит применённых пакетов — исход пустой, не отказ");
        ReadStatus(pkg.Id).Item1.Should().Be(WikiPackage.StatusReverted);
        LogLines("revert").Should().HaveCount(1);
        LogLines("retract").Should().HaveCount(1);
    }

    [Fact]
    public async Task Retract_ПакетПоявилсяВНовомРазделеПослеПервогоЧтения_РазделНеПропущен_ЗамокВзят()
    {
        // Мутация «набор замков по первому чтению, тело не сверяет разделы» (убрать missing в
        // RetractBody): личный раздел откатывается без замка, пока его держит гейт — откат
        // завершается за окно ожидания, TimeoutException не случается: ThrowsAsync краснеет.
        await WriteCardAndPage();
        var knowledge = await Applied(WikiSections.Common, Op(WikiPackage.OpAppend, PagePath, ClaimA));

        var gateC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdingCommon = _keeper.WithSectionsLockedAsync(User, [WikiSections.Common],
            async () => { startedC.SetResult(); await gateC.Task; return true; });
        await startedC.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Первое чтение — синхронно до ожидания замка: набор разделов на этот момент — только common.
        var retract = _sut.RetractAsync(User, SourceRef);
        retract.IsCompleted.Should().BeFalse();

        // Пока откат ждёт common, источник получает пакет в личном разделе — набор вырос.
        var chronicle = await Applied(WikiSections.Personal, Op(WikiPackage.OpCreate, ChroniclePath, ClaimB));
        var gateP = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedP = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdingPersonal = _keeper.WithSectionsLockedAsync(User, [WikiSections.Personal],
            async () => { startedP.SetResult(); await gateP.Task; return true; });
        await startedP.Task.WaitAsync(TimeSpan.FromSeconds(10));

        gateC.SetResult();
        await holdingCommon;
        // Откат обязан взять и личный раздел, а он занят: пока гейт держит, откат не завершается
        // и ни один пакет (включая common) не откатан — тело при нехватке замка ничего не трогает.
        await Assert.ThrowsAsync<TimeoutException>(() => retract.WaitAsync(TimeSpan.FromMilliseconds(300)));
        ReadStatus(knowledge.Id).Item1.Should().Be(WikiPackage.StatusApplied);
        ReadStatus(chronicle.Id).Item1.Should().Be(WikiPackage.StatusApplied);

        gateP.SetResult();
        await holdingPersonal;
        var r = await retract.WaitAsync(TimeSpan.FromSeconds(10));

        r.Ok.Should().BeTrue(r.Reason);
        r.Outcomes.Select(o => o.PackageId).Should().BeEquivalentTo([knowledge.Id, chronicle.Id]);
        r.Outcomes.Should().OnlyContain(o => o.Reverted);
        File.Exists(PageFull(ChroniclePath)).Should().BeFalse("раздел не пропущен: хроника снята");
    }

    [Fact]
    public async Task Retract_ОтказЗаписиПосередине_ЧастичныйИсходCompleteFalse_ИсключенияНаружуНет()
    {
        // Мутация «исключение записи не ловить» (убрать try/catch вокруг WriteBytesAtomic):
        // сбой уходит в RetractUnderLock и весь исход превращается в Ok=false без Unlinked —
        // Ok и Unlinked краснеют. Сбой записи ставит каталог на место файла «состояния до снятия».
        const string page2 = "Общее/Сущность-2.md";
        await WriteCardAndPage();
        await WriteViaKeeper(WikiSections.Common, page2, PageBaseFor("Сущность-2"));
        var pkg = await Applied(WikiSections.Common,
            Op(WikiPackage.OpAppend, page2, ClaimA),
            Op(WikiPackage.OpAppend, PagePath, ClaimB));
        // Пакет «до меры S4b-3»: побайтного отката нет — путь снятия сносок.
        File.Delete(AppliedRecordOf(pkg.Id));
        Directory.CreateDirectory(RetractStateOf(pkg.Id, PagePath));
        var blockedBefore = File.ReadAllBytes(PageFull(PagePath));

        var r = await _sut.RetractAsync(User, SourceRef);

        r.Ok.Should().BeTrue(r.Reason, "отказ записи — частичный исход, не отказ операции");
        var u = r.Unlinked.Should().ContainSingle().Which;
        u.Complete.Should().BeFalse("одна страница не обработана");
        u.Pages.Should().Equal([page2], "фактически переписана только вторая страница");
        r.Failed.Should().ContainSingle().Which.Should().Contain(pkg.Id).And.Contain(PagePath)
            .And.Contain("запись страницы не удалась");
        File.ReadAllBytes(PageFull(PagePath)).Should().Equal(blockedBefore, "необработанная страница не тронута");
        ReadStatus(pkg.Id).Item1.Should().Be(WikiPackage.StatusApplied);
        r.NeedsUnlink.Select(o => o.PackageId).Should().Equal(pkg.Id);
        LogLines("retract").Should().ContainSingle().Which.Should().Contain(page2).And.NotContain(PagePath);
    }

    [Fact]
    public async Task Retract_КарточкаСДиапазономДругойЧасти_ПакетХранитГолыйId_СноскаСнимается()
    {
        // Мутация «сверка карточки по голому id» (вернуть RefMatches(cardRef, IdPartOf(...)) без
        // префикса origin): карточка «session:chat-A#m1-9» не совпадает с запросом «…#m3-5» —
        // страница прошла бы как «след другого источника», сноска осталась бы: NotContain("[^") и
        // Unlinked.Pages краснеют, пакет ушёл бы в reverted со сноской на снятый источник.
        await WriteCardAndPage();
        var pkg = await Applied(WikiSections.Common, ChatId, Op(WikiPackage.OpAppend, PagePath, ClaimA));
        File.Delete(AppliedRecordOf(pkg.Id));

        var r = await _sut.RetractAsync(User, SourceRef + "#m3-5");

        r.Ok.Should().BeTrue(r.Reason);
        r.Unlinked.Should().ContainSingle().Which.Pages.Should().Equal(PagePath);
        File.ReadAllText(PageFull(PagePath)).Should().NotContain("[^");
        ReadStatus(pkg.Id).Item1.Should().Be(WikiPackage.StatusReverted);
    }

    [Fact]
    public async Task Retract_ЦельСноскиНеОпознана_ПричинаНазываетСноскуИЦель_ПакетОстаётсяApplied()
    {
        // Мутация «неопознанную сноску считать обработанной» ловит и соседний набор; здесь
        // держится точная формулировка причины: «сноска с неопознанной целью: s1 → «…»».
        await WriteCardAndPage();
        var pkg = await Applied(WikiSections.Common, Op(WikiPackage.OpAppend, PagePath, ClaimA));
        File.Delete(AppliedRecordOf(pkg.Id));
        File.Delete(PageFull(CardPath));
        var before = File.ReadAllBytes(PageFull(PagePath));

        var r = await _sut.RetractAsync(User, SourceRef);

        r.Ok.Should().BeTrue(r.Reason);
        r.Failed.Should().ContainSingle().Which.Should()
            .Contain("сноска с неопознанной целью: s1 → «" + CardTitle + "»").And.Contain("не опознан");
        File.ReadAllBytes(PageFull(PagePath)).Should().Equal(before);
        ReadStatus(pkg.Id).Item1.Should().Be(WikiPackage.StatusApplied);
    }

    [Fact]
    public async Task Retract_СтраницуСнеслиВнеХранителя_УжеВозвращённое_ВAlreadyPagesИсхода()
    {
        // Мутация «не пробрасывать AlreadyPages» (не передавать r.AlreadyPages в PackageOutcome):
        // Equal краснеет — исход назвал бы страницу возвращённой этим откатом (хвост R-W3-3).
        const string newPage = "Общее/Новое-знание.md";
        await WriteCardAndPage();
        var pkg = await Applied(WikiSections.Common, Op(WikiPackage.OpCreate, newPage, ClaimB));
        File.Delete(PageFull(newPage));

        var r = await _sut.RetractAsync(User, SourceRef);

        r.Ok.Should().BeTrue(r.Reason);
        var o = r.Outcomes.Should().ContainSingle().Which;
        o.Reverted.Should().BeTrue();
        o.PackageId.Should().Be(pkg.Id);
        o.AlreadyPages.Should().Equal([newPage]);
        o.RestoredPages.Should().Contain(newPage);
    }

    // ─── Помощники ────────────────────────────────────────────────────────────────────────────

    private string AppliedRecordOf(string packageId) =>
        Path.Combine(WikiPaths.Keeper(_dir, User), "applied", packageId + ".json");
    private string RetractStateOf(string packageId, string pageKey) =>
        Path.Combine(WikiPaths.Keeper(_dir, User), "retract", packageId, pageKey);

    private Project CreateServerProject(string name, string owner = User)
    {
        var root = Path.Combine(_dir, "repo", owner, name);
        Directory.CreateDirectory(root);
        return _projects.Create(name, root, owner, owner);
    }
}
