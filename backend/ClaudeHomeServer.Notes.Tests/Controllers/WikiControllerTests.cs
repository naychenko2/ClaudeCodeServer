using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace ClaudeHomeServer.Tests.Controllers;

// S2f: REST вики по разделам (ADR-035/ADR-036). Владелец — только из токена; раздел — ключ
// из проекта/сферы, а не путь из тела; человек видит всю свою вики. Контроллер не пишет
// страницы: «Завести», adopt и удаление сироты идут через WikiKeeper.
// Образец построения — NotesWikiSourceTests: temp-дир + боевые UserStore/ProjectManager +
// контроллер напрямую (тестируется контракт действия, не транспорт).
public class WikiControllerTests : IDisposable
{
    private const string User = "u1";
    private readonly string _dir;
    private readonly string _pages;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly WikiKeeper _keeper;
    private readonly WikiLintService _lint;
    // S7c-0: конструктор контроллера держит читателя журнала (его зовёт GET /api/wiki/journal).
    // Тесты этого файла про журнал ничего не проверяют — экземпляр нужен, чтобы резолвился
    // конструктор; свои сценарии журнала — в WikiJournalReaderTests.
    private readonly WikiJournalReader _journal;
    // S7b0b: то же про захват и очередь разбора (их зовёт POST /api/wiki/ingest/note):
    // экземпляры нужны для конструктора, сценарии разбора — в WikiIngestNoteTests.
    private readonly WikiSourceCapture _capture;
    private readonly WikiIngestService _ingest;
    private readonly WikiApplyService _apply;
    private readonly WikiNoteTransfer _transfer;
    private readonly NoSpheres _spheres = new();
    private readonly NoSessions _sessions = new();

