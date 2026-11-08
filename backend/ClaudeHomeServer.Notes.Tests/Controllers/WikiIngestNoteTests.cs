using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// S7b0b: POST api/wiki/ingest/note — «Разобрать заметку в базу» тем же кодом, что
/// инструмент wiki_ingest (WikiCardIngest): захват → карточка → заявка. Проверяется
/// контракт ручки: своя заметка доходит до очереди разбора (200 с outcome), чужая —
/// 404 not_found (владельца проверяет CaptureNote, тот же путь, что у MCP), 21-й открытый
/// пакет — 409 с кодом ДО захвата; две одновременные заявки одной
/// заметки — без 500 и без лишних пакетов (дедуп — внутри IngestAsync).
///
/// С LI-2 вика заявителя заводится до заявки: прежнего 409 wiki_not_initialized этой ручки
/// нет (svc 403 и выключенный флаг 409 — раньше заведения, валидация тела — раньше заведения).
///
/// Модель подменена штатным «нечего добавить»: здесь проверяется контракт входа, а не
/// качество разбора (оно — в тестах WikiIngestService). Образец построения —
/// WikiControllerTests: temp-дир + боевые UserStore/ProjectManager + контроллер напрямую
/// (тестируется контракт действия, не транспорт).
/// </summary>
[Trait("Category", "Host")]
public class WikiIngestNoteTests : IDisposable
{
    private const string User = "u1";
    private const string Foreign = "u2";

    private readonly string _dir; // dataDir
    private readonly string _roots; // рабочие каталоги проектов — вне {data}
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly WikiLintService _lint;
    private readonly WikiJournalReader _journal;
    private readonly WikiSourceCapture _capture;
    private readonly WikiIngestService _ingest;
    private readonly WikiApplyService _apply;
    private readonly WikiNoteTransfer _transfer;
    private readonly NoSpheres _spheres = new();
    private readonly NoSessions _sessions = new();

    public WikiIngestNoteTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_ingest_note_" + Guid.NewGuid().ToString("N"));
        // Корни проектов — вне {data}: {data} — служебное пространство сервера (тот же
        // порядок, что в WikiSourceCaptureTests).
        _roots = Path.Combine(Path.GetTempPath(), "wiki_ingest_note_roots_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_roots);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance);
        _keeper = new WikiKeeper(_notes, _config);
        // Служба линта и читатель журнала этим тестам не нужны (их ручки они не зовут):
        // экземпляры держатся потому, что их держит конструктор контроллера.
        _lint = new WikiLintService(_notes, _keeper, _spheres, _config, NullLogger<WikiLintService>.Instance);
        _journal = new WikiJournalReader(_notes, _sessions, _config);
        _apply = new WikiApplyService(_notes, _keeper, _spheres, _config);
        _ingest = new WikiIngestService(new NothingModel(), _spheres, _apply, _config);
        _transfer = new WikiNoteTransfer(_notes, _apply, _projects);
        _capture = new WikiSourceCapture(_notes, _projects, _sessions, _config, _keeper);
        // Разбор работает только с заведённой викой (тот же отказ, что у инструмента),
        // поэтому вика владельца заведена: сценарии ниже проверяют путь дальше этого порога.
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        foreach (var d in new[] { _dir, _roots })
        {
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
        }
    }

    private string Root => WikiPaths.Root(_dir, User);

    private Project NewProject(string name, string owner = User) =>
        _projects.Create(name, Path.Combine(_roots, "proj_" + name), owner, owner, createDirectory: true);

    // ─── своя заметка доходит до очереди разбора ───────────────────────────

    [Fact]
    public async Task IngestNote_OwnNote_ReachesIngest_NothingNew()
    {
        var project = NewProject("П1");
        var note = _notes.Create(User, new CreateNoteRequest(
            "Заметка встречи", "Решили открывать встречу в понедельник.", project.Id));

        var res = await Controller().IngestNote(new WikiIngestNoteRequest(note.Id));

        ((IStatusCodeActionResult)res).StatusCode.Should().Be(200);
        JsonOf(res).GetProperty("outcome").GetString().Should().Be(WikiIngestService.OutcomeNothingNew,
            "заявка дошла до очереди разбора: исход ставит она, а не ручка");
        // Карточку поставил захват — она в разделе дома заметки (тот же путь, что у MCP).
        var sectionDir = Path.Combine(WikiPaths.Pages(_dir, User), "Проекты", project.Id);
        Directory.EnumerateFiles(sectionDir, "*.md", SearchOption.AllDirectories)
            .Should().NotBeEmpty("дом и карточка — с захвата, не с заявки");
    }

    // ─── чужая заметка: 404, ни карточки, ни заявки ────────────────────────

    [Fact]
    public async Task IngestNote_ForeignNote_404()
    {
        var alien = _notes.Create(Foreign, new CreateNoteRequest("Чужая мысль", "тело чужого владельца"));

        var res = await Controller().IngestNote(new WikiIngestNoteRequest(alien.Id));

        AssertCode(res, 404, "not_found");
        JsonOf(res).GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace(
            "отказ называет причину, а не пустой код");
        // Инвариант «только своя»: чужая заметка не стала источником этой вики — ни пакета.
        WikiPackageStore.CountOpen(Root).Should().Be(0);
    }

