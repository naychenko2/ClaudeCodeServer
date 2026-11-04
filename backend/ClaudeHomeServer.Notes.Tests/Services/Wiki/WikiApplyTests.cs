using System.Text.Json;
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

// S4b-2 (ADR-035, решение 4 шаги 4–6, решение 12г меры 4 и 6): запись пакета, откат при сбое
// и сторож старта. Проверки до записи (шаги 1–3) — в WikiApplyCheckTests; здесь — то, что
// после: прообразы, файлы, манифест, журнал, статус, возврат по прообразам и закрытие
// «applying» после падения процесса.
//
// Окна между фазами (между проверкой и записью) ставятся дисковым состоянием, а не гонкой:
// PrepareAsync и WriteAsync — две фазы одной двери, тест ставит между ними файл с нужным
// состоянием (чужую правку, файл, который нельзя прочитать). Task.Delay в этом файле нет.
public class WikiApplyTests : IDisposable
{
    private const string User = "u1";
    private const string Section = "common";
    private const string CardTitle = "Карточка чата";
    private const string CardPath = "Общее/Карточка-чата.md";
    private const string PagePath = "Общее/Сущность.md";
    private const string Page2Path = "Общее/Сущность-2.md";

    // Текст источника: по нему выводится цитата (мера 1). Утверждения тестов — его части.
    private const string SourceText =
        "Решение: перенести сборку на Linux. Прежний план был остаться на Windows. Контенер собран на второй машине.";

    private static readonly WikiApplyService.ApplyContext Ctx = new(CardTitle, SourceText);