    public WikiControllerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_controller_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _pages = WikiPaths.Pages(_dir, User);
        var notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance);
        _keeper = new WikiKeeper(notes, _config);
        // S3-3: контроллер держит службу линта (её же зовёт кнопка POST /api/wiki/lint).
        _lint = new WikiLintService(notes, _keeper, _spheres, _config, NullLogger<WikiLintService>.Instance);
        _journal = new WikiJournalReader(notes, _sessions, _config);
        _apply = new WikiApplyService(notes, _keeper, _spheres, _config);
        _ingest = new WikiIngestService(new NoopCheapRunner(), _spheres, _apply, _config);
        _transfer = new WikiNoteTransfer(notes, _apply, _projects);
        _capture = new WikiSourceCapture(notes, _projects, _sessions, _config, _keeper);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── 1. Дерево разделов ────────────────────────────────────────────────

    [Fact]
    public void Дерево_СвоиРазделыСЖивымиИменами_СиротаУдалённогоПроектаПомечена()
    {
        var p = CreateServerProject("Живой проект");
        var key = "projects/" + p.Id;
        var sectionDir = Path.Combine(_pages, "Проекты", p.Id);
        Directory.CreateDirectory(sectionDir);
        File.WriteAllText(Path.Combine(sectionDir, "X.md"), "# X\n\nтекст.", new UTF8Encoding(false));

        var tree = TreeOf(Controller());
        tree.Initialized.Should().BeFalse("вики не заведена — признак на диске, а не папки разделов");
        // LI-2: GET дерева вики не заводит — чтение не имеет побочных эффектов, пустое
        // состояние показывается как есть. Мутация «заводить на GET» — красный.
        File.Exists(Path.Combine(_pages, WikiPaths.SchemaFileName)).Should().BeFalse();
        // Личный и «Общее» — в дереве всегда (ADR-036, решение 1)
        tree.Sections.Should().Contain(s => s.Key == "personal" && !s.Orphan);
        tree.Sections.Should().Contain(s => s.Key == "common" && !s.Orphan);

        var own = tree.Sections.Single(s => s.Key == key);
        own.Name.Should().Be("Живой проект");
        own.Orphan.Should().BeFalse();
        own.PageCount.Should().Be(1);

        // Живое имя: переименование проекта меняет имя раздела — путь (папка по id) не двигается
        _projects.Update(p.Id, "Переименованный", p.RootPath);
        TreeOf(Controller()).Sections.Single(s => s.Key == key).Name.Should().Be("Переименованный");

        // Удалённый проект: его раздел на диске — сирота, с пометкой и без живого имени
        _projects.Delete(p.Id);
        var orphan = TreeOf(Controller()).Sections.Single(s => s.Key == key);
        orphan.Orphan.Should().BeTrue();
        orphan.Name.Should().BeNull();
    }

    // ─── 2. Только своя вики: 403 чужой раздел, 401 без токена ─────────────

    [Fact]
    public void ЧужойПроект_403_БезТокена_401()
    {
        var foreign = CreateServerProject("Чужой проект", owner: "u2");

        // Дерево чужих разделов не показывает вовсе (GetByOwner)
        TreeOf(Controller()).Sections.Should().NotContain(s => s.Key == "projects/" + foreign.Id);

        // Но если чужой ключ назван явно — 403, а не чужие данные
        AssertCode(Controller().Section("projects/" + foreign.Id), 403, "wiki_foreign");

        // Без токена — 401 (контракт), а не NRE на claim
        ((IStatusCodeActionResult)AnonymousController().Tree().Result!).StatusCode.Should().Be(401);
    }

    // ─── 3. «Завести»: локальный проект отказан кодом способности ──────────

    [Fact]
    public async Task Завести_ЛокальныйПроект_ОтказСКодомСпособности_Серверный_ЗаводитВики()
    {
        var local = _projects.CreateLocal("Локальный", "/home/u/local", User, "dev-1");
        var ctl = Controller();

        var res = await ctl.Initialize(new WikiInitRequest("projects/" + local.Id));
        AssertCode(res, 409, "wiki_section_off");
        // Причина — текст способности ProjectCapabilities, не местная формулировка контроллера
        // (инлайновых проверок локальности в контроллере нет — сторож G10)
        JsonDocument.Parse(JsonSerializer.Serialize(((ObjectResult)res).Value)).RootElement
            .GetProperty("message").GetString().Should().Be(ProjectCapabilities.WikiSectionLocalOffReason);

        // Раздел сферы резервирован за следующей фичей (ADR-036, решение 7)
        AssertCode(await ctl.Initialize(new WikiInitRequest("spheres/s1")), 409, "wiki_sphere_reserved");

        // Серверный проект — заводятся: признак заведённости появляется на диске
        var server = CreateServerProject("Серверный");
        ((OkObjectResult)await ctl.Initialize(new WikiInitRequest("projects/" + server.Id)))
            .StatusCode.Should().Be(200);
        File.Exists(Path.Combine(_pages, WikiPaths.SchemaFileName)).Should().BeTrue();

        // «Завести» без адресата — та же операция (одна на владельца), повтор не меняет файл
        ((OkObjectResult)await ctl.Initialize(null)).StatusCode.Should().Be(200);
    }

    // ─── 3а. Гейт флага llm-wiki на «Завести» (W1-3) ──────────────────────

    [Fact]
    public async Task Завести_ФлагВыключен_409_ВикаНеЗаводится()
    {
        AssertCode(await Controller(flagsOn: false).Initialize(null), 409, "wiki_llm_wiki_off");
        // Мутация «убрать проверку флага в Initialize»: вики заводится — файл появляется, тест красный.
        File.Exists(Path.Combine(_pages, WikiPaths.SchemaFileName)).Should().BeFalse("гейт — до создания состояния");
    }

    // ─── 4. Удаление раздела: только сирота ────────────────────────────────

    [Fact]
    public async Task УдалениеРаздела_ТолькоСирота_СноситПапкуИМанифест_ЖурналОстаётся()
    {
        var p = CreateServerProject("Проект");
        var key = "projects/" + p.Id;
        await _keeper.EnsureInitializedAsync(User);
        (await _keeper.WritePageAsync(User, key, $"Проекты/{p.Id}/X.md", "---\ntype: topic\nstatus: active\n---\n# X\n\nтекст."))
            .Status.Should().Be(WikiOpStatus.Applied);

        // Живой раздел удалить нельзя: сирота — решение человека, но живой — нет
        AssertCode(await Controller().DeleteSection(key), 409, "wiki_not_orphan");

        // Чужая правка + adopt: в журнале появляется строка — история раздела
        File.WriteAllText(Path.Combine(_pages, "Проекты", p.Id, "X.md"), "# X\n\nчужая правка.", new UTF8Encoding(false));
        (await _keeper.AdoptAsync(User, key, $"Проекты/{p.Id}/X.md")).Status.Should().Be(WikiOpStatus.Applied);

        // Проект удалён — раздел стал сиротой — кнопка удаляет его
        _projects.Delete(p.Id);
        ((OkObjectResult)await Controller().DeleteSection(key)).StatusCode.Should().Be(200);
        Directory.Exists(Path.Combine(_pages, "Проекты", p.Id)).Should().BeFalse();
        File.Exists(ManifestFile(key)).Should().BeFalse("манифест мёртвого раздела не оставляем");
        File.ReadAllText(Path.Combine(WikiPaths.Keeper(_dir, User), "log.jsonl")).Should().Contain("adopt",
            "журнал вики — история, она не удаляется вместе с разделом");
    }

    // ─── 5. Каталог и страница ─────────────────────────────────────────────

    [Fact]
    public async Task КаталогИСтраница_ШапкаСтатусИЧислоИсточников()
    {
        var p = CreateServerProject("Проект");
        var key = "projects/" + p.Id;
        var pageKey = $"Проекты/{p.Id}/Знание.md";
        await _keeper.EnsureInitializedAsync(User);
        (await _keeper.WritePageAsync(User, key, pageKey,
            "---\ntype: topic\nsummary: Короткая шапка\nsources: [Чат 1, Чат 2]\nstatus: active\n---\n# Знание\n\nтекст."))
            .Status.Should().Be(WikiOpStatus.Applied);

        var catalog = ((OkObjectResult)Controller().Section(key)).Value
            .Should().BeAssignableTo<WikiCatalogDto>().Which;
        var item = catalog.Pages.Single(x => x.PageKey == pageKey);
        item.Title.Should().Be("Знание");
        item.Summary.Should().Be("Короткая шапка");
        item.Type.Should().Be("topic");
        item.Status.Should().Be("active");
        item.SourcesCount.Should().Be(2);
        // Проекции раздела (_index/_log) в каталог не входят
        catalog.Pages.Should().NotContain(x => x.PageKey.Contains("_index") || x.PageKey.Contains("_log"));

        var page = JsonDocument.Parse(JsonSerializer.Serialize(((OkObjectResult)Controller().Page(key, pageKey)).Value))
            .RootElement;
        page.GetProperty("content").GetString().Should().Contain("# Знание");

        // Страница чужого раздела — 400: тот же контракт пары (раздел, страница), что у CheckPage
        AssertCode(Controller().Page("common", pageKey), 400, "wiki_page_key");
        // Такого файла нет — 404
        ((IStatusCodeActionResult)Controller().Page(key, "Проекты/" + p.Id + "/Нет.md")).StatusCode.Should().Be(404);
    }

    // ─── 6. Дрейф страницы доходит до UI (дефект ревью S5b) ────────────────

    [Fact]
    public async Task Дрейф_ПравкаМимоХранителя_ВКаталогеИСтранице_AdoptСнимаетБезПерезаписиФайла()
    {
        var p = CreateServerProject("Проект");
        var key = "projects/" + p.Id;
        var pageKey = $"Проекты/{p.Id}/Знание.md";
        var file = Path.Combine(_pages, "Проекты", p.Id, "Знание.md");
        await _keeper.EnsureInitializedAsync(User);
        (await _keeper.WritePageAsync(User, key, pageKey, "---\ntype: topic\nstatus: active\n---\n# Знание\n\nтекст."))
            .Status.Should().Be(WikiOpStatus.Applied);

        // Чистая страница, записанная Хранителем: файл равен своей записи — дрейфа нет
        var clean = CatalogOf(Controller(), key);
        clean.ManifestState.Should().Be("ok");
        clean.Pages.Single(x => x.PageKey == pageKey).Drift.Should().BeFalse("файл равен записи манифеста");
        PageJson(Controller(), key, pageKey).GetProperty("drift").GetBoolean().Should().BeFalse();

        // Правка человека мимо Хранителя: файл ≠ запись — это дрейф, и он виден и в каталоге,
        // и на странице: иначе плашка и «Принять как есть» недостижимы до 409 применения.
        File.WriteAllText(file, File.ReadAllText(file) + "\nчужая строка.", new UTF8Encoding(false));
        CatalogOf(Controller(), key).Pages.Single(x => x.PageKey == pageKey).Drift
            .Should().BeTrue("правка мимо Хранителя доходит до UI до adopt");
        var drifted = PageJson(Controller(), key, pageKey);
        drifted.GetProperty("drift").GetBoolean().Should().BeTrue();
        drifted.GetProperty("manifestState").GetString().Should().Be("ok");

        // Вторая половина инварианта: файл на диске БЕЗ записи в валидном манифесте — тоже
        // дрейф (та же мера, что сверка применения), а не «неизвестная страница».
        File.WriteAllText(Path.Combine(_pages, "Проекты", p.Id, "Своя.md"), "# Своя\n\nзаведена человеком.", new UTF8Encoding(false));
        CatalogOf(Controller(), key).Pages.Single(x => x.PageKey == $"Проекты/{p.Id}/Своя.md").Drift
            .Should().BeTrue("файл есть, а записи нет — дрейф, как у применения");

        // adopt («Принять как есть»): дрейф снимается, файл и его шапка не тронуты
        var bytes = File.ReadAllText(file);
        ((OkObjectResult)await Controller().Adopt(new WikiAdoptRequest(key, pageKey))).StatusCode.Should().Be(200);
        File.ReadAllText(file).Should().Be(bytes, "adopt принимает диск как есть, ничего не перезаписывая");
        CatalogOf(Controller(), key).Pages.Single(x => x.PageKey == pageKey).Drift.Should().BeFalse("после adopt дрейфа нет");
        PageJson(Controller(), key, pageKey).GetProperty("drift").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void РазделБезМанифеста_ДрейфаНет_ManifestStateЧестный_ЧужойРаздел_БезУтечки()
    {
        var p = CreateServerProject("Проект");
        var key = "projects/" + p.Id;
        var pageKey = $"Проекты/{p.Id}/Знание.md";
        var dir = Path.Combine(_pages, "Проекты", p.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Знание.md"), "# Знание\n\nзаведено человеком.", new UTF8Encoding(false));

        // Манифеста нет вовсе (норма до первого применения): страница НЕ дрейф — иначе на
        // разделе без манифеста рисовалась бы ложная плашка; состояние отдаётся честно.
        var catalog = CatalogOf(Controller(), key);
        catalog.ManifestState.Should().Be("missing");
        catalog.Pages.Single(x => x.PageKey == pageKey).Drift.Should().BeFalse("без манифеста ложного дрейфа нет");
        var page = PageJson(Controller(), key, pageKey);
        page.GetProperty("drift").GetBoolean().Should().BeFalse();
        page.GetProperty("manifestState").GetString().Should().Be("missing");

        // Битый манифест — то же: не исключение из GET, не дрейф; видно состояние «invalid».
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestFile(key))!);
        File.WriteAllText(ManifestFile(key), "{ не json", new UTF8Encoding(false));
        CatalogOf(Controller(), key).ManifestState.Should().Be("invalid");
        CatalogOf(Controller(), key).Pages.Single(x => x.PageKey == pageKey).Drift.Should().BeFalse();
        PageJson(Controller(), key, pageKey).GetProperty("manifestState").GetString().Should().Be("invalid");

        // Чужой раздел — 403 как раньше, и отказ не раскрывает чужого содержимого: ни страниц,
        // ни их дрейфа (отказ выносится до чтения диска — ResolveSection).
        var foreign = CreateServerProject("Чужой проект", owner: "u2");
        var res = Controller().Section("projects/" + foreign.Id);
        AssertCode(res, 403, "wiki_foreign");
        JsonSerializer.Serialize(((ObjectResult)res).Value).Should().NotContain("pages");
    }

    // ─── 7. Сервисный токен (typ=svc): мутирующие ручки — 403, чтение — как раньше (B2a) ──

    // Сервисный JWT хода (MCP) к вики не адресат: «Завести», adopt, удаление раздела, разбор
    // заметки и линт — только действия человека. Мутация (критерий c2): убрать проверку
    // IsServiceToken в любой из этих ручек — её тест краснеет (ручка доходит до операции).

    [Fact]
    public async Task СервисныйТокен_Завести_403_ВикаНеЗаводится()
    {
        var res = await Controller(serviceToken: true).Initialize(null);
        ((IStatusCodeActionResult)res).StatusCode.Should().Be(403);
        // Отказ до операции: ничего не записано — схема вики не появилась.
        File.Exists(Path.Combine(_pages, WikiPaths.SchemaFileName)).Should().BeFalse();
    }

    [Fact]
    public async Task СервисныйТокен_Adopt_403_ДрейфНаМесте()
    {
        var p = CreateServerProject("Проект");
        var key = "projects/" + p.Id;
        var pageKey = $"Проекты/{p.Id}/Знание.md";
        await _keeper.EnsureInitializedAsync(User);
        var content = "---\ntype: topic\nstatus: active\n---\n# Знание\n\nтекст.";
        (await _keeper.WritePageAsync(User, key, pageKey, content)).Status.Should().Be(WikiOpStatus.Applied);
        // Дрейф на странице: операция, которую хочет сделать svc-вызов, реальна (adopt принял бы её).
        File.WriteAllText(Path.Combine(_pages, "Проекты", p.Id, "Знание.md"), content + "\nчужая правка.", new UTF8Encoding(false));

        var res = await Controller(serviceToken: true).Adopt(new WikiAdoptRequest(key, pageKey));
        ((IStatusCodeActionResult)res).StatusCode.Should().Be(403);
        // Отказ до операции: дрейф цел — adopt его не снял.
        CatalogOf(Controller(), key).Pages.Single(x => x.PageKey == pageKey).Drift.Should().BeTrue();
    }

    [Fact]
    public async Task СервисныйТокен_УдалитьРаздел_403_СиротаОстаётся()
    {
        var p = CreateServerProject("Проект");
        var key = "projects/" + p.Id;
        await _keeper.EnsureInitializedAsync(User);
        (await _keeper.WritePageAsync(User, key, $"Проекты/{p.Id}/X.md", "---\ntype: topic\nstatus: active\n---\n# X\n\nтекст."))
            .Status.Should().Be(WikiOpStatus.Applied);
        _projects.Delete(p.Id); // раздел стал сиротой — кнопка человека удалила бы его

        var res = await Controller(serviceToken: true).DeleteSection(key);
        ((IStatusCodeActionResult)res).StatusCode.Should().Be(403);
        Directory.Exists(Path.Combine(_pages, "Проекты", p.Id)).Should().BeTrue("отказ до удаления: папка цела");
    }

    [Fact]
    public async Task СервисныйТокен_РазобратьЗаметку_403_ЗаявкиНет()
    {
        var res = await Controller(serviceToken: true).IngestNote(new WikiIngestNoteRequest("note-1"));
        ((IStatusCodeActionResult)res).StatusCode.Should().Be(403);
        // Отказ до проверок состояния: заявка не ставилась — открытых пакетов нет.
        WikiPackageStore.CountOpen(WikiPaths.Root(_dir, User)).Should().Be(0);
    }

    [Fact]
    public async Task СервисныйТокен_Линт_403_ПрогонНеНачат()
    {
        var res = await Controller(serviceToken: true).Lint();
        ((IStatusCodeActionResult)res).StatusCode.Should().Be(403);
        // Отказ до TryRunOwnerAsync: журнал прогона не начат.
        File.Exists(Path.Combine(WikiPaths.Keeper(_dir, User), "metrics.jsonl")).Should().BeFalse();
    }

    // Ручки чтения под svc отвечают как раньше (200): отказ — только на мутирующих.
    // Мутация «распространить гейт на Tree»: этот тест краснеет.
    [Fact]
    public void СервисныйТокен_ЧтениеДерева_200()
    {
        TreeOf(Controller(serviceToken: true)).Sections.Should().Contain(s => s.Key == "personal");
    }

    // ─── 8. «Начать вики заново»: чистка ИИ-страниц (J-P2) ──────────────────

    // Контракт ручки: svc — 403 до операции; предпросмотр — числа по разделам/классам и пути,
    // страницы/манифест/журнал побайтно целы (маркеры доводки основ при первом входе под замок
    // — названы в снимке, а не скрыты прогревом); запуск — удалён ровно состав предпросмотра,
    // архив относительно корня вики; недведенный шаг после состоявшейся чистки — 200 с warning,
    // без пути хоста в тексте; отказ Хранителя — 4xx с причиной, не 500; два одновременных
    // запуска — оба 200, второй ничего не удаляет. Сам предикат чистки, формат журнала и
    // содержимое архива — в WikiPurgeTests (J-P1a): здесь их не дублируем.

    [Fact]
    public async Task Чистка_СервисныйТокен_403_ВикаПобайтноЦела()
    {
        BuildPurgeFixture();
        var before = SnapshotWiki();

        var res = await Controller(serviceToken: true).Purge(dryRun: false);
        ((IStatusCodeActionResult)res).StatusCode.Should().Be(403);
        // Мутация «убрать IsServiceToken в Purge»: чистка дошла бы до удаления — снимок красный.
        SnapshotWiki().Should().Equal(before, "отказ до операции: ни одного байта не тронуто");
    }

    [Fact]
    public async Task Чистка_Предпросмотр_ЧислаПоКлассамИПути_ДискПобайтноЦел_БезАрхива()
    {
        BuildPurgeFixture();
        var before = SnapshotWiki();

        var body = ((OkObjectResult)await Controller().Purge(dryRun: true)).Value
            .Should().BeAssignableTo<WikiPurgeDto>().Which;
        body.DryRun.Should().BeTrue();
        body.CountsBySection.Keys.Should().BeEquivalentTo(["common"],
            "в «Личном» удалять нечего — этого раздела в числах нет");
        body.CountsBySection["common"].Should().Equal(new Dictionary<string, int>
        {
            [WikiProjections.AuthorshipAi] = 1,
            [WikiFrontmatter.TypeChronicle] = 1,
            [WikiFrontmatter.TypeSource] = 1,
        }, "числа по разделам и классам — то, что человек видит перед кнопкой");
        body.Pages.Select(p => p.PageKey).Should().BeEquivalentTo(PurgeAiPage, PurgeChronicle, PurgeCardAi);
        body.Pages.Single(p => p.PageKey == PurgeAiPage).Class.Should().Be(WikiProjections.AuthorshipAi);
        body.Pages.Single(p => p.PageKey == PurgeCardAi).Class.Should().Be(WikiFrontmatter.TypeSource);
        body.Deleted.Should().Be(0);
        body.ArchivePath.Should().BeNull("предпросмотр не создаёт архив");
        body.StaledPackages.Should().Be(0);

        // Первый вход в раздел под замком делает ленивую доводку основ (WikiKeeper.cs:599,
        // WikiBaselines.cs:72): маркеры keeper/baselines-backfilled/ — единственное, что
        // предпросмотр дописывает на диске; прогревать их больше нечем — честный снимок
        // называет их прямо (ревью 8bdbef69). Мутация «dryRun пишет или удаляет»: изменённый
        // хэш страницы/манифеста/журнала или удалённый файл — соответствующая строка красная.
        var after = SnapshotWiki();
        var expectedMarkers = new[] { "common", "personal" }
            .Select(s => $"keeper/baselines-backfilled/{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant()}");
        after.Keys.Except(before.Keys).Should().BeEquivalentTo(expectedMarkers,
            "добавились только маркеры доводки основ — по одному на затронутый раздел");
        before.Keys.Except(after.Keys).Should().BeEmpty("предпросмотр ничего не удаляет");
        foreach (var (key, hash) in before)
            after[key].Should().Be(hash, "страницы, манифест и журнал байт в байт прежние");
    }

    [Fact]
    public async Task Чистка_БезЯвногоDryRun_Предпросмотр_НичегоНеУдалено()
    {
        BuildPurgeFixture();
        var body = ((OkObjectResult)await Controller().Purge(dryRun: null)).Value
            .Should().BeAssignableTo<WikiPurgeDto>().Which;
        // Мутация «dryRun ?? false»: разрушающее действие без явного решения удалило бы
        // страницы — DryRun и BeTrue красные.
        body.DryRun.Should().BeTrue("разрушающее действие требует явного решения человека");
        body.Deleted.Should().Be(0);
        File.Exists(Path.Combine(_pages, PurgeAiPage)).Should().BeTrue();
    }

    [Fact]
    public async Task Чистка_Запуск_УдаляетСоставПредпросмотра_АрхивОтКорняВики_ЧеловеческоеЦело()
    {
        BuildPurgeFixture();
        var kept = new Dictionary<string, string>
        {
            [PurgeHumanPage] = File.ReadAllText(Path.Combine(_pages, PurgeHumanPage)),
            [PurgeCardHuman] = File.ReadAllText(Path.Combine(_pages, PurgeCardHuman)),
        };

        var body = ((OkObjectResult)await Controller().Purge(dryRun: false)).Value
            .Should().BeAssignableTo<WikiPurgeDto>().Which;
        body.DryRun.Should().BeFalse();
        body.Deleted.Should().Be(3, "ровно состав предпросмотра: ИИ-страница, хроника, карточка-сирота");
        body.ArchivePath.Should().NotBeNull().And.StartWith("keeper/archive/purge-");
        var archiveFull = Path.Combine(WikiPaths.Root(_dir, User), body.ArchivePath!);
        File.Exists(archiveFull).Should().BeTrue("путь — относительно корня вики, файл на диске");
        body.StaledPackages.Should().Be(0, "открытых ИИ-пакетов в фикстуре нет");
        // Мутация «заполнять warning всегда»: эта строка красная — предупреждать не о чем.
        body.Warning.Should().BeNull("перезапуск разбора доведён: предупреждать не о чем");

        foreach (var page in new[] { PurgeAiPage, PurgeChronicle, PurgeCardAi })
            File.Exists(Path.Combine(_pages, page)).Should().BeFalse($"«{page}» — класс чистки, файл удалён");
        foreach (var (page, text) in kept)
            File.ReadAllText(Path.Combine(_pages, page)).Should().Be(text, $"«{page}» цел: человеческое чистка не трогает (И6)");

        // Архив — снимок ДО удаления: без того, что снесено, «начать заново» нечем восстановить
        // (мутация «архив после удаления» — эти строки красные).
        using var zip = ZipFile.OpenRead(archiveFull);
        // Имена записей архива — относительные пути с «/» от корня вики (AddTreeToZip), т. е.
        // «pages/» + pageKey: пути берутся теми же константами фикстуры, другой копии пути нет.
        zip.Entries.Select(e => e.FullName).Should().Contain(
            [$"pages/{PurgeAiPage}", $"pages/{PurgeChronicle}", $"pages/{PurgeCardAi}"]);
    }

    [Fact]
    public async Task Чистка_НевалидныйМанифестРаздела_409_СПричиной_СтраницыЦелы()
    {
        BuildPurgeFixture();
        File.WriteAllText(ManifestFile("common"), "{ не json", new UTF8Encoding(false));

        var res = await Controller().Purge(dryRun: false);
        // Мутация «маппить отказ в 500»: AssertCode красный — 500 у этой ручки нет ни на одном коде.
        AssertCode(res, 409, "wiki_purge_manifest");
        // Отказ до разрушения: ни одной записи не сделано, архива нет — операция не начата.
        File.Exists(Path.Combine(_pages, PurgeAiPage)).Should().BeTrue();
        Directory.Exists(Path.Combine(WikiPaths.Keeper(_dir, User), "archive")).Should().BeFalse();
    }

    [Fact]
    public async Task Чистка_ДваОдновременныхЗапуска_Оба200_ВторойНичего_СуммаРовнаСоставу()
    {
        BuildPurgeFixture();
        // Два POST одного владельца через один WikiKeeper: замок разделов (J-P1a) держит всю
        // операцию, второй входит после первого и своих файлов под замком уже не читает —
        // вторично не удаляет. Мутация «без замка»: File.Delete чужих файлов молча проходит,
        // сумма удваивается — BeEquivalentTo красное.
        var first = Task.Run(() => Controller().Purge(dryRun: false));
        var second = Task.Run(() => Controller().Purge(dryRun: false));
        var a = await first.WaitAsync(TimeSpan.FromSeconds(30));
        var b = await second.WaitAsync(TimeSpan.FromSeconds(30));

        ((IStatusCodeActionResult)a).StatusCode.Should().Be(200);
        ((IStatusCodeActionResult)b).StatusCode.Should().Be(200);
        var deleted = new[] { DeletedOf(a), DeletedOf(b) };
        deleted.Sum().Should().Be(3, "ни одна страница не удалена дважды и ни одна не потеряна");
        deleted.Should().BeEquivalentTo([0, 3], "один запуск делает чистку целиком, второй — ноль");
        File.Exists(Path.Combine(_pages, PurgeHumanPage)).Should().BeTrue("человеческое цело при любом порядке гонки");
    }

    [Fact]
    public async Task Чистка_ПерезапускРазбораНеДоведён_200_СWarning_БезПутиХоста()
    {
        // Путь Хранителя Ok=true с причиной (WikiKeeper.cs:420): чистка состоялась, а запись
        // флага бэклога — единственный шаг перезапуска, бросающий вызывающему (гашение пакетов
        // и сброс курсоров молчат о своём отказе). Блокируем её состоянием диска, без шва в
        // коде (тот же приём, что в ЧисткаЧастичныйСбойУдаления J-P1a): на Windows файл флага
        // read-only — File.Move с overwrite отказывает по атрибуту; на Unix каталог keeper/
        // без права записи — temp-файл флага не создаётся (EACCES). Всё, что идёт до флага,
        // остаётся писательным: каталог архива и файл журнала готовятся заранее — журнал
        // дописывается, а не создаётся, и под заблокированным каталогом это удалось бы ему.
        BuildPurgeFixture();
        var keeperDir = WikiPaths.Keeper(_dir, User);
        File.WriteAllText(Path.Combine(keeperDir, "log.jsonl"), "", new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(keeperDir, "archive"));
        if (OperatingSystem.IsWindows())
        {
            var flagFull = Path.Combine(keeperDir, "backlog-until.json");
            File.WriteAllText(flagFull, "{}", new UTF8Encoding(false));
            File.SetAttributes(flagFull, FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(keeperDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        IActionResult res;
        try
        {
            res = await Controller().Purge(dryRun: false);
        }
        finally
        {
            // Права возвращаем сразу: и assertions, и уборка дерева не должны спотыкаться
            // о заблокированный каталог.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(keeperDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Состоявшаяся чистка не прячется в молчании: 200 + warning (J-P2, ревью 8bdbef69).
        // Мутация «не заполнять warning» (всегда null): Be и NotContain ниже — красные.
        var body = ((OkObjectResult)res).Value.Should().BeAssignableTo<WikiPurgeDto>().Which;
        body.DryRun.Should().BeFalse();
        body.Deleted.Should().Be(3, "чистка состоялась: предупреждение её не отменяет");
        body.ArchivePath.Should().NotBeNull("архив до флага: операция дошла до конца");
        body.Warning.Should().Be("чистка выполнена, но перезапуск разбора не доведён — повтори позже");
        // e.Message дословно не отдаётся: в нём абсолютный путь хоста — тестовый корень.
        // Мутация «warning = res.Reason дословно»: эта строка красная.
        body.Warning.Should().NotContain(_dir, "в warning нет пути хоста");
        WikiBacklogFlag.IsActive(WikiPaths.Root(_dir, User), DateOnly.FromDateTime(DateTime.UtcNow))
            .Should().BeFalse("флаг не поставлен: причина честная");
    }

    // ─── Помощники ─────────────────────────────────────────────────────────

    private Project CreateServerProject(string name, string owner = User)
    {
        var root = Path.Combine(_dir, "repo", owner, name);
        Directory.CreateDirectory(root);
        return _projects.Create(name, root, owner, owner);
    }

    private string ManifestFile(string sectionKey) =>
        Path.Combine(WikiPaths.Keeper(_dir, User), "manifest", WikiManifest.SafeFileName(sectionKey) + ".json");

    // Фикстура чистки — уменьшенная копия боевой из J-P1a (WikiPurgeTests), ровно по классам
    // предиката. Удаляемое: ИИ-страница (сноска на карточку не-human — «ai»), хроника (по
    // построению ИИ), карточка-сирота (на неё ссылается только удаляемая страница). Целое:
    // человеческая страница (сноска на карточку origin: human) и её карточка — ссылку держит
    // живая страница. Файлы писаны мимо Хранителя: манифеста нет (Missing — не отказ чистки),
    // тот же приём, что у BuildFixture в J-P1a.
    private const string PurgeAiPage = "Общее/Ии-знание.md";
    private const string PurgeChronicle = "Общее/Хроники/с1.md";
    private const string PurgeCardAi = "Общее/Источники/Карточка-ии.md";
    private const string PurgeCardHuman = "Личное/Источники/Карточка-человека.md";
    private const string PurgeHumanPage = "Личное/Моя-страница.md";

    private static readonly string PurgeAiFile = """
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Ии-знание

        ## Сводка
        Контенер собран на второй машине [^s1]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[Карточка-ии]] — «Контенер собран на второй машине»
        """;

    private static readonly string PurgeChronicleFile = """
        ---
        type: chronicle
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Хроника чата с1

        ## О чём
        Разбор окна чата.

        ## Что решили
        Идти по плану.

        ## Что выяснили
        Ничего нового.

        ## Открытые вопросы
        —
        """;

    private static readonly string PurgeCardAiFile = """
        ---
        type: source
        origin: session
        ref: session:s1#m1-9
        hash: 0123456789012301234567890123456789012345678901234567890123456789
        captured: 2026-10-27
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Карточка-ии
        """;

    private static readonly string PurgeCardHumanFile = """
        ---
        type: source
        origin: human
        ref: notes/моя-заметка.md
        hash: 1111111111111111111111111111111111111111111111111111111111111111
        captured: 2026-10-27
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Карточка-человека
        """;

    private static readonly string PurgeHumanFile = """
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Моя-страница

        ## Сводка
        перенести сборку на Linux [^s1]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[Карточка-человека]] — «перенести сборку на Linux»
        """;

    private async Task BuildPurgeFixture()
    {
        await _keeper.EnsureInitializedAsync(User);
        foreach (var (pageKey, content) in new[]
        {
            (PurgeAiPage, PurgeAiFile), (PurgeChronicle, PurgeChronicleFile), (PurgeCardAi, PurgeCardAiFile),
            (PurgeCardHuman, PurgeCardHumanFile), (PurgeHumanPage, PurgeHumanFile),
        })
        {
            var full = Path.Combine(_pages, pageKey);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, new UTF8Encoding(false));
        }
    }

    // Снимок всего дерева вики — путь → sha256 байтов: «на диске не тронуто ничего» мерится
    // файлами, а не признаком «ничего не писали» (тот же приём, что в J-P1a).
    private Dictionary<string, string> SnapshotWiki()
    {
        var root = WikiPaths.Root(_dir, User);
        var snap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            snap[Path.GetRelativePath(root, f).Replace('\\', '/')] =
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant();
        return snap;
    }

    private static int DeletedOf(IActionResult r) =>
        ((OkObjectResult)r).Value.Should().BeAssignableTo<WikiPurgeDto>().Which.Deleted;

    private static WikiTreeDto TreeOf(WikiController ctl) =>
        ((OkObjectResult)ctl.Tree().Result!).Value.Should().BeAssignableTo<WikiTreeDto>().Which;

    private static WikiCatalogDto CatalogOf(WikiController ctl, string section) =>
        ((OkObjectResult)ctl.Section(section)).Value.Should().BeAssignableTo<WikiCatalogDto>().Which;

    // Ответ страницы читается как JSON: это то, что видит фронт (контракт поля, а не тип).
    private static JsonElement PageJson(WikiController ctl, string section, string page) =>
        JsonDocument.Parse(JsonSerializer.Serialize(((OkObjectResult)ctl.Page(section, page)).Value)).RootElement;

    private static void AssertCode(IActionResult result, int status, string code)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>($"{status} с телом отказа").Which;
        obj.StatusCode.Should().Be(status);
        JsonDocument.Parse(JsonSerializer.Serialize(obj.Value)).RootElement
            .GetProperty("code").GetString().Should().Be(code);
    }

    // serviceToken (B2a): claim typ=svc — сервисный токен хода (тот же признак, что в
    // TasksControllerTests); для проверки отказа 403 на мутирующих ручках.
    private WikiController Controller(string user = User, bool flagsOn = true, bool serviceToken = false)
    {
        var http = new DefaultHttpContext();
        var claims = new List<Claim> { new("sub", user) };
        if (serviceToken) claims.Add(new("typ", "svc"));
        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return new WikiController(_keeper, _projects, _spheres, _lint, _journal, flagsOn ? new WikiFlagOn() : new WikiFlagOff(), _config, _capture, _ingest, _apply, _transfer)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private WikiController AnonymousController()
    {
        var http = new DefaultHttpContext(); // User без идентичности — путь боя без токена
        return new WikiController(_keeper, _projects, _spheres, _lint, _journal, null, _config, _capture, _ingest, _apply, _transfer)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private sealed class WikiFlagOn : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }

    // Боевой шлюз флагов не бывает null: «флаг выключен» — это шлюз, отвечающий «нет» на ключ llm-wiki.
    private sealed class WikiFlagOff : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key != FeatureFlagKeys.LlmWiki;
    }

    // Сфер нет: предмет этих тестов — проектные разделы и сироты.
    private sealed class NoSpheres : ISphereDirectory
    {
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public bool Enabled(string ownerId) => false;
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }

    // Каталог чатов пуст: эти тесты не зовут чтения журнала.
    private sealed class NoSessions : ISessionDirectory
    {
        public Session? GetById(string id) => null;
        public IReadOnlyCollection<Session> GetAll() => [];
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
        public bool SetWikiMark(string sessionId, string? mark) => false;
    }

    // Модель в этих тестах не зовётся ни разу (разбор — сценарии WikiIngestNoteTests);
    // если что-то позвал — пусть будет видно. Тот же отказной раннер, что в соседних файлах.
    private sealed class NoopCheapRunner : ICheapTextRunner
    {
        private static Task<T> Throw<T>(string actionKey) =>
            Task.FromException<T>(new InvalidOperationException("ИИ-действие не ожидалось: " + actionKey));

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "none";
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Throw<string>(actionKey);
        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) =>
            Throw<string?>(actionKey);
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Throw<OneShotResult>(actionKey);
    }
}