    // ─── LI-2: вика заявителя заводится самой заявкой ───────────────────────

    [Fact]
    public async Task IngestNoteНезаведённойВикиЗаводитЕёИЗаявкаИдётДальше()
    {
        // Мутация (критерий c2): вернуть прежний 409 wiki_not_initialized в IngestNote — u3
        // остаётся без pages/_schema.md, заявка отвечает 409: тест красный.
        var project = NewProject("П3", "u3");
        var note = _notes.Create("u3", new CreateNoteRequest("Мысль u3", "Решили: нарезку делает код.", project.Id));

        var res = await Controller("u3").IngestNote(new WikiIngestNoteRequest(note.Id));

        WikiPaths.IsInitialized(_dir, "u3").Should().BeTrue(
            "заявка владельца — действие человека: его вика создаётся сама, кнопки «Завести» нет");
        ((IStatusCodeActionResult)res).StatusCode.Should().Be(200, "отказа по незаведённой вике этой ручке нет");
        JsonOf(res).GetProperty("outcome").GetString().Should().Be(WikiIngestService.OutcomeNothingNew,
            "заявка дошла до очереди разбора");
    }

    [Fact]
    public async Task IngestNoteНетТакойЗаметки_404_ВикаВсёРавноЗаведена()
    {
        // Порядок точек разреза: заведение — после валидации тела и до сервиса, т.е. до
        // обращения к заметке. Отказ несуществующей заметки остаётся своим 404; вика при этом
        // заведена — пустая заведённая вика после отказанного действия безвредна (разрез).
        var res = await Controller("u3").IngestNote(new WikiIngestNoteRequest("любой-id"));

        AssertCode(res, 404, "not_found");
        WikiPaths.IsInitialized(_dir, "u3").Should().BeTrue();
    }

    [Fact]
    public async Task IngestNote_СервисныйТокен_403_ВикиНеЗаведены()
    {
        // LI-2: svc не доходит ни до записей, ни до заведения — агент не заводит вику.
        var res = await Controller("u3", svc: true).IngestNote(new WikiIngestNoteRequest("любой-id"));

        ((IStatusCodeActionResult)res).StatusCode.Should().Be(403);
        WikiPaths.IsInitialized(_dir, "u3").Should().BeFalse();
        Directory.Exists(WikiPaths.Pages(_dir, "u3")).Should().BeFalse("ни байта в pages/");
    }

    [Fact]
    public async Task IngestNoteПустоеТелоИлиПустойId_400_ВикиНеЗаведены()
    {
        // Валидация тела — раньше заведения (порядок точек не менялся): «менять нечего» не
        // создаёт вику.
        AssertCode(await Controller("u3").IngestNote(null), 400, "wiki_note_id");
        AssertCode(await Controller("u3").IngestNote(new WikiIngestNoteRequest("")), 400, "wiki_note_id");
        WikiPaths.IsInitialized(_dir, "u3").Should().BeFalse();
    }

    [Fact]
    public async Task IngestNote_FlagOff_409_ЗаявкаНеСоздана()
    {
        // Флаг выключен: отказ раньше проверки заведённости (u3 без вики) и id заметки;
        // мутация «убрать проверку флага» даёт wiki_not_initialized — тест красный.
        AssertCode(await Controller("u3", flagsOn: false).IngestNote(new WikiIngestNoteRequest("любой-id")), 409, "wiki_llm_wiki_off");
        // LI-2: при выключенной функции вики не появляется — ни одной точки заведения.
        WikiPaths.IsInitialized(_dir, "u3").Should().BeFalse("отказ по флагу раньше заведения");
        AssertCode(await Controller(flagsOn: false).IngestNote(null), 409, "wiki_llm_wiki_off");

        // Реальная заметка у заведённой вики (u1): флаг выключен — заявка не принята,
        // ни пакета, ни карточки в разделе проекта.
        var project = NewProject("П1");
        var note = _notes.Create(User, new CreateNoteRequest("Заметка при выключенном флаге", "текст", project.Id));
        AssertCode(await Controller(flagsOn: false).IngestNote(new WikiIngestNoteRequest(note.Id)), 409, "wiki_llm_wiki_off");
        WikiPackageStore.CountOpen(Root).Should().Be(0, "заявка не создана");
        Directory.Exists(Path.Combine(WikiPaths.Pages(_dir, User), "Проекты", project.Id))
            .Should().BeFalse("отказ по флагу не пишет карточку");
    }

