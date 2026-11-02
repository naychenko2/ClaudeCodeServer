using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace ClaudeHomeServer.Tests.Services;

// S7c-0 (ADR-035 §12г меры 4–6, пункт 5 «Нужно от бэка»): чтение журнала вики для человека.
// Строки log.jsonl — носители события, их порядок в файле — хронология: «новые сверху» —
// обратный порядок файла. Пакет даёт состояние и класс, каталог сессий и vault — имя источника.
// Граница чтения — хвост `take`: большой файл целиком в память не читается никогда.
// Образец построения — WikiPackagesControllerTests: temp-дир + боевые UserStore/ProjectManager/
// NotesService + служба (и контроллер) напрямую.
public class WikiJournalReaderTests : IDisposable
{
    private const string User = "u1";
    private const string Foreign = "u2";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly NotesService _notes;
    private readonly SessionsStub _sessions = new();
    private readonly WikiJournalReader _reader;

    public WikiJournalReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_journal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, null, new FlagOn());
        _reader = new WikiJournalReader(_notes, _sessions, _config);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── 1. Три пакета разных классов: порядок, политика, класс, фильтры ────

    [Fact]
    public void Журнал_ТриПакета_НовыеСверху_ПолитикаКлассИСтатусВерны()
    {
        _sessions.Known["chat-1"] = new Session { Id = "chat-1", OwnerId = User, Name = "Разговор о сборке" };

        // Хронология вызовов = порядок дописывания строк: сначала auto из чата в «Общем»,
        // затем спорная (inbox) по заметке в «Личном», затем link из файла в «Общем».
        NewPackage("common", WikiPackage.StatusApplied, WikiGate.OriginSession, "session:chat-1#m1-5",
            Op(WikiGate.OriginSession, WikiPackage.OpAppend, WikiPackage.DecisionAuto, "Общее/Сборка.md"));
        NewPackage("personal", WikiPackage.StatusApplied, WikiGate.OriginNote, "note:n-1",
            Op(WikiGate.OriginNote, WikiPackage.OpSupersede, WikiPackage.DecisionInbox, "Личное/План.md"));
        // Третий пакет — тот случай, ради которого строка журнала и склеивается с пакетом:
        // применено само, потом откатано. Строка применения остаётся, состояние — текущее.
        NewPackage("common", WikiPackage.StatusReverted, WikiGate.OriginFile, "file:p1/a.md",
            Op(WikiGate.OriginFile, WikiPackage.OpLink, WikiPackage.DecisionAuto, "Общее/Сборка.md"));

        Log("apply", "common", ["Общее/Сборка.md"], "session:chat-1#m1-5", "keeper", WikiPackage.DecisionAuto);
        Log("apply", "personal", ["Личное/План.md"], "note:n-1", "keeper", WikiPackage.DecisionInbox);
        Log("apply", "common", ["Общее/Сборка.md"], "file:p1/a.md", "keeper", WikiPackage.DecisionAuto);

        var res = Read(Any());
        res.Entries.Should().HaveCount(3, "окно больше числа записей — весь журнал");
        res.InvalidLines.Should().Be(0);

        // Новые сверху — обратный порядок файла (он же обратная хронология).
        res.Entries[0].Source.Should().Be("file:p1/a.md");
        res.Entries[1].Source.Should().Be("note:n-1");
        res.Entries[2].Source.Should().Be("session:chat-1#m1-5");

        res.Entries[0].Status.Should().Be(WikiPackage.StatusReverted, "состояние пакета — текущее, а не то, что было при применении");
        res.Entries[0].Policy.Should().Be(WikiPackage.DecisionAuto);
        res.Entries[0].Classes.Should().Equal("file×link");
        res.Entries[0].Origin.Should().Be(WikiGate.OriginFile);
        res.Entries[0].Pages.Should().Equal("Общее/Сборка.md");

        // «Через Входящие» — то, где человека спросили (мера 6), а не то, что он принял.
        res.Entries[1].Status.Should().Be(WikiPackage.StatusApplied);
        res.Entries[1].Policy.Should().Be(WikiPackage.DecisionInbox);
        res.Entries[1].Classes.Should().Equal("note×supersede");

        // Имя источника — из каталога чатов этого владельца.
        res.Entries[2].SourceName.Should().Be("Разговор о сборке");
        res.Entries[2].Classes.Should().Equal("session×append");

        // Пакет, к которому привёл класс, — тот, что назван в исходной карточке.
        res.Entries[2].Packages.Should().BeEmpty("строка apply не называет id: он выводится из карточки");

        // Фильтры сужают окно, а не ищут за его краем.
        Read(Any() with { Section = "common" }).Entries.Select(e => e.Section).Should().Equal("common", "common");
        Read(Any() with { Origin = "note" }).Entries.Should().HaveCount(1);
        Read(Any() with { Origin = "note" }).Entries[0].Source.Should().Be("note:n-1");
        Read(Any() with { Class = "file×link" }).Entries.Should().HaveCount(1);
        Read(Any() with { Class = "session×append" }).Entries.Should().HaveCount(1);
        Read(Any() with { Section = "personal", Origin = "file" }).Entries.Should().BeEmpty();
    }

    // Строка retract называет id откатанных пакетов прямо (мера 5): они и остаются в записи,
    // одного статуса на несколько нет — их видно по их id.
    [Fact]
    public void СтрокаRetract_НазываетПакеты_ОбщегоСтатусаНет_КлассыСобранные()
    {
        var p1 = NewPackage("common", WikiPackage.StatusReverted, WikiGate.OriginSession, "session:chat-9",
            Op(WikiGate.OriginSession, WikiPackage.OpAppend, WikiPackage.DecisionAuto, "Общее/A.md"));
        var p2 = NewPackage("common", WikiPackage.StatusReverted, WikiGate.OriginSession, "session:chat-9",
            Op(WikiGate.OriginSession, WikiPackage.OpSupersede, WikiPackage.DecisionInbox, "Общее/B.md"));

        Log("apply", "common", ["Общее/A.md"], "session:chat-9", "keeper", WikiPackage.DecisionAuto);
        Log("retract", "common", ["Общее/A.md", "Общее/B.md"], "session:chat-9", "user", null, [p1, p2]);

        var res = Read(Any());
        res.Entries.Should().HaveCount(2);
        var retract = res.Entries[0];
        retract.Op.Should().Be("retract");
        retract.Packages.Should().Equal(p1, p2);
        retract.Status.Should().BeNull("несколько пакетов — одного статуса у записи нет");
        retract.Classes.Should().Equal("session×append", "session×supersede");
        // Политики у строки нет — она берётся по пакетам: хоть одна пришла из «Входящих».
        retract.Policy.Should().Be(WikiPackage.DecisionInbox);

        // Строка применения того же источника — своего пакета не теряет: он единственный
        // созданный не позже неё.
        res.Entries[1].Op.Should().Be("apply");
        res.Entries[1].Status.Should().Be(WikiPackage.StatusReverted);
        res.Entries[1].Policy.Should().Be(WikiPackage.DecisionAuto);
    }

    // ─── 2. Имя источника: только своё, иначе — без имени, не отказ ─────────

    [Fact]
    public void ИмяЧата_ТолькоСвоего_ЧужойИНесуществующий_БезИмени()
    {
        _sessions.Known["chat-own"] = new Session { Id = "chat-own", OwnerId = User, Name = "Свой разговор" };
        _sessions.Known["chat-other"] = new Session { Id = "chat-other", OwnerId = Foreign, Name = "Чужой разговор" };
        // Имя недоверенное: переносы и длинные — в выдаче одна строка до 120 символов.
        _sessions.Known["chat-long"] = new Session
        {
            Id = "chat-long",
            OwnerId = User,
            Name = "Длинное имя\n" + new string('м', 200),
        };

        var note = _notes.Create(User, new CreateNoteRequest(Title: "Заголовок заметки", Content: "текст", Source: null));

        Log("apply", "common", ["Общее/A.md"], "session:chat-own", "keeper", WikiPackage.DecisionAuto);
        Log("apply", "common", ["Общее/B.md"], "session:chat-other", "keeper", WikiPackage.DecisionAuto);
        Log("apply", "common", ["Общее/C.md"], "session:no-such", "keeper", WikiPackage.DecisionAuto);
        Log("apply", "common", ["Общее/D.md"], $"note:{note.Id}", "keeper", WikiPackage.DecisionAuto);
        Log("apply", "common", ["Общее/E.md"], "session:chat-long", "keeper", WikiPackage.DecisionAuto);
        Log("apply", "common", ["Общее/F.md"], "url:example.com", "keeper", WikiPackage.DecisionAuto);

        var bySource = Read(Any()).Entries.ToDictionary(e => e.Source!, e => e.SourceName);

        bySource["session:chat-own"].Should().Be("Свой разговор");
        // Чужой чат и несуществующий — один исход: имени нет, и по нему нельзя назвать,
        // существует ли этот чат или он чужой (мера 5).
        bySource["session:chat-other"].Should().BeNull();
        bySource["session:no-such"].Should().BeNull();
        bySource["note:" + note.Id].Should().Be("Заголовок заметки");
        bySource["url:example.com"].Should().BeNull("имени этого origin здесь взять нечем");

        var longName = bySource["session:chat-long"];
        longName.Should().NotBeNull();
        longName!.Length.Should().Be(WikiJournalReader.MaxNameLength);
        longName.Should().NotContain("\n");
        longName.Should().StartWith("Длинное имя мммм");
    }

    // ─── 3. Битая строка и большой файл: читается хвост, не весь файл ───────

    [Fact]
    public void БольшойФайлИБитыеСтроки_ЧитаетсяХвостTake_ИсключенияНет()
    {
        // 50 000 законных строк, затем битая строка и оборванная посреди записи (обрыв
        // записи — что реально оставляет в журнале падение процесса).
        var sb = new StringBuilder();
        for (var i = 1; i <= 50_000; i++)
            sb.Append(FakeLine(i)).Append('\n');
        sb.Append("{не json}\n");
        sb.Append("{\"ts\":\"2026-10-31T00:00:00+00:00\",\"op\":");
        WriteLog(sb.ToString());

        var res = Read(Any() with { Take = 200 });

        // Хвост ограничен тем, что попросили: 200 полных строк + оборванная, а не 50 001.
        res.Take.Should().Be(200);
        res.LinesRead.Should().Be(201);
        res.LinesRead.Should().BeLessThan(50_000, "весь журнал в память не читается");
        // Нечитаемое названо числом и не притворяется записью (тот же приём, что у нечитаемых
        // файлов пакета в «Входящих»).
        res.InvalidLines.Should().Be(2);
        res.Entries.Should().HaveCount(199);
        res.Entries[0].Op.Should().Be("relink", "последняя законная строка — новая сверху");
    }

    // ─── 4. Ручка: чужой раздел, выключенный флаг, плохой фильтр, окно ──────

    [Fact]
    public void Ручка_ЧужойРаздел_403_ВыключенныйФлаг_409_ЗначениеФильтраВнеПеречня_400() {
        var foreignProject = CreateProject("Чужой проект", Foreign);
        Log("apply", "common", ["Общее/A.md"], "session:chat-1", "keeper", WikiPackage.DecisionAuto);

        // Флаг выключен — 409 с причиной (тот же код, что у линта и настроек): журнал
        // такой вики не читается.
        AssertCode(Controller(new FlagOff()).Journal(null, null, null, null), 409, "wiki_llm_wiki_off");

        var ok = Controller().Journal(null, null, null, null);
        ((OkObjectResult)ok).Value.Should().BeAssignableTo<WikiJournalDto>().Which.Entries.Should().HaveCount(1);

        // Чужой раздел — тот же контракт, что у раздела и страницы: 403 «только своя вики».
        AssertCode(Controller().Journal("projects/" + foreignProject.Id, null, null, null), 403, "wiki_foreign");
        // Не ключ раздела и значение фильтра вне закрытого перечня — 400 с именем поля.
        AssertCode(Controller().Journal("Проекты/abc", null, null, null), 400, "wiki_section_key");
        AssertCode(Controller().Journal(null, "chat", null, null), 400, "wiki_origin_key");
        AssertCode(Controller().Journal(null, "session", "session", null), 400, "wiki_class_key");

        // Без токена — 401 (контракт), а не NRE на claim.
        ((IStatusCodeActionResult)AnonymousController().Journal(null, null, null, null)).StatusCode.Should().Be(401);
    }

    // Ручка отдаёт то же окно, что служба, — с именем источника и полем окна.
    [Fact]
    public void Ручка_ОтдаётЗаписиНовыеСверху_СИменемИсточникаОКлассом()
    {
        _sessions.Known["chat-1"] = new Session { Id = "chat-1", OwnerId = User, Name = "Разговор о сборке" };
        Log("apply", "common", ["Общее/A.md"], "session:chat-1", "keeper", WikiPackage.DecisionAuto);
        Log("retract", "common", ["Общее/A.md"], "session:chat-1", "user");

        var dto = ((OkObjectResult)Controller().Journal("common", null, null, 50)).Value
            .Should().BeAssignableTo<WikiJournalDto>().Which;
        dto.Take.Should().Be(50);
        dto.Entries.Should().HaveCount(2);
        dto.Entries[0].Op.Should().Be("retract");
        dto.Entries[0].SourceName.Should().Be("Разговор о сборке");
        dto.Entries[0].Origin.Should().Be("session");
        dto.Entries[1].Op.Should().Be("apply");
        dto.Entries[1].Policy.Should().Be("auto");

        // Фильтр по разделу — по ключу, а не по пути.
        ((OkObjectResult)Controller().Journal("personal", null, null, null)).Value
            .Should().BeAssignableTo<WikiJournalDto>().Which.Entries.Should().BeEmpty();
    }

    // ─── 5. take выше потолка урезается, а не отказ ─────────────────────────

    [Fact]
    public void TakeВышеПотолка_УрезаетсяДоMaxTake()
    {
        WikiJournalReader.ClampTake(null).Should().Be(WikiJournalReader.DefaultTake);
        WikiJournalReader.ClampTake(50).Should().Be(50);
        WikiJournalReader.ClampTake(0).Should().Be(1);
        WikiJournalReader.ClampTake(-7).Should().Be(1);
        WikiJournalReader.ClampTake(5_000).Should().Be(WikiJournalReader.MaxTake);

        var sb = new StringBuilder();
        for (var i = 1; i <= 1_200; i++)
            sb.Append(FakeLine(i)).Append('\n');
        WriteLog(sb.ToString());

        var res = Read(Any() with { Take = 5_000 });
        res.Take.Should().Be(WikiJournalReader.MaxTake, "окно над потолком — обрезка, а не отказ");
        res.LinesRead.Should().Be(WikiJournalReader.MaxTake);
        res.Entries.Should().HaveCount(WikiJournalReader.MaxTake);
    }

    // ─── Построение ─────────────────────────────────────────────────────────

    private string Root() => WikiPaths.Root(_dir, User);
    private string LogPath() => Path.Combine(WikiPaths.Keeper(_dir, User), "log.jsonl");

    // Журнал пишется на тот же путь, что и у Хранителя, — каталог создаётся заранее:
    // большой файл в этих тестах ставится прямо, а не построчным дописыванием.
    private void WriteLog(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath())!);
        File.WriteAllText(LogPath(), text, new UTF8Encoding(false));
    }

    private WikiJournalReader.Result Read(WikiJournalReader.Query q) => _reader.Read(User, q);

    /// <summary>Запрос без фильтров и со своим окном.</summary>
    private static WikiJournalReader.Query Any() => new(null, null, null, null);

    private void Log(string op, string section, IReadOnlyList<string> pages, string? source, string actor,
        string? policy = null, IReadOnlyList<string>? packages = null) =>
        WikiManifest.AppendLog(Root(), op, section, pages, source, actor, policy, packages);

    // Строка той же формы, что пишет журнал, — для наполнения большого файла.
    private static string FakeLine(int i) =>
        $$"""{"ts":"2026-10-01T00:{{(i / 60) % 60:D2}}:{{i % 60:D2}}+00:00","op":"relink","section":"common","pages":[],"source":null,"actor":"keeper","policy":null,"packages":null}""";

    private Project CreateProject(string name, string owner)
    {
        var repo = Path.Combine(_dir, "repo", owner, name);
        Directory.CreateDirectory(repo);
        return _projects.Create(name, repo, owner, owner);
    }

    // Пакет в своём корне, доведённый до статуса разрешёнными переходами стора.
    private string NewPackage(string section, string status, string origin, string refPart,
        params WikiPackage.Operation[] ops)
    {
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = section,
            Sources = [new WikiPackage.SourceRef(origin, refPart)],
            Operations = ops,
            Status = WikiPackage.StatusChecking,
        };
        WikiPackageStore.Create(Root(), pkg).Ok.Should().BeTrue();
        var steps = status switch
        {
            WikiPackage.StatusChecking => Array.Empty<string>(),
            WikiPackage.StatusApplied => new[] { WikiPackage.StatusProposed, WikiPackage.StatusApplying, WikiPackage.StatusApplied },
            WikiPackage.StatusReverted => new[] { WikiPackage.StatusProposed, WikiPackage.StatusApplying, WikiPackage.StatusApplied, WikiPackage.StatusReverted },
            _ => new[] { status },
        };
        foreach (var step in steps)
            WikiPackageStore.Transition(Root(), pkg.Id, step).Ok.Should().BeTrue($"{status}: {step}");
        return pkg.Id;
    }

    // Класс ставится из источника пакета и op — как его ставит код (своей строки класса
    // ShapeErrors не допускает).
    private static WikiPackage.Operation Op(string origin, string op, string decision, string page) => new()
    {
        Op = op,
        Page = page,
        Text = "утверждение из источника",
        Target = "прежнее утверждение",
        Class = WikiPackage.ClassKey(origin, op),
        Decision = decision,
        Reason = decision == WikiPackage.DecisionInbox ? "замещает действующее утверждение" : null,
        BaseHashes = new Dictionary<string, string> { [page] = Hash },
    };

    private static void AssertCode(IActionResult result, int status, string code)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>($"{status} с телом отказа").Which;
        obj.StatusCode.Should().Be(status);
        JsonDocument.Parse(JsonSerializer.Serialize(obj.Value)).RootElement
            .GetProperty("code").GetString().Should().Be(code);
    }

    private WikiController Controller(IFeatureFlagGate? flags = null)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, User)], "test"));
        var keeper = new WikiKeeper(_notes, _config);
        var lint = new WikiLintService(_notes, keeper, new NoSpheres(), _config, NullLogger<WikiLintService>.Instance);
        var (capture, ingest) = IngestGraph(keeper);
        return new WikiController(keeper, _projects, new NoSpheres(), lint, _reader, flags ?? new FlagOn(), _config, capture, ingest, null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private WikiController AnonymousController()
    {
        var http = new DefaultHttpContext(); // User без идентичности — путь боя без токена
        var keeper = new WikiKeeper(_notes, _config);
        var lint = new WikiLintService(_notes, keeper, new NoSpheres(), _config, NullLogger<WikiLintService>.Instance);
        var (capture, ingest) = IngestGraph(keeper);
        return new WikiController(keeper, _projects, new NoSpheres(), lint, _reader, new FlagOn(), _config, capture, ingest, null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    // S7b0b: конструктор контроллера держит захват и очередь разбора. Эти тесты их не зовут
    // (свои сценарии разбора — в WikiIngestNoteTests); экземпляры нужны, чтобы резолвился
    // конструктор. Модель — отказная: если что-то позвал — пусть будет видно.
    private (WikiSourceCapture, WikiIngestService) IngestGraph(WikiKeeper keeper)
    {
        var apply = new WikiApplyService(_notes, keeper, new NoSpheres(), _config);
        return (
            new WikiSourceCapture(_notes, _projects, _sessions, _config, keeper),
            new WikiIngestService(new NoopCheapRunner(), new NoSpheres(), apply, _config));
    }

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

    private sealed class FlagOn : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }

    private sealed class FlagOff : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => false;
    }

    private sealed class NoSpheres : ISphereDirectory
    {
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public bool Enabled(string ownerId) => false;
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
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

    // ─── B4b (ADR-038, предохранитель 4): overrodeHuman читается и даёт фильтр «ИИ поменял моё» ───

    [Fact]
    public void OverrodeHuman_ПисьмоЧтениеИФильтр()
    {
        // Мутация «читатель не видит поле» (убрать разбор overrodeHuman в ParseLine): Be(true) и
        // фильтр краснеют. Мутация «фильтр не фильтрует» — HaveCount(1) краснеет.
        WikiManifest.AppendLog(Root(), "apply", "common", ["Общее/А.md"], "session:c1", "keeper", "auto", overrodeHuman: true);
        WikiManifest.AppendLog(Root(), "apply", "common", ["Общее/Б.md"], "session:c2", "keeper", "auto");

        var all = Read(Any());
        all.Entries.Should().HaveCount(2);
        all.Entries.Single(e => e.Pages.Contains("Общее/А.md")).OverrodeHuman.Should().BeTrue();
        all.Entries.Single(e => e.Pages.Contains("Общее/Б.md")).OverrodeHuman.Should().BeFalse("поля нет — признака нет");

        var filtered = Read(new WikiJournalReader.Query(null, null, null, null, OverrodeHuman: true));
        filtered.Entries.Should().ContainSingle().Which.Pages.Should().Equal("Общее/А.md");
    }

    [Fact]
    public void OverrodeHuman_НеTrue_ПризнакомНеСчитается()
    {
        WriteLog("""{"ts":"2026-10-01T00:00:00+00:00","op":"apply","section":"common","pages":["Общее/А.md"],"source":"session:c1","actor":"keeper","policy":"auto","packages":null,"overrodeHuman":false}""" + "\n");

        Read(Any()).Entries.Single().OverrodeHuman.Should().BeFalse();
    }

    // ─── B6 (находки В2/В5): REST журнала — фильтр overrodeHuman и вид действия ───

    private static WikiPackage.Operation HumanTargetOp(string op, string decision, string page) =>
        Op(WikiGate.OriginSession, op, decision, page) with
        {
            Class = WikiPackage.ClassKey(WikiGate.OriginSession, op, humanTarget: true),
        };

    [Fact]
    public void Ручка_OverrodeHuman_ФильтрПолеИВидДействия_СтарыеСтрокиБезПризнака()
    {
        // Мутации (по одной): убрать overrodeHuman из Query в контроллере — фильтр не сужает
        // (HaveCount(2) краснеет); подменить KindDisputed на KindReplaced — "disputed" краснеет;
        // убрать OverrodeKind из DTO-проекции — "replaced" краснеет.
        NewPackage("common", WikiPackage.StatusApplied, WikiGate.OriginSession, "session:rep",
            HumanTargetOp(WikiPackage.OpSupersede, WikiPackage.DecisionAuto, "Общее/А.md"));
        NewPackage("common", WikiPackage.StatusApplied, WikiGate.OriginSession, "session:dis",
            HumanTargetOp(WikiPackage.OpConflict, WikiPackage.DecisionAuto, "Общее/Б.md"));
        NewPackage("common", WikiPackage.StatusApplied, WikiGate.OriginSession, "session:plain",
            Op(WikiGate.OriginSession, WikiPackage.OpAppend, WikiPackage.DecisionAuto, "Общее/В.md"));
        WikiManifest.AppendLog(Root(), "apply", "common", ["Общее/А.md"], "session:rep", "keeper", "auto", overrodeHuman: true);
        WikiManifest.AppendLog(Root(), "apply", "common", ["Общее/Б.md"], "session:dis", "keeper", "auto", overrodeHuman: true);
        WikiManifest.AppendLog(Root(), "apply", "common", ["Общее/В.md"], "session:plain", "keeper", "auto");
        // Старая строка без поля overrodeHuman вовсе.
        Log("apply", "common", ["Общее/Г.md"], null, "keeper", WikiPackage.DecisionAuto);

        var all = (WikiJournalDto)((OkObjectResult)Controller().Journal(null, null, null, null)).Value!;
        all.Entries.Should().HaveCount(4, "без фильтра виден весь журнал");
        var a = all.Entries.Single(e => e.Pages.Contains("Общее/А.md"));
        (a.OverrodeHuman, a.OverrodeKind).Should().Be((true, "replaced"));
        var b = all.Entries.Single(e => e.Pages.Contains("Общее/Б.md"));
        (b.OverrodeHuman, b.OverrodeKind).Should().Be((true, "disputed"));
        var v = all.Entries.Single(e => e.Pages.Contains("Общее/В.md"));
        (v.OverrodeHuman, v.OverrodeKind).Should().Be((false, null));
        var g = all.Entries.Single(e => e.Pages.Contains("Общее/Г.md"));
        (g.OverrodeHuman, g.OverrodeKind).Should().Be((false, null), "старая строка: признака нет, ничего не падает");

        var filtered = (WikiJournalDto)((OkObjectResult)Controller().Journal(null, null, null, null, overrodeHuman: true)).Value!;
        filtered.Entries.Should().HaveCount(2);
        filtered.Entries.Select(e => e.OverrodeKind).Should().BeEquivalentTo(["replaced", "disputed"]);
    }

    [Fact]
    public void Ручка_OverrodeHuman_РешилЧеловекИзВходящих_ВидаНет()
    {
        // Операция `…@human`, принятая человеком (decision=inbox), — не «ИИ поменял»: вид не ставится.
        NewPackage("common", WikiPackage.StatusApplied, WikiGate.OriginSession, "session:inb",
            HumanTargetOp(WikiPackage.OpSupersede, WikiPackage.DecisionInbox, "Общее/А.md"));
        WikiManifest.AppendLog(Root(), "apply", "common", ["Общее/А.md"], "session:inb", "keeper", "inbox", overrodeHuman: true);

        var dto = (WikiJournalDto)((OkObjectResult)Controller().Journal(null, null, null, null)).Value!;
        dto.Entries.Single().OverrodeKind.Should().BeNull();
    }
}
