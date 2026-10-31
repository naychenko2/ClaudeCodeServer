using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace ClaudeHomeServer.Tests.Services;

// S3-3: служба линта вики — ночное окно + запуск по кнопке. Проверяется поведение прогона:
// отказ одного раздела не роняет остальных и не выходит из службы (c1); прогон по всем
// разделам оставляет health и ровно одну строку метрик на раздел (c2); health чужого
// владельца — 403/404 (c3); кнопка и ночное окно не делают двойной запись (конкурентность).
// Окно (его часы) здесь не проверяется: прогон вызывается напрямую, как его зовёт REST.
// Образец построения — WikiControllerTests: temp-дир + боевые ProjectManager/NotesService +
// страница пишется Хранителем (тот же путь, что у применения).
public class WikiLintServiceTests : IDisposable
{
    private const string User = "u1";
    private readonly string _dir;
    private readonly string _pages;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly WikiLintService _sut;
    private readonly NoSpheres _spheres = new();

    public WikiLintServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_lint_service_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        // Флаг llm-wiki включён: иначе граф не несёт страниц вики и лентеру нечего видеть
        // (тот же приём, что в WikiLintTests/NotesWikiSourceTests).
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, flags: new WikiFlagGate());
        _keeper = new WikiKeeper(_notes, _config);
        _sut = new WikiLintService(_notes, _keeper, _spheres, _config, NullLogger<WikiLintService>.Instance);
        _pages = WikiPaths.Pages(_dir, User);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── c1. Битая шапка: служба жива, остальные разделы обработаны, строка о ней есть ──

    [Fact]
    public async Task БитаяШапка_НеРоняетПрогон_ОстальныеРазделыОбработаны_СтрокаОНейВЗдоровье()
    {
        var good = CreateProject("Хороший");
        var bad = CreateProject("Битый");
        await _keeper.EnsureInitializedAsync(User);
        await WriteGoodPage(good.Id, "Знание");

        // Страница с неразобранной шапкой — как заведена человеком мимо Хранителя.
        var badDir = Path.Combine(_pages, "Проекты", bad.Id);
        Directory.CreateDirectory(badDir);
        File.WriteAllText(Path.Combine(badDir, "Сбойная.md"), "---\ntype: unicorn\n---\n# Сбойная\n\nтекст.", new UTF8Encoding(false));

        var res = await _sut.TryRunOwnerAsync(User);

        res.Started.Should().BeTrue("исключение не вышел из прогона: служба жива");
        res.SectionsOk.Should().Be(1, "целый раздел обработан, несмотря на отказ соседнего");
        res.SectionsFailed.Should().Be(1);

        // Живой раздел получил обычный health.
        File.Exists(HealthOf(good.Id)).Should().BeTrue();
        File.ReadAllText(HealthOf(good.Id)).Should().Contain("Здоровье раздела");

        // Отказной раздел — не молчание: строка о битой странице — в health её раздела.
        var health = File.ReadAllText(HealthOf(bad.Id));
        health.Should().Contain("Проекты/" + bad.Id + "/Сбойная.md", "строка о странице — с её путём");
        health.Should().Contain("не разобрана");

        // Слот владельца освобождён после отказа: следующий прогон того же владельца идёт.
        var again = await _sut.TryRunOwnerAsync(User);
        again.Started.Should().BeTrue("отказ раздела не занят слот и не гасит службу");
    }

    // ─── c2. Прогон по кнопке по двум разделам: 2 строки метрик, health каждого раздела ──

    [Fact]
    public async Task ПрогонПоДвумРазделам_ДвеСтрокиМетрикСSectionИCodeVersion_HealthВКаждомРазделе()
    {
        var a = CreateProject("Проект А");
        var b = CreateProject("Проект Б");
        await _keeper.EnsureInitializedAsync(User);
        await WriteGoodPage(a.Id, "Знание-а");
        await WriteGoodPage(b.Id, "Знание-б");

        var res = await _sut.TryRunOwnerAsync(User);
        res.Started.Should().BeTrue();
        res.SectionsOk.Should().Be(2);
        res.SectionsFailed.Should().Be(0);

        // metrics.jsonl: ровно две строки (по одной на раздел), каждая — с section и codeVersion.
        var lines = MetricsLines();
        lines.Should().HaveCount(2, "одна строка метрик на обработанный раздел");
        var sections = lines.Select(l => l.GetProperty("section").GetString()).ToList();
        sections.Should().Contain("projects/" + a.Id);
        sections.Should().Contain("projects/" + b.Id);
        foreach (var line in lines)
            line.GetProperty("codeVersion").GetString().Should().NotBeNullOrEmpty(
                "метрика без штампа кода не сравнима с базовой линией S0b-2");

        // _health.md — в каждом разделе (проекции-файлы раздела, не в keeper/).
        File.Exists(HealthOf(a.Id)).Should().BeTrue();
        File.Exists(HealthOf(b.Id)).Should().BeTrue();
    }

    // ─── Конкурентность: кнопка и ночное окно — один прогон на владельца ───────────

    [Fact]
    public async Task ГонкаДвухПрогонов_ОдинНаВладельца_ВторойУжеИдёт_БезДвойнойЗаписи()
    {
        var p = CreateProject("Гонка");
        var key = "projects/" + p.Id;
        await _keeper.EnsureInitializedAsync(User);
        await WriteGoodPage(p.Id, "Знание");

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Держим блокировку раздела: первый прогон встанет на ней и не успеет закончиться
        // до второго вызова — ровно та ситуация, когда кнопка и ночное окно сходятся.
        var held = _keeper.WithSectionsLockedAsync(User, [key], () => tcs.Task);

        // async-метод выполняется на вызывающем потоке до первого await: к моменту возврата
        // Task слот владельца уже занят первым прогоном — второй отвергается детерминированно.
        var first = _sut.TryRunOwnerAsync(User);

        var second = await _sut.TryRunOwnerAsync(User);
        second.Started.Should().BeFalse("второй прогон отвергается, а не встаёт в очередь");

        // Тот же отказ по REST-кнопке: 409 с кодом, а не вторая запись файлов.
        AssertCode(await Controller().Lint(), 409, "wiki_lint_running");

        tcs.SetResult(0);
        var firstRes = await first;
        firstRes.Started.Should().BeTrue();
        await held;

        // Двойной записи нет: ровно одна строка метрик и один health на раздел.
        MetricsLines().Should().HaveCount(1);
        File.Exists(HealthOf(p.Id)).Should().BeTrue();
    }

    // ─── c3. Health чужого владельца — 403/404 ───────────────────────────────────

    [Fact]
    public async Task Здоровье_ЧужойВладелец403_БезФайла404_СвойHealth_Текстом()
    {
        var mine = CreateProject("Свой");
        var foreign = CreateProject("Чужой", owner: "u2");
        var ctl = Controller();

        // Своего health ещё нет (раздел не линтовался) — 404, не пустой отчёт.
        ((IStatusCodeActionResult)ctl.Health("projects/" + mine.Id)).StatusCode.Should().Be(404);

        // Чужой владелец — 403 той же ResolveSection, что у каталога: чужой health не читается.
        AssertCode(ctl.Health("projects/" + foreign.Id), 403, "wiki_foreign");

        // Записанный Хранителем health отдаётся как есть: это отчёт прогона, не производный DTO.
        await _keeper.WriteSectionHealthAndMetricsAsync(User, "projects/" + mine.Id, "# Здоровье раздела «x»", null);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(((OkObjectResult)ctl.Health("projects/" + mine.Id)).Value))
            .RootElement;
        json.GetProperty("section").GetString().Should().Be("projects/" + mine.Id);
        json.GetProperty("content").GetString().Should().Contain("Здоровье раздела");

        // POST /lint без prepared-состояния не роняет контроллер: прогон пустой вики — 200 с нулями.
        var ran = JsonDocument.Parse(JsonSerializer.Serialize(((OkObjectResult)await ctl.Lint()).Value)).RootElement;
        ran.GetProperty("ran").GetBoolean().Should().BeTrue();
    }

    // ─── Помощники ───────────────────────────────────────────────────────────────

    private Project CreateProject(string name, string owner = User)
    {
        var root = Path.Combine(_dir, "repo", owner, name);
        Directory.CreateDirectory(root);
        return _projects.Create(name, root, owner, owner);
    }

    // Страница раздела — записанная Хранителем (тот же путь, что у применения пакета):
    // папка раздела, файл и его запись в манифесте появляются вместе.
    private async Task WriteGoodPage(string projectId, string title)
    {
        (await _keeper.WritePageAsync(User, "projects/" + projectId, $"Проекты/{projectId}/{title}.md",
            "---\ntype: topic\nsources: [Чат 1]\n---\n# " + title + "\n\nтекст. [[Чат 1]]"))
            .Status.Should().Be(WikiOpStatus.Applied);
    }

    private string HealthOf(string projectId) => Path.Combine(_pages, "Проекты", projectId, WikiKeeper.HealthFileName);

    private string MetricsFile() => Path.Combine(WikiPaths.Keeper(_dir, User), "metrics.jsonl");

    private List<JsonElement> MetricsLines() =>
        File.Exists(MetricsFile())
            ? [.. File.ReadAllLines(MetricsFile()).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement)]
            : [];

    private static void AssertCode(IActionResult result, int status, string code)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>($"{status} с телом отказа").Which;
        obj.StatusCode.Should().Be(status);
        JsonDocument.Parse(JsonSerializer.Serialize(obj.Value)).RootElement
            .GetProperty("code").GetString().Should().Be(code);
    }

    private WikiController Controller(string user = User)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user)], "test"));
        return new WikiController(_keeper, _projects, _spheres, _sut, _config)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private sealed class NoSpheres : ISphereDirectory
    {
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public bool Enabled(string ownerId) => false;
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }

    private sealed class WikiFlagGate : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }
}