    [Fact]
    public async Task IngestNote_PackagesLimit_409()
    {
        var project = NewProject("П1");
        var note = _notes.Create(User, new CreateNoteRequest("Заметка при лимите", "текст", project.Id));
        FillOpenPackages();

        var res = await Controller().IngestNote(new WikiIngestNoteRequest(note.Id));

        AssertCode(res, 409, "packages_limit");
        // 21-я заявка не ставится и не трогает диск: отказ ДО захвата (тот же порядок,
        // что у инструмента) — карточки этой заметки в её разделе нет.
        Directory.Exists(Path.Combine(WikiPaths.Pages(_dir, User), "Проекты", project.Id))
            .Should().BeFalse("отказ по лимиту не пишет карточку");
    }

    // ─── конкурентность: две заявки одной заметки ──────────────────────────

    [Fact]
    public async Task IngestNote_Parallel_NoServerError()
    {
        var project = NewProject("П1");
        var note = _notes.Create(User, new CreateNoteRequest("Одна и та же", "текст один раз", project.Id));

        var req = new WikiIngestNoteRequest(note.Id);
        var results = await Task.WhenAll(
            Task.Run(() => Controller().IngestNote(req)),
            Task.Run(() => Controller().IngestNote(req)));

        foreach (var res in results)
        {
            ((IStatusCodeActionResult)res).StatusCode.Should().Be(200,
                "две одновременные заявки одной заметки: ни 500, ни исключения наружу");
            JsonOf(res).GetProperty("outcome").GetString().Should().NotBeNullOrWhiteSpace();
        }
        // Пакетов не больше, чем даёт IngestAsync: модель здесь «нечего добавить» —
        // пакета нет ни у одной из двух заявок (двойной прогон того же источника — дедуп
        // внутри IngestAsync, он же).
        WikiPackageStore.CountOpen(Root).Should().Be(0);
    }

    // ─── Помощники ─────────────────────────────────────────────────────────

    // 20 открытых пакетов на всю вики (лимит ADR-035, решение 4): пакет рождается в
    // «checking», а он открытый (WikiPackage.IsOpen) — их ставит один Create.
    private void FillOpenPackages()
    {
        for (var i = 0; i < WikiPackage.MaxOpenPackages; i++)
        {
            var created = WikiPackageStore.Create(Root, new WikiPackage
            {
                Id = WikiPackageStore.NewId(),
                Section = WikiSections.Personal,
                Sources = [new WikiPackage.SourceRef(WikiGate.OriginNote, "note:" + i)],
                Operations =
                [
                    new WikiPackage.Operation
                    {
                        Op = WikiPackage.OpAppend,
                        Page = "Личное/Знание.md",
                        Text = "утверждение " + i,
                        Class = WikiPackage.ClassKey(WikiGate.OriginNote, WikiPackage.OpAppend),
                        Decision = WikiPackage.DecisionAuto,
                    },
                ],
                Status = WikiPackage.StatusChecking,
            });
            created.Ok.Should().BeTrue($"пакет для лимита обязан быть поставлен: {created.Reason ?? created.Code}");
        }
        WikiPackageStore.CountOpen(Root).Should().Be(WikiPackage.MaxOpenPackages);
    }

    private static JsonElement JsonOf(IActionResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(((ObjectResult)result).Value!)).RootElement;

    private static void AssertCode(IActionResult result, int status, string code)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>($"{status} с телом отказа").Which;
        obj.StatusCode.Should().Be(status);
        JsonDocument.Parse(JsonSerializer.Serialize(obj.Value)).RootElement
            .GetProperty("code").GetString().Should().Be(code);
    }

    private WikiController Controller(string user = User, bool flagsOn = true, bool svc = false)
    {
        var http = new DefaultHttpContext();
        var identity = new ClaimsIdentity([new Claim("sub", user)], "test");
        // Сервисный токен хода (typ=svc) — тот же claim-литерал, что выдаёт JwtService.
        if (svc) identity.AddClaim(new Claim("typ", "svc"));
        http.User = new ClaimsPrincipal(identity);
        return new WikiController(_keeper, _projects, _spheres, _lint, _journal, flagsOn ? new WikiFlagOn() : new WikiFlagOff(), _config, _capture, _ingest, _apply, _transfer)
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

    // Модель подменена: ответ — штатное «нечего добавить» (тот же контракт, что NothingLlm
    // в NotesToolsetWikiTests): заявка заканчивается nothing_new без пакета и без ремонта.
    private sealed class NothingModel : ICheapTextRunner
    {
        private static readonly string Nothing =
            $$"""{"outcome":"{{WikiKeeperPrompts.NothingToAdd}}","reason":"нет ничего нового"}""";

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(Nothing);

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult<string?>(Nothing);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(new OneShotResult(Nothing, new OneShotUsage(10, 0, 0, 5, 0.0, "fake"), 1));
    }

    // Сфер нет: домом заметки здесь выступает проект.
    private sealed class NoSpheres : ISphereDirectory
    {
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public bool Enabled(string ownerId) => false;
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }

    // Каталог чатов пуст: эти тесты не зовут ни захвата чата, ни чтения журнала.
    private sealed class NoSessions : ISessionDirectory
    {
        public Session? GetById(string id) => null;
        public IReadOnlyCollection<Session> GetAll() => [];
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
        public bool SetWikiMark(string sessionId, string? mark) => false;
    }
}