    private static readonly string CardFile = $$"""
        ---
        type: source
        origin: session
        ref: session:s1#m1-9
        hash: 0123456789012345678901234567890123456789012345678901234567890123
        captured: 2026-10-27
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{CardTitle}}
        """;

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
        Прежний план был остаться на Windows [^s1]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[{{CardTitle}}]] — «Прежний план был остаться на Windows»
        """;

    private static readonly string Page2Base = $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Сущность-2

        ## Сводка
        Контенер собран на второй машине [^s1]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[{{CardTitle}}]] — «Контенер собран на второй машине»
        """;

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly RecordingEventLog _events = new();
    private readonly WikiApplyService _sut;

    public WikiApplyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_apply_write_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        // Флаг llm-wiki включён: состояние читается моделью (BuildFresh), источник wiki в модель
        // попадает только с ним (тот же приём, что в WikiKeeperTests).
        _notes = new NotesService(projects, _config, NullLogger<NotesService>.Instance, _events, flags: new WikiFlagOn());
        _keeper = new WikiKeeper(_notes, _config, _events);
        _sut = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config, _events);
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

    // Записанные вызовы IProjectEventLogService — тот же приём, что в WikiKeeperTests:
    // контракт проверяется без SQLite.
    private sealed class RecordingEventLog : IProjectEventLogService
    {
        public List<ProjectEvent> Appended { get; } = [];

        public ProjectEvent? Append(string projectId, string ownerId, string type, string actor,
            string summary, string? entityRef = null, string? actorUserId = null)
        {
            var e = new ProjectEvent { ProjectId = projectId, OwnerId = ownerId, Type = type, Actor = actor, Summary = summary, EntityRef = entityRef };
            Appended.Add(e);
            return e;
        }
    }

    private string Root => WikiPaths.Root(_dir, User);
    private string PageFull(string pageKey) => Path.Combine(WikiPaths.Pages(_dir, User), pageKey);
    private string PreimageOf(string packageId, string pageKey) =>
        Path.Combine(WikiPaths.Keeper(_dir, User), "preimages", packageId, pageKey);

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private List<string> LogLines()
    {
        var p = Path.Combine(WikiPaths.Keeper(_dir, User), "log.jsonl");
        return File.Exists(p) ? File.ReadAllLines(p).Where(l => l.Length > 0).ToList() : [];
    }

    private async Task WriteViaKeeper(string pageKey, string content)
    {
        var r = await _keeper.WritePageAsync(User, Section, pageKey, content);
        r.Status.Should().Be(WikiOpStatus.Applied, r.Reason);
    }

    private Task WriteCardAndPage() => WriteCardAndPage(PageBase);
    private async Task WriteCardAndPage(string pageContent)
    {
        await WriteViaKeeper(CardPath, CardFile);
        await WriteViaKeeper(PagePath, pageContent);
    }

    // Снимок всего дерева pages/ — путь → sha256 байтов: «как до пакета» проверяется по
    // файлам, а не по признаку «ничего не написали».
    private Dictionary<string, string> SnapshotPages()
    {
        var pagesDir = WikiPaths.Pages(_dir, User);
        var snap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(pagesDir, "*", SearchOption.AllDirectories))
            snap[Path.GetRelativePath(pagesDir, f).Replace('\\', '/')] = Sha256Of(File.ReadAllBytes(f));
        return snap;
    }

    private static WikiPackage.Operation Op(
        string op, string page, string? text, string? target = null,
        string decision = WikiPackage.DecisionAuto, string? human = null,
        IReadOnlyDictionary<string, string>? baseHashes = null) => new()
    {
        Op = op,
        Page = page,
        Text = text,
        Target = target,
        Class = WikiPackage.ClassKey("session", op),
        Decision = decision,
        Reason = decision == WikiPackage.DecisionInbox ? "спорный класс" : null,
        BaseHashes = baseHashes ?? new Dictionary<string, string>(),
        HumanDecision = human,
        HumanReason = human == WikiPackage.HumanRejected ? "не нужно" : null,
    };

    private WikiPackage CreateAccepted(params WikiPackage.Operation[] ops) => CreateAccepted(Section, ops);
    private WikiPackage CreateAccepted(string section, params WikiPackage.Operation[] ops)
    {
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = section,
            Sources = [new WikiPackage.SourceRef("session", "chat-1")],
            Operations = ops,
            Status = WikiPackage.StatusChecking,
        };
        var created = WikiPackageStore.Create(Root, pkg);
        created.Ok.Should().BeTrue(created.Reason);
        WikiPackageStore.Transition(Root, pkg.Id, WikiPackage.StatusProposed).Ok.Should().BeTrue();
        var accepted = WikiPackageStore.Transition(Root, pkg.Id, WikiPackage.StatusAccepted);
        accepted.Ok.Should().BeTrue(accepted.Reason);
        return WikiPackageStore.TryRead(Root, pkg.Id).Package!;
    }

    private (string Status, string? Reason) ReadStatus(string id)
    {
        var read = WikiPackageStore.TryRead(Root, id);
        read.State.Should().Be(WikiPackageFileState.Valid);
        return (read.Package!.Status, read.Package!.StatusReason);
    }

    private WikiApplyStartupGuard NewGuard() =>
        new(_notes, _keeper, _config, NullLogger<WikiApplyStartupGuard>.Instance);

    // ─── 1. Применение целиком: файл, манифест, прообраз, журнал, проекции, статус (c4–c5) ───

    [Fact]
    public async Task ПрименениеПакета_ФайлМанифестПрообразЖурналПроекцииИСтатус_Согласованы()
    {
        await WriteCardAndPage();
        var text = "Контенер собран на второй машине";
        var pkg = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, text));

        var r = await _sut.ApplyAsync(User, pkg, Ctx);

        r.Applied.Should().BeTrue(r.Reason);
        File.ReadAllText(PageFull(PagePath)).Should().Contain(text);

        // Манифест — хэш байтов, прочитанных с диска (та же форма, что у Хранителя):
        // следующая сверка сравнивает то, что реально лежит в файле.
        var manifest = WikiManifest.TryRead(Root, Section);
        manifest.State.Should().Be(WikiManifestState.Valid);
        manifest.Manifest[PagePath].Should().Be(Sha256Of(File.ReadAllBytes(PageFull(PagePath))));

        // Прообраз — байты страницы до применения, по тому же относительному пути: тем,
        // чем держится откат (мера 4), и оно не мусор — остаётся и после успеха.
        File.ReadAllBytes(PreimageOf(pkg.Id, PagePath)).Should().Equal(new UTF8Encoding(false).GetBytes(PageBase),
            "прообраз = состояние до пакета, байт в байт");

        // Строка журнала (мера 6): op=apply, политика auto (спорных не было), источник пакета.
        var line = LogLines().Single(l => l.Contains("\"op\":\"apply\""));
        line.Should().Contain("\"policy\":\"auto\"");
        // SV1-В, c3: строка apply называет применённый пакет — читатель находит его по id, а не склейкой.
        JsonDocument.Parse(line).RootElement.GetProperty("packages").EnumerateArray().Select(x => x.GetString())
            .Should().Equal(pkg.Id);
        line.Should().Contain("chat-1", "откат «всё из этого источника» ищет по карточке источника (мера 5)");
        line.Should().Contain(PagePath, "изменённые страницы — то, что человек видит в журнале");

        // _log.md — производная от log.jsonl: без строки применения он остался бы на полшага
        // позади (порядок шага 6 в коде — журнал перед проекциями).
        File.ReadAllText(PageFull("Общее/_log.md")).Should().Contain("apply");

        ReadStatus(pkg.Id).Item1.Should().Be(WikiPackage.StatusApplied);
        // Замкнутый статус: применить дважды нельзя.
        var again = await _sut.ApplyAsync(User, pkg, Ctx);
        again.Applied.Should().BeFalse();
        again.Code.Should().Be(WikiApplyService.CodeStatus);
    }

    // ─── 2. Сбой после записи первого из двух файлов: оба файла как до пакета (c1) ───

    [Fact]
    public async Task СбойПослеЗаписиПервогоФайлаИзДвух_ОбаФайлаПобайтноКакДоПакета_СтатусApplyFailed()
    {
        // Мутация «без восстановления» (в FailAfterWrite убрать RestoreFromPreimages):
        // первый файл остаётся применённым — SnapshotPages().Equal(before) краснеет, а
        // «откатано по прообразам» в причине исчезает.
        await WriteCardAndPage();
        await WriteViaKeeper(Page2Path, Page2Base);
        var pkg = CreateAccepted(
            Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"),
            Op(WikiPackage.OpAppend, Page2Path, "Решение: перенести сборку на Linux"));
        var before = SnapshotPages();

        var prepared = await _sut.PrepareAsync(User, pkg, Ctx);
        prepared.Ready.Should().BeTrue(prepared.Reason);
        prepared.Rendered.Select(d => d.Path).Should().Equal(new[] { PagePath, Page2Path },
            "порядок записи — порядок первой правки из рендера");

        // Сбой ФС на ВТОРОЙ странице: её байты нельзя прочитать (чужой держатель, FileShare.None) —
        // ровно там, где шаг 5 сверяет базовое состояние. Окно не гонка: фаза записи вызвана
        // после того, как блок уже стоит.
        WikiApplyService.ApplyResult r;
        using (var block = new FileStream(PageFull(Page2Path), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            r = await _sut.WriteAsync(User, pkg.Id, prepared, Ctx);
        // Блок снят до снимка дерева: исход записи от этого не меняется, а прочитать
        // заблокированный файл SnapshotPages не может.
        r.Applied.Should().BeFalse();
        r.Code.Should().Be(WikiApplyService.CodeInternal);

        // Откатан — только записанный этим вызовом. Второй файл не тронут никогда: побайтно
        // оба — как до пакета. Первое утверждение теста — именно байты, а не текст причины.
        SnapshotPages().Should().Equal(before, "первый возвращён по прообразу, второй не записывался");
        r.Reason.Should().Contain("откатано по прообразам").And.Contain(PagePath);

        // Манифест не тронут (его правка — шаг 6, до него дело не дошло): его хэши равны байтам
        // на диске, т.е. следующая проверка этого раздела пройдёт, а не упадёт на «дрейфе».
        var manifest = WikiManifest.TryRead(Root, Section).Manifest;
        manifest[PagePath].Should().Be(Sha256Of(File.ReadAllBytes(PageFull(PagePath))));
        manifest[Page2Path].Should().Be(Sha256Of(File.ReadAllBytes(PageFull(Page2Path))));

        ReadStatus(pkg.Id).Item1.Should().Be(WikiPackage.StatusApplyFailed);
        File.Exists(PreimageOf(pkg.Id, PagePath)).Should().BeTrue("прообразы — не мусор: откат по пакету делает их них (мера 4)");
    }

    // ─── 3. Внешняя правка между проверкой и записью (c3) ───────────────────────

    [Fact]
    public async Task ВнешняяПравкаМеждуПроверкойИЗаписью_ApplyFailed_ЧужаяПравкаНеПерезаписана()
    {
        // Мутация «без сверки базы» (убрать сравнение diskHash/recorded с BaseSnapshot):
        // запись пошла бы поверх правки человека — File.ReadAllText(...).Be(foreign) краснеет.
        await WriteCardAndPage();
        var pkg = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"));

        var prepared = await _sut.PrepareAsync(User, pkg, Ctx);
        prepared.Ready.Should().BeTrue(prepared.Reason);

        // Правка человека между фазами: мимо Хранителя, вне блокировки — то, ради чего
        // запись заново сверяет базу, а не доверяет проверке двух минут назад.
        var foreign = PageBase + "\nправка человека между проверкой и записью.\n";
        File.WriteAllText(PageFull(PagePath), foreign, new UTF8Encoding(false));

        var r = await _sut.WriteAsync(User, pkg.Id, prepared, Ctx);

        r.Applied.Should().BeFalse();
        r.Code.Should().Be(WikiApplyService.CodeDrift);
        r.Reason.Should().Contain("изменилось после проверки");
        File.ReadAllText(PageFull(PagePath)).Should().Be(foreign,
            "чужая правка не перезаписана и не откатана: о ней решает человек (adopt или новый разбор)");
        ReadStatus(pkg.Id).Item1.Should().Be(WikiPackage.StatusApplyFailed);
        // Прообразов этой страницы нет: её не записывали — откатывать нечего, и чужое не трогано.
        File.Exists(PreimageOf(pkg.Id, PagePath)).Should().BeFalse();
    }

    // ─── 4. Пакет, застрявший в «applying»: сторож старта (c2) ──────────────────

    [Fact]
    public async Task ПакетApplyingПриСтартеСлужбы_ОткатанПоПрообразам_МанифестСогласован_СтатусApplyFailed()
    {
        // Дисковое состояние после падения процесса посреди шага 6: прообраз записан (шаг 4),
        // страница записана и её хэш уже в манифесте (шаг 5 + его же правка), статус не дописан.
        await WriteCardAndPage();
        var pkg = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"));
        WikiPackageStore.Transition(Root, pkg.Id, WikiPackage.StatusApplying).Ok.Should().BeTrue();

        var oldBytes = File.ReadAllBytes(PageFull(PagePath));
        Directory.CreateDirectory(Path.GetDirectoryName(PreimageOf(pkg.Id, PagePath))!);
        File.WriteAllBytes(PreimageOf(pkg.Id, PagePath), oldBytes);
        await WriteViaKeeper(PagePath, PageBase + "\nприменение, после которого процесс упало.\n");
        var lostHash = WikiManifest.TryRead(Root, Section).Manifest[PagePath];

        await NewGuard().StartAsync(CancellationToken.None);

        // Страница — прежняя байт в байт; манифест — о ней, а не о потерянном применении:
        // иначе человек получил бы «дрейф» там, где всё уже решено, и решил бы его заново.
        File.ReadAllBytes(PageFull(PagePath)).Should().Equal(oldBytes);
        var manifest = WikiManifest.TryRead(Root, Section).Manifest;
        manifest[PagePath].Should().NotBe(lostHash);
        manifest[PagePath].Should().Be(Sha256Of(oldBytes));

        var (status, reason) = ReadStatus(pkg.Id);
        status.Should().Be(WikiPackage.StatusApplyFailed, "сторож не оставляет «applying» ни при каком исходе");
        reason.Should().Contain("сторож старта").And.Contain(PagePath);

        // Сторож идемпотентен: второй старт этого пакета уже не трогает (переход closed → closed
        // не в таблице) — и не чинит то, что не сломано.
        await NewGuard().StartAsync(CancellationToken.None);
        ReadStatus(pkg.Id).Item1.Should().Be(WikiPackage.StatusApplyFailed);
    }

    [Fact]
    public async Task ПакетApplyingБезПрообразов_СтатусВсёРавноЗакрыт_ФайлыИЧужиеПакетыНеТронуты()
    {
        // Падение было на шаге 4 или раньше: ни одной страницы записано не было. Откатывать
        // нечего — но «applying» обязан закрыться: смысл сторожа в том, что в нём не остаётся
        // ни одного пакета (его заявка держит повторное применение навсегда).
        await WriteCardAndPage();
        var stuck = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"));
        WikiPackageStore.Transition(Root, stuck.Id, WikiPackage.StatusApplying).Ok.Should().BeTrue();
        // Пакет в другом статусе — не дело сторожа.
        var other = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, "Решение: перенести сборку на Linux"));
        var before = SnapshotPages();

        await NewGuard().StartAsync(CancellationToken.None);

        ReadStatus(stuck.Id).Item1.Should().Be(WikiPackage.StatusApplyFailed);
        ReadStatus(other.Id).Item1.Should().Be(WikiPackage.StatusAccepted, "сторож откатывает «applying», а не всё подряд");
        SnapshotPages().Should().Equal(before, "записи не было — откатывать нечего, файлов не тронуто");
    }

    // ─── 5. «inbox»: на диске только после решения человека; политика — в журнале (c4) ───

    [Fact]
    public async Task InboxБезРешенияЧеловекаНаДискНеВыходит_ПринятыйВыходит_ВЖурналеПолитикаInbox()
    {
        // Мутация «брать все операции» (Applies без HumanDecision): вторая страница меняется,
        // NotBe(before2) краснеет; мутация «политика всегда auto»: вторая строка журнала
        // теряет «inbox».
        await WriteCardAndPage();
        await WriteViaKeeper(Page2Path, Page2Base);

        var unresolved = CreateAccepted(
            Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"),
            Op(WikiPackage.OpAppend, Page2Path, "Решение: перенести сборку на Linux", decision: WikiPackage.DecisionInbox));
        var before2 = File.ReadAllText(PageFull(Page2Path));

        var r1 = await _sut.ApplyAsync(User, unresolved, Ctx);
        r1.Applied.Should().BeTrue(r1.Reason);
        File.ReadAllText(PageFull(PagePath)).Should().Contain("Контенер собран на второй машине");
        File.ReadAllText(PageFull(Page2Path)).Should().Be(before2,
            "спорная операция без решения человека не пишется на диск ни при какой политике");
        LogLines().Should().ContainSingle("один применённый пакет — одна строка журнала");
        LogLines()[0].Should().Contain("\"policy\":\"auto\"").And.NotContain(Page2Path);

        // Та же спорная операция с принятым решением человека — пишется, и журнал говорит
        // «inbox»: человека спросили, и это видно (мера 6).
        var accepted = CreateAccepted(
            Op(WikiPackage.OpAppend, Page2Path, "Решение: перенести сборку на Linux",
                decision: WikiPackage.DecisionInbox, human: WikiPackage.HumanAccepted));
        var r2 = await _sut.ApplyAsync(User, accepted, Ctx);
        r2.Applied.Should().BeTrue(r2.Reason);
        File.ReadAllText(PageFull(Page2Path)).Should().Contain("Решение: перенести сборку на Linux");
        LogLines().Should().HaveCount(2);
        LogLines()[1].Should().Contain("\"policy\":\"inbox\"").And.Contain(Page2Path);
    }

    // ─── 6. Кэш графа и событие проекта (шаг 6) ────────────────────────────────

    [Fact]
    public async Task Применение_МодельВидитНовуюСтраницуСразу_ИСобытиеПроектаНаКаждыйФайл()
    {
        // Раздел проекта: событие idёт только туда, где есть адресат (тот же порядок, что у
        // Хранителя); «Общее» и «Личное» его не имеют.
        const string section = "projects/p1";
        const string cardRel = "Проекты/p1/Карточка-чата.md";
        const string pageRel = "Проекты/p1/Знание.md";
        // Запись — Хранителем и по своему разделу: пара (раздел, страница) проверяется им же.
        async Task WriteInProjectSection(string pageKey, string content)
        {
            var w = await _keeper.WritePageAsync(User, section, pageKey, content);
            w.Status.Should().Be(WikiOpStatus.Applied, w.Reason);
        }
        await WriteInProjectSection(cardRel, CardFile);

        // Кэш тёплый: без инвалидации новая страница появилась бы только по истечении TTL.
        _notes.GetSummaries(User, null, null).Select(n => n.Title).Should().NotContain("Знание");
        // Событие от записи карточки — не предмет этого утверждения.
        _events.Appended.Clear();

        var pkg = CreateAccepted(section, Op(WikiPackage.OpCreate, pageRel, "Решение: перенести сборку на Linux"));
        var r = await _sut.ApplyAsync(User, pkg, Ctx);
        r.Applied.Should().BeTrue(r.Reason);

        _notes.GetSummaries(User, null, null).Select(n => n.Title).Should().Contain("Знание",
            "кэш инвалидирован: применение видно модели сразу (шаг 6), а не по истечении TTL");
        _events.Appended.Select(e => (e.ProjectId, e.Type, e.Actor)).Should().Equal(
            ("p1", ProjectEventTypes.WikiChanged, "keeper"));
    }

    // ─── 7. Сбой ПОСЛЕ записи манифеста: манифест откатан вместе с файлами (ревью S4b-2, M1) ───

    [Fact]
    public async Task СбойПослеЗаписиМанифеста_ФайлыИМанифестКакДоПакета_ВторойPrepareПроходит()
    {
        // Мутация «не откатывать манифест» (в FailAfterWrite убрать RollbackTo и проекции):
        // манифест остаётся с хэшем применённого состояния при возвращённых файлах →
        // Should().Equal(beforeManifest) краснеет, а второй PrepareAsync падает на CodeDrift:
        // раздел был бы заблокирован до ручного adopt.
        await WriteCardAndPage();
        var pkg = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"));

        var prepared = await _sut.PrepareAsync(User, pkg, Ctx);
        prepared.Ready.Should().BeTrue(prepared.Reason);

        var before = SnapshotPages();
        var beforeManifest = new Dictionary<string, string>(WikiManifest.TryRead(Root, Section).Manifest);

        // Сбой ровно там, где риск: log.jsonl сделан каталогом ПОСЛЕ того, как шаг 6 уже
        // записал хэши в манифест, — AppendLog бросает уже после RecordApplied. Окно между
        // фазами ставится дисковым состоянием, не гонкой.
        var logPath = Path.Combine(WikiPaths.Keeper(_dir, User), "log.jsonl");
        File.Exists(logPath).Should().BeFalse("до применения журнала этого раздела нет");
        Directory.CreateDirectory(logPath);

        var r = await _sut.WriteAsync(User, pkg.Id, prepared, Ctx);

        r.Applied.Should().BeFalse();
        r.Code.Should().Be(WikiApplyService.CodeInternal);
        r.Reason.Should().Contain("откатано по прообразам").And.Contain(PagePath);

        // Файлы побайтно как до — и манифест как до: его хэши равны байтам на диске, т.е.
        // сверка следующего PrepareAsync проходит, а не отказывает разделу.
        SnapshotPages().Should().Equal(before, "страницы возвращены по прообразам");
        WikiManifest.TryRead(Root, Section).Manifest.Should().Equal(beforeManifest,
            "хэш применённого состояния в манифесте при возвращённых файлах — и есть заблокированный раздел");

        ReadStatus(pkg.Id).Item1.Should().Be(WikiPackage.StatusApplyFailed);

        // Инвариант: следующий пакет этого раздела проходит проверку, а не «wiki_apply_drift».
        var next = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"));
        var again = await _sut.PrepareAsync(User, next, Ctx);
        again.Ready.Should().BeTrue(again.Reason);
    }

    // ─── 8. Сторож старта и create-страница: запись манифеста убрана вместе с файлом (M2) ───

    [Fact]
    public async Task СторожСтарта_CreateСтраницы_ФайлУдалён_ЗаписьМанифестаУбрана_ВторойPrepareПроходит()
    {
        // Мутация «не убирать ключ» (в RollbackOne для пустого прообраза не ставить
        // rollback[page] = null): запись остаётся в манифесте при удалённом файле →
        // Should().Equal(beforeManifest) краснеет, а второй PrepareAsync падает на CodeDrift
        // («файл из манифеста отсутствует на диске»).
        const string newPage = "Общее/Новое-знание.md";
        await WriteCardAndPage();
        var pkg = CreateAccepted(Op(WikiPackage.OpCreate, newPage, "Решение: перенести сборку на Linux"));
        WikiPackageStore.Transition(Root, pkg.Id, WikiPackage.StatusApplying).Ok.Should().BeTrue();

        var before = SnapshotPages();
        var beforeManifest = new Dictionary<string, string>(WikiManifest.TryRead(Root, Section).Manifest);

        // Дисковое состояние после падения процесса посреди шага 6 применения create:
        // страница записана (прообраз пустой — её не было), её хэш в манифесте, статус не дописан.
        Directory.CreateDirectory(Path.GetDirectoryName(PreimageOf(pkg.Id, newPage))!);
        File.WriteAllBytes(PreimageOf(pkg.Id, newPage), []);
        var created = $$"""
            ---
            type: topic
            aliases: []
            owns: []
            sources: []
            status: active
            ---

            # Новое знание

            ## Сводка
            Решение: перенести сборку на Linux [^s1]

            ## Хронология

            ## Противоречия

            ## История

            [^s1]: [[{{CardTitle}}]] — «Решение: перенести сборку на Linux»
            """;
        File.WriteAllText(PageFull(newPage), created, new UTF8Encoding(false));
        WikiManifest.RecordApplied(Root, Section,
            new Dictionary<string, string> { [newPage] = Sha256Of(new UTF8Encoding(false).GetBytes(created)) });

        await NewGuard().StartAsync(CancellationToken.None);

        // Файла нет — и записи о нём нет: манифест и дерево побайтно таковы, каковы были до
        // оборванного применения. Иначе человек получил бы «дрейф» там, где всё уже решено.
        File.Exists(PageFull(newPage)).Should().BeFalse();
        SnapshotPages().Should().Equal(before, "возвращено ровно то, что было записано, и только это");
        WikiManifest.TryRead(Root, Section).Manifest.Should().Equal(beforeManifest,
            "запись create-страницы переживает её файл — это и есть «файл из манифеста отсутствует»");

        var (status, reason) = ReadStatus(pkg.Id);
        status.Should().Be(WikiPackage.StatusApplyFailed);
        reason.Should().Contain("сторож старта").And.Contain(newPage);

        // Инвариант: следующий PrepareAsync раздела проходит.
        var next = CreateAccepted(Op(WikiPackage.OpCreate, newPage, "Решение: перенести сборку на Linux"));
        var again = await _sut.PrepareAsync(User, next, Ctx);
        again.Ready.Should().BeTrue(again.Reason);
    }

    // ─── B4b (ADR-038, предохранитель 4): признак «ИИ поменял моё» в строке журнала ───

    private static WikiPackage.Operation HumanTargetSupersede(string decision, string? human = null) =>
        Op(WikiPackage.OpSupersede, PagePath, "Контенер собран на второй машине",
            target: "Прежний план был остаться на Windows", decision: decision, human: human) with
        {
            Class = WikiPackage.ClassKey("session", WikiPackage.OpSupersede, humanTarget: true),
        };

    [Fact]
    public async Task ИИСамЗаменилУтверждениеЧеловека_СтрокаApplyНесётOverrodeHuman()
    {
        // Мутация «не писать признак» (убрать overrodeHuman: в вызове AppendLog) — Contain краснеет.
        await WriteCardAndPage();
        var pkg = CreateAccepted(HumanTargetSupersede(WikiPackage.DecisionAuto));

        var applied = await _sut.ApplyAsync(User, pkg, Ctx);

        applied.Applied.Should().BeTrue(applied.Reason);
        LogLines().Single(l => l.Contains("\"op\":\"apply\"")).Should().Contain("\"overrodeHuman\":true");
    }

    [Fact]
    public async Task ОбычноеПрименение_ВСтрокеApplyПоляOverrodeHumanНет()
    {
        // Мутация «писать всегда» (overrodeHuman: true) краснеет здесь: поле только у замен по человеку.
        await WriteCardAndPage();
        var pkg = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"));

        (await _sut.ApplyAsync(User, pkg, Ctx)).Applied.Should().BeTrue();

        LogLines().Single(l => l.Contains("\"op\":\"apply\"")).Should().NotContain("overrodeHuman");
    }

    [Fact]
    public async Task ЗаменуПоЧеловекуПринялЧеловекИзВходящих_ПризнакаНет()
    {
        // Решил человек — ИИ ничего не «переопределял»: признак только у decision=auto.
        await WriteCardAndPage();
        var pkg = CreateAccepted(HumanTargetSupersede(WikiPackage.DecisionInbox, WikiPackage.HumanAccepted));

        var applied = await _sut.ApplyAsync(User, pkg, Ctx);

        applied.Applied.Should().BeTrue(applied.Reason);
        LogLines().Single(l => l.Contains("\"op\":\"apply\"")).Should().NotContain("overrodeHuman");
    }
}
