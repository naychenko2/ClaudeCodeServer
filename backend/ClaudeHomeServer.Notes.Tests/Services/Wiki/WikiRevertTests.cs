using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// S4b-3 (ADR-035, решение 12г, мера 4): откат применённого пакета по прообразам, строка op=revert
// в журнале, REST-кнопка и чистка прообразов старше 90 дней. Применение и прообразы — S4b-2
// (WikiApplyTests); здесь — то, что делается после применения: возврат байт в байт, отказ по чужой
// правке, согласованность манифеста после отката и граница срока хранения.
//
// Окна ставятся дисковым состоянием, а не гонкой: чужая правка кладется между применением и
// откатом явно; Task.Delay в этом файле нет.
public class WikiRevertTests : IDisposable
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
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly RecordingEventLog _events = new();
    private readonly WikiApplyService _sut;
    private readonly WikiPackageRevert _revert;

    public WikiRevertTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_revert_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));
        // Флаг llm-wiki включён: состояние читается моделью (BuildFresh), источник wiki в модель
        // попадает только с ним (тот же приём, что в WikiApplyTests).
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, _events, new WikiFlagOn());
        _keeper = new WikiKeeper(_notes, _config, _events);
        _sut = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config, _events);
        _revert = new WikiPackageRevert(_notes, _keeper, _config);
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

    private WikiPackage CreateAccepted(params WikiPackage.Operation[] ops)
    {
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = Section,
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

    // Пакет доведён до «applied» переходами стора, но его страницы на диск не записаны: для
    // исходов, которые решаются до чтения прообразов (чужой раздел, статус). Страница операции
    // принадлежит его разделу — это проверяет и форма пакета в сторе.
    private string NewAppliedPackageInStore(string section)
    {
        var folder = WikiSections.Folder(section);
        folder.Should().NotBeNull($"«{section}» — не ключ раздела вики");
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = section,
            Sources = [new WikiPackage.SourceRef("session", "chat-1")],
            // Folder() отдаёт папку с хвостовым «/» — страница склеивается без разделителя.
            Operations = [Op(WikiPackage.OpAppend, folder + "X.md", "Контенер собран на второй машине")],
            Status = WikiPackage.StatusChecking,
        };
        WikiPackageStore.Create(Root, pkg).Ok.Should().BeTrue();
        foreach (var step in new[] { WikiPackage.StatusProposed, WikiPackage.StatusApplying, WikiPackage.StatusApplied })
            WikiPackageStore.Transition(Root, pkg.Id, step).Ok.Should().BeTrue(step);
        return pkg.Id;
    }

    private (string Status, string? Reason) ReadStatus(string id)
    {
        var read = WikiPackageStore.TryRead(Root, id);
        read.State.Should().Be(WikiPackageFileState.Valid);
        return (read.Package!.Status, read.Package!.StatusReason);
    }

    // ─── 1. Откат без чужих правок: байт в байт, журнал, манифест, второй Prepare (c1) ───

    [Fact]
    public async Task ОткатБезЧужихПравок_ВозвращаетПобайтно_ЖурналСOpRevert_МанифестСогласован()
    {
        // Мутация «без возврата по прообразам» (заменить цикл возврата на пустой): страница
        // остаётся применённой — Equal(preimageBytes) краснеет.
        await WriteCardAndPage();
        var pkg = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"));
        var applied = await _sut.ApplyAsync(User, pkg, Ctx);
        applied.Applied.Should().BeTrue(applied.Reason);
        var preimageBytes = File.ReadAllBytes(PreimageOf(pkg.Id, PagePath));

        var r = await _revert.RevertAsync(User, pkg.Id);

        r.Reverted.Should().BeTrue(r.Reason);
        r.RestoredPages.Should().Equal(new[] { PagePath });

        // Страница — байт в байт состояние до пакета: прообраз и есть источник возврата (мера 4).
        File.ReadAllBytes(PageFull(PagePath)).Should().Equal(preimageBytes);

        // Манифест следует за файлами: запись о возвращённой странице — хэш её байтов на диске.
        var manifest = WikiManifest.TryRead(Root, Section);
        manifest.State.Should().Be(WikiManifestState.Valid);
        manifest.Manifest[PagePath].Should().Be(Sha256Of(File.ReadAllBytes(PageFull(PagePath))));

        // Строка журнала (мера 6): op=revert, страница, карточка источника, автор — человек
        // (тот же автор, что у ручного adopt: откат — кнопка, а не политика).
        var line = LogLines().Single(l => l.Contains("\"op\":\"revert\""));
        line.Should().Contain(PagePath);
        line.Should().Contain("chat-1", "откат «всё из этого источника» связывает строку с чатом по карточке (мера 5)");
        line.Should().Contain("\"actor\":\"user\"");

        // _log.md — производная от log.jsonl: строка отката в ней (порядок: журнал до проекций).
        File.ReadAllText(PageFull("Общее/_log.md")).Should().Contain("revert");

        // Статус ведётся Transition стора: applied → reverted, конечный (решение постановщика).
        ReadStatus(pkg.Id).Item1.Should().Be(WikiPackage.StatusReverted);

        // Инвариант урока S4b: после отката раздел открыт следующему пакету, а не заблокирован
        // «дрейфом» — второй PrepareAsync того же раздела проходит.
        var next = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"));
        var again = await _sut.PrepareAsync(User, next, Ctx);
        again.Ready.Should().BeTrue(again.Reason);
    }

    // ─── 1b. Повторный откат: отказ по статусу, второй строки журнала нет (доработка) ──────

    [Fact]
    public async Task ПовторныйОткат_ОтказПоСтатусу_ВторойСтрокиЖурналаНет()
    {
        // Мутация «убрать проверку статуса» (снять гейты «reverted»/«не applied» в RevertAsync):
        // второй вызов идёт к откату — Code=CodeStatus краснеет (он CodeClaim: переход
        // «reverted» → «reverted» отвергнут таблицей), и «второй строки нет» краснеет: журнал
        // получает вторую строку op=revert.
        await WriteCardAndPage();
        var pkg = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"));
        var applied = await _sut.ApplyAsync(User, pkg, Ctx);
        applied.Applied.Should().BeTrue(applied.Reason);
        var first = await _revert.RevertAsync(User, pkg.Id);
        first.Reverted.Should().BeTrue(first.Reason);
        var afterFirst = SnapshotPages();

        var r = await _revert.RevertAsync(User, pkg.Id);

        r.Reverted.Should().BeFalse();
        r.Code.Should().Be(WikiPackageRevert.CodeStatus);
        r.Reason.Should().Contain("уже откатан");
        // Вторая строка op=revert не дописывается: кнопка не повторяет уже сделанный откат.
        LogLines().Count(l => l.Contains("\"op\":\"revert\"")).Should().Be(1);
        // Файлы и статус — такие, какими остались после первого отката.
        SnapshotPages().Should().Equal(afterFirst);
        ReadStatus(pkg.Id).Item1.Should().Be(WikiPackage.StatusReverted);

        // Из «reverted» нет переходов вовсе: статус конечный (таблица переходов).
        var t = WikiPackageStore.Transition(Root, pkg.Id, WikiPackage.StatusStale);
        t.Ok.Should().BeFalse();
        t.Code.Should().Be(WikiPackageStore.CodeTransition);

        // «reverted» и «applied» — не открытые: не ждут разбора и не держат лимит 20
        // (список открытых — не ключи таблицы: у «applied» есть переход человека, но он закрыт
        // для кода). Открытые — ровно те, из которых работу продолжает код или человек.
        WikiPackage.IsOpen(WikiPackage.StatusReverted).Should().BeFalse();
        WikiPackage.IsOpen(WikiPackage.StatusApplied).Should().BeFalse();
        WikiPackage.IsOpen(WikiPackage.StatusApplying).Should().BeTrue();
    }

    // ─── 2. Чужая правка одной страницы: отказ с перечнем, файлы не тронуты (c2) ──────────

    [Fact]
    public async Task ОткатПослеЧужойПравкиОднойСтраницы_ОтказСПеречнем_ФайлаРазделаНеТронуты()
    {
        // Мутация «без сверки хэшей» (убрать сравнение diskHash с записью манифеста): откат
        // пошёл бы поверх правки человека — Reverted/Changed краснеют, а чужая правка пропала бы
        // с диска.
        await WriteCardAndPage();
        await WriteViaKeeper(Page2Path, Page2Base);
        var pkg = CreateAccepted(
            Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"),
            Op(WikiPackage.OpAppend, Page2Path, "Решение: перенести сборку на Linux"));
        var applied = await _sut.ApplyAsync(User, pkg, Ctx);
        applied.Applied.Should().BeTrue(applied.Reason);

        // Правка человека после применения, мимо Хранителя: откат стёр бы её вместе с пакетом.
        var foreign = File.ReadAllText(PageFull(Page2Path)) + "\nправка человека после применения.\n";
        File.WriteAllText(PageFull(Page2Path), foreign, new UTF8Encoding(false));
        var before = SnapshotPages();

        var r = await _revert.RevertAsync(User, pkg.Id);

        r.Reverted.Should().BeFalse();
        r.Code.Should().Be(WikiPackageRevert.CodeChanged);
        r.ChangedPages.Should().Equal(new[] { Page2Path }, "перечень — ровно изменённая страница");
        r.Reason.Should().Contain(Page2Path);

        // На диске не тронуто ничего: не откатывается и та страница, которую откатить можно было
        // бы, — пакет откатывается весь или не откатывается вовсе.
        SnapshotPages().Should().Equal(before);
        File.ReadAllText(PageFull(Page2Path)).Should().Be(foreign, "чужая правка не перезаписана и не откатана");

        // Журнал не получает строки отката: откат не случился.
        LogLines().Should().NotContain(l => l.Contains("\"op\":\"revert\""));
    }

    // ─── 3. Create-страница: файл удалён, запись манифеста убрана, второй Prepare проходит ─

    [Fact]
    public async Task ОткатCreateСтраницы_ФайлУдалён_ЗаписьМанифестаУбрана_ВторойPrepareПроходит()
    {
        // Мутация «не убирать ключ» (в ветке пустого прообраза не ставить rollback[page] = null):
        // запись остаётся в манифесте при удалённом файле → NotContainKey краснеет, а второй
        // PrepareAsync падает на CodeDrift («файл из манифеста отсутствует на диске»).
        const string newPage = "Общее/Новое-знание.md";
        await WriteCardAndPage();
        var beforeManifest = new Dictionary<string, string>(WikiManifest.TryRead(Root, Section).Manifest);
        var pkg = CreateAccepted(Op(WikiPackage.OpCreate, newPage, "Решение: перенести сборку на Linux"));
        var applied = await _sut.ApplyAsync(User, pkg, Ctx);
        applied.Applied.Should().BeTrue(applied.Reason);
        File.Exists(PageFull(newPage)).Should().BeTrue();

        var r = await _revert.RevertAsync(User, pkg.Id);

        r.Reverted.Should().BeTrue(r.Reason);
        r.RestoredPages.Should().Equal(new[] { newPage });

        // Пустой прообраз = страницы не было: её файл удаляется, её запись манифеста — вместе с ним.
        File.Exists(PageFull(newPage)).Should().BeFalse();
        var manifest = WikiManifest.TryRead(Root, Section).Manifest;
        manifest.Should().NotContainKey(newPage);
        // Остальные страницы — побайтно как были: откатывает ровно то, что записал пакет.
        foreach (var (page, hash) in beforeManifest)
            Sha256Of(File.ReadAllBytes(PageFull(page))).Should().Be(hash);
        manifest[PagePath].Should().Be(Sha256Of(File.ReadAllBytes(PageFull(PagePath))));

        // Инвариант: следующий PrepareAsync раздела проходит, а не отказывает «файл из манифеста
        // отсутствует».
        var next = CreateAccepted(Op(WikiPackage.OpCreate, newPage, "Решение: перенести сборку на Linux"));
        var again = await _sut.PrepareAsync(User, next, Ctx);
        again.Ready.Should().BeTrue(again.Reason);
    }

    // ─── 4. Откатываются только применённые пакеты ────────────────────────────────────────

    [Fact]
    public async Task ОткатНеПрименённогоПакета_ОтказПоСтатусу_БезПрообразов_Отказ()
    {
        await WriteCardAndPage();
        // Принят, но не применён: на диске его записей нет — откатывать нечего, и отказ называется
        // статусом, а не «пустым успехом».
        var accepted = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"));
        var before = SnapshotPages();

        var r1 = await _revert.RevertAsync(User, accepted.Id);
        r1.Reverted.Should().BeFalse();
        r1.Code.Should().Be(WikiPackageRevert.CodeStatus);

        // Пакет «applied» без прообразов (файлы убраны вручную): тот же честный отказ,
        // а не успех со пустым списком.
        var ghostId = NewAppliedPackageInStore(Section);
        var r2 = await _revert.RevertAsync(User, ghostId);
        r2.Reverted.Should().BeFalse();
        r2.Code.Should().Be(WikiPackageRevert.CodeNoPreimages);

        // Пакета нет вовсе — отказ «нечего откатывать», не NRE и не 500-подобный провал.
        var r3 = await _revert.RevertAsync(User, WikiPackageStore.NewId());
        r3.Reverted.Should().BeFalse();
        r3.Code.Should().Be(WikiPackageRevert.CodeClaim);

        SnapshotPages().Should().Equal(before, "отказ — это отказ: файлов не тронуто");
    }

    // ─── 5. REST (c3): чужой пакет — 404, чужой раздел — 403, отказ по хэшам — 409 с перечнем ─

    [Fact]
    public async Task RestЧужогоПакета_404_ЧужойРаздел_403()
    {
        // Пакета нет в моём корне (чужой id) — 404, как и для чтения.
        var missing = WikiPackageStore.NewId();
        ((IStatusCodeActionResult)await Controller().Revert(missing)).StatusCode.Should().Be(404);

        // Пакет в моём корне, но его раздел называет чужой проект — 403 (тот же контракт, что у decide).
        var foreign = CreateServerProject("Чужой проект", owner: "u2");
        var foreignSection = NewAppliedPackageInStore("projects/" + foreign.Id);
        AssertCode(await Controller().Revert(foreignSection), 403, "wiki_foreign");

        // Без токена — 401 (контракт), а не NRE на claim.
        ((IStatusCodeActionResult)await AnonymousController().Revert(foreignSection)).StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task RestОтказПоХэшам_409_СПеречнемСтраниц()
    {
        await WriteCardAndPage();
        var pkg = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"));
        var applied = await _sut.ApplyAsync(User, pkg, Ctx);
        applied.Applied.Should().BeTrue(applied.Reason);
        File.WriteAllText(PageFull(PagePath), File.ReadAllText(PageFull(PagePath)) + "\nправка человека после применения.\n",
            new UTF8Encoding(false));

        var body = AssertCode(await Controller().Revert(pkg.Id), 409, "wiki_revert_changed");
        body.GetProperty("pages").EnumerateArray().Select(e => e.GetString()).Should().Equal(new[] { PagePath },
            "человек видит, на какие страницы смотреть перед повторной попыткой");

        // Не применённый пакет — тот же 409, по статусу.
        var accepted = CreateAccepted(Op(WikiPackage.OpAppend, Page2Path, "Решение: перенести сборку на Linux"));
        AssertCode(await Controller().Revert(accepted.Id), 409, "wiki_revert_status");
    }

    // ─── 6. Чистка прообразов старше 90 дней (c4) ──────────────────────────────────────────

    [Fact]
    public void ПрообразыСтарше90ДнейУдаленыМладшеИГраницаОстались()
    {
        // Мутация «без сравнения возраста» (условие Age > срока заменить на true): удаляются все
        // три файла — Should().Exist(young, edge) краснеет; мутация «сравнивать >= с cutoff» —
        // граница удаляется — NotContain(older) краснеет.
        var now = DateTimeOffset.UtcNow;
        var old = PreimageOf("pkg-old", PagePath);
        var young = PreimageOf("pkg-young", PagePath);
        var edge = PreimageOf("pkg-edge", PagePath);
        foreach (var p in new[] { old, young, edge })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, new UTF8Encoding(false).GetBytes("прообраз"));
        }
        File.SetLastWriteTimeUtc(old, now.AddDays(-91).UtcDateTime);
        File.SetLastWriteTimeUtc(young, now.AddDays(-89).UtcDateTime);
        File.SetLastWriteTimeUtc(edge, now.AddDays(-90).UtcDateTime);

        var deleted = WikiPackageRevert.PurgeOldPreimages(Root, now);

        deleted.Should().Be(1);
        File.Exists(old).Should().BeFalse("91 день — старше срока хранения");
        File.Exists(young).Should().BeTrue("89 дней — внутри срока");
        File.Exists(edge).Should().BeTrue("ровно 90 суток — не «старше»: интервал хранения [now−90d, now]");
        // Каталог пустого пакета убран: держать нечего.
        Directory.Exists(Path.GetDirectoryName(old)!).Should().BeFalse();
        Directory.Exists(Path.GetDirectoryName(young)!).Should().BeTrue();

        // Пустая вики (каталога прообразов нет) — 0 и без исключения.
        var other = Path.Combine(Path.GetTempPath(), "wiki_revert_empty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            WikiPackageRevert.PurgeOldPreimages(WikiPaths.Root(other, "u9"), now).Should().Be(0);
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    // ─── Помощники контроллера (образец — WikiPackagesControllerTests) ─────────────────────

    private Project CreateServerProject(string name, string owner = User)
    {
        var root = Path.Combine(_dir, "repo", owner, name);
        Directory.CreateDirectory(root);
        return _projects.Create(name, root, owner, owner);
    }

    private static JsonElement AssertCode(IActionResult result, int status, string code)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>($"{status} с телом отказа").Which;
        obj.StatusCode.Should().Be(status);
        var body = JsonDocument.Parse(JsonSerializer.Serialize(obj.Value)).RootElement;
        body.GetProperty("code").GetString().Should().Be(code);
        return body;
    }

    private WikiPackagesController Controller(string user = User)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, user)], "test"));
        return new WikiPackagesController(_projects, _config, _revert)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private WikiPackagesController AnonymousController()
    {
        var http = new DefaultHttpContext(); // User без идентичности — путь боя без токена
        return new WikiPackagesController(_projects, _config, _revert)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }
}
