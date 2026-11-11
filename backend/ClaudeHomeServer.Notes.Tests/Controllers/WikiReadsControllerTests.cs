using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace ClaudeHomeServer.Tests.Controllers;

// R4 (ADR-039, §7): открытие страницы вики человеком через REST — учёт Human в keeper/reads.json.
// Два пути: GET /api/wiki/page (ключ — параметр page) и GET /api/notes/{id} для источника wiki
// (ключ — note.Path, он же PageKey под pages/). Не считаются: svc-токен (его GET отвечает как
// раньше), 400/404, обычная заметка. Сбой учёта (нечитаемый файл стора → RecordMany=false) не
// меняет ответ. Два параллельных GET одной страницы дают Human=2: сериализация — семафор файла
// в сторе (R1), здесь проверяется итог, а не замок.
// Контроллеры строятся напрямую (контракт действия, не транспорт) — как в WikiControllerPagesTests:
// фабрики хоста нет, метка Host не нужна.
public class WikiReadsControllerTests : IDisposable
{
    private const string User = "u1";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly WikiJournalReader _journal;
    private readonly string _key;
    private readonly string _pageKey;

    public WikiReadsControllerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_reads_ctl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "notes", User)); // личный vault: для обычной заметки
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, flags: new Flags(true));
        _keeper = new WikiKeeper(_notes, _config);
        // GET page читает журнал (overrides): имени источников там не нужно — каталог сессий не зовётся.
        _journal = new WikiJournalReader(_notes, null!, _config);

        var root = Path.Combine(_dir, "repo", User, "Проект");
        Directory.CreateDirectory(root);
        var project = _projects.Create("Проект", root, User, User);
        _key = "projects/" + project.Id;
        _pageKey = $"Проекты/{project.Id}/Знание.md";
        SeedWiki();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void SeedWiki()
    {
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
        var page = """
            ---
            type: topic
            aliases: []
            owns: []
            sources: []
            status: active
            ---

            # Знание

            ## Сводка
            Сервер работает на порту пять тысяч.

            ## Хронология

            ## Противоречия

            ## История
            """;
        var r = _keeper.WritePageAsync(User, _key, _pageKey, page).GetAwaiter().GetResult();
        r.Status.Should().Be(WikiOpStatus.Applied, r.Reason);
    }

    // ─── 1. GET /api/wiki/page: человек — Human; svc, 400, 404 — нет ───────

    [Fact]
    public void ГетСтраницыЧеловеком_Human1_ПрочиеПутиНулевые()
    {
        var res = PageController().Page(_key, _pageKey);
        ((IStatusCodeActionResult)res).StatusCode.Should().Be(200);

        var snap = Reads();
        snap.Valid.Should().BeTrue();
        var p = snap.Pages[_pageKey];
        p.Human.Should().Be(1);
        p.Recall.Should().Be(0);
        p.Query.Should().Be(0);
        p.Read.Should().Be(0);
        p.LastRead.Should().NotBeNull();
    }

    [Fact]
    public void ГетСтраницыСервиснымТокеном_200_БезЗаписи()
    {
        // Мутация (критерий c2): снять «!IsServiceToken» в WikiController.Page — этот тест красный:
        // svc-чтение попало бы в стор. Сам GET при этом отвечает как раньше — 200.
        var res = PageController(serviceToken: true).Page(_key, _pageKey);
        ((IStatusCodeActionResult)res).StatusCode.Should().Be(200, "svc — путь чтения, но не человек: ответ тот же");

        Reads().Pages.Should().BeEmpty("svc не доходит до учёта");
    }

    [Fact]
    public void ГетОтсутствующейСтраницы_404_БезЗаписи()
    {
        var missing = _pageKey.Replace("Знание.md", "Нет.md");
        var res = PageController().Page(_key, missing);
        ((IStatusCodeActionResult)res).StatusCode.Should().Be(404);

        Reads().Pages.Should().BeEmpty();
    }

    [Fact]
    public void ГетСтраницыНеИзЭтогоРаздела_400_БезЗаписи()
    {
        var res = PageController().Page(_key, "Личное/Чужая.md");
        ((IStatusCodeActionResult)res).StatusCode.Should().Be(400);

        Reads().Pages.Should().BeEmpty();
    }

    // ─── 2. GET /api/notes/{id}: страница вики — Human; обычная заметка и svc — нет ───

    [Fact]
    public void ГетЗаметкиСтраницыВики_Human1_ИОтветБезИзменений()
    {
        var id = IdOfPage();
        var ctl = NotesController();
        var ok = ctl.Get(id).Result.Should().BeAssignableTo<OkObjectResult>().Which;
        ok.StatusCode.Should().Be(200);
        ok.Value.Should().BeAssignableTo<NoteDetail>().Which.Path.Should().Be(_pageKey,
            "ключ учёта — тот же PageKey, что отдаётся в ответе");
        ctl.Response.Headers.ETag.ToString().Should().NotBeEmpty("ответ ручки не изменился: ETag на месте");

        var snap = Reads();
        snap.Valid.Should().BeTrue();
        snap.Pages[_pageKey].Human.Should().Be(1);
    }

    [Fact]
    public void ГетОбычнойЗаметки_200_БезЗаписи()
    {
        // Мутация (критерий c2): снять «note.Source == NotesService.WikiKey» в NotesController.Get —
        // красный только этот тест. Заметка намеренно лежит в папке «Личное» личного vault: её
        // Path — валидный PageKey (тот же префикс, что у раздела вики), и без проверки источника
        // она записалась бы в стор чтений. Заметка в корне vault этот тест не ловит: её Path
        // отвергает сам стор (IsValidPageKey), и мутация осталась бы зелёной.
        var note = _notes.Create(User, new CreateNoteRequest(
            Title: "Обычная", Content: "обычная заметка личного vault.", Source: "personal", Folder: "Личное"));
        var res = NotesController().Get(note.Id);
        res.Result.Should().BeAssignableTo<OkObjectResult>().Which.StatusCode.Should().Be(200);

        Reads().Pages.Should().BeEmpty("обычная заметка — не страница вики, даже с путём под раздел");
    }

    [Fact]
    public void ГетЗаметкиВикиСервиснымТокеном_200_БезЗаписи()
    {
        var res = NotesController(serviceToken: true).Get(IdOfPage());
        res.Result.Should().BeAssignableTo<OkObjectResult>().Which.StatusCode.Should().Be(200);

        Reads().Pages.Should().BeEmpty();
    }

    // ─── 3. Конкурентность и отказ учёта ────────────────────────────────────

    [Fact]
    public async Task ДваПараллельныхГетаОднойСтраницы_Human2()
    {
        var results = await Task.WhenAll(
            Task.Run(() => PageController().Page(_key, _pageKey)),
            Task.Run(() => PageController().Page(_key, _pageKey)));
        results.Select(r => ((IStatusCodeActionResult)r).StatusCode).Should().Equal(200, 200);

        // Ни одно чтение не потерялось: read-modify-write стора — под семафором файла (R1).
        Reads().Pages[_pageKey].Human.Should().Be(2);
    }

    [Fact]
    public void НечитаемыйФайлСтора_ГетОтвечает200_ФайлНеТронут()
    {
        // Сбой учёта не меняет ответ: стор невалиден → RecordMany отказывает (fail-closed R1),
        // страница читается как обычно, испорченный файл не перезаписывается.
        var file = Path.Combine(WikiPaths.Keeper(_dir, User), "reads.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ не json");
        var before = File.ReadAllText(file);

        var res = PageController().Page(_key, _pageKey);
        ((IStatusCodeActionResult)res).StatusCode.Should().Be(200);

        File.ReadAllText(file).Should().Be(before);
    }

    // ─── Помощники ──────────────────────────────────────────────────────────

    private WikiReadsStore.Snapshot Reads() => WikiReadsStore.Read(WikiPaths.Root(_dir, User));

    // Id страницы вики — из графа заметок тем же путём, что видит UI (источник wiki появляется
    // при заведённой вики и включённом флаге — SourcesFor).
    private string IdOfPage() =>
        _notes.GetSummaries(User, null, null).Single(n => n.Source == "wiki" && n.Title == "Знание").Id;

    private static ControllerContext Ctx(bool serviceToken = false)
    {
        var http = new DefaultHttpContext();
        var claims = new List<Claim> { new("sub", User) };
        if (serviceToken) claims.Add(new("typ", "svc"));
        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return new ControllerContext { HttpContext = http };
    }

    private WikiController PageController(bool serviceToken = false) =>
        new(_keeper, _projects, new NoSpheres(), null!, _journal, new Flags(true), _config,
            null!, null!, null!, null!)
        {
            ControllerContext = Ctx(serviceToken),
        };

    private NotesController NotesController(bool serviceToken = false) =>
        new(_notes, null!, null!, null!, null!, _config)
        {
            ControllerContext = Ctx(serviceToken),
        };

    // Один флаг: llm-wiki; остальные ключи выключены.
    private sealed class Flags(bool llmWiki) : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki && llmWiki;
    }

    private sealed class NoSpheres : ISphereDirectory
    {
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public bool Enabled(string ownerId) => false;
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }
}
