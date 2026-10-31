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
// Доработка по вердикту ревью S3-3: выключенный флаг llm-wiki — прогон пропускается целиком
// (ни health, ни метрик, кнопка отказана с причиной); сверка манифеста с диском по байтам даёт
// строки дрейфа в health и его счётчик в метриках; строка метрик — в единицах и именах
// базовой линии S0b-2 (проценты с одним знаком). Окно (его часы) здесь не проверяется:
// прогон вызывается напрямую, как его зовёт REST.
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
    // Флаг llm-wiki — часть фикстуры: выключенный флаг теперь означает «прогона нет», и
    // проверить это можно только переключив его на живой фикстуре.
    private readonly WikiFlagGate _flags = new();

    public WikiLintServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_lint_service_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        // Флаг llm-wiki включён по умолчанию: иначе граф не несёт страниц вики и лентеру
        // нечего видеть (тот же приём, что в WikiLintTests/NotesWikiSourceTests). Тот же
        // экземпляр шва — у службы: состояние флага она читает сама (решение по ревью S3-3).
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, flags: _flags);
        _keeper = new WikiKeeper(_notes, _config);
        _sut = new WikiLintService(_notes, _keeper, _spheres, _config, NullLogger<WikiLintService>.Instance, _flags);
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

    // ─── MAJOR 1 вердикта (доработка S3-3): выключенный флаг — не «чисто», а пропуск ──

    [Fact]
    public async Task ФлагВыключен_ПрогонПропущен_НиHealthНиМетрик_КнопкаОтказанаСПричиной()
    {
        var p = CreateProject("Выключен");
        await _keeper.EnsureInitializedAsync(User);
        await WriteGoodPage(p.Id, "Знание"); // сирота: на страницу нет ни одной ссылки

        _flags.On = false;
        var res = await _sut.TryRunOwnerAsync(User);

        res.Outcome.Should().Be(WikiLintRunOutcome.FeatureOff);
        File.Exists(HealthOf(p.Id)).Should().BeFalse(
            "при выключенном флаге граф не несёт страниц вики: «Находки (0)» был бы ложью");
        File.Exists(MetricsFile()).Should().BeFalse(
            "нулевые метрики невыполненной проверки не входят в сравнение с базовой линией");

        // Отказ кнопки — с причиной, а не «уже идёт» и не «ran».
        AssertCode(await Controller().Lint(), 409, "wiki_llm_wiki_off")
            .GetProperty("message").GetString().Should().Contain("функция выключена");

        // Фикстура нетривиальна: при включённом флаге та же страница даёт находку сироты —
        // значит молчание выше было про флаг, а не про пустой раздел.
        _flags.On = true;
        (await _sut.TryRunOwnerAsync(User)).Started.Should().BeTrue();
        File.ReadAllText(HealthOf(p.Id)).Should().Contain("сирота");
    }

    // ─── MAJOR 2 вердикта (доработка S3-3): сверка манифеста с диском по байтам ──────

    [Fact]
    public async Task ПравкаМимоХранителяИУдалениеФайла_СтрокиОДрейфеВЗдоровье_СчётчикДрейфаВМетриках()
    {
        var p = CreateProject("Дрейф");
        await _keeper.EnsureInitializedAsync(User);
        await WriteGoodPage(p.Id, "Знание-а");
        await WriteGoodPage(p.Id, "Знание-б");

        // Правка мимо Хранителя: байты файла другие, запись в манифесте — та же. Членство не
        // изменилось: по одному членству (сверка линтера) расхождения нет.
        File.AppendAllText(Path.Combine(_pages, "Проекты", p.Id, "Знание-а.md"),
            "\n\nправка мимо Хранителя.", new UTF8Encoding(false));
        // Удаление мимо Хранителя: файла нет, запись в манифесте осталась.
        File.Delete(Path.Combine(_pages, "Проекты", p.Id, "Знание-б.md"));

        var res = await _sut.TryRunOwnerAsync(User);
        res.SectionsOk.Should().Be(1, "расхождение — находка, а не отказ раздела");

        var health = File.ReadAllText(HealthOf(p.Id));
        health.Should().Contain("дрейф · страница `Проекты/" + p.Id + "/Знание-а.md`");
        health.Should().Contain("файл расходится с записью манифеста");
        health.Should().Contain("дрейф · страница `Проекты/" + p.Id + "/Знание-б.md`");
        health.Should().Contain("файла из записи манифеста нет на диске");

        MetricsLines().Single().GetProperty("driftCount").GetInt32().Should().Be(2,
            "оба расхождения — и правка, и удаление — сочтены");
    }

    // ─── Минор 3 вердикта: единицы и имена метрик — базовая линия S0b-2 ──────────────

    [Fact]
    public async Task МетрикиВЕдиницахБазовойЛинии_ПроцентыСОднимЗнаком_ИменаПолейЕеИмени()
    {
        var p = CreateProject("Метрики");
        await _keeper.EnsureInitializedAsync(User);
        // Фикстура с известными долями: 3 страницы раздела; 2 ссылки — одна разрешённая
        // (Страница-а → Страница-б), одна в призрака; сироты — Страница-а и Страница-в.
        await WriteGoodPage(p.Id, "Страница-а", "[[Страница-б]] и [[Призрак икс]].");
        await WriteGoodPage(p.Id, "Страница-б", "Текст.");
        await WriteGoodPage(p.Id, "Страница-в", "Текст.");

        (await _sut.TryRunOwnerAsync(User)).SectionsOk.Should().Be(1);

        var m = MetricsLines().Single();
        m.GetProperty("notes").GetInt32().Should().Be(3);
        m.GetProperty("links").GetInt32().Should().Be(2);
        m.GetProperty("ghosts").GetInt32().Should().Be(1);
        m.GetProperty("ghostsShare").GetDouble().Should().Be(50.0, "процент с одним знаком, не доля 0..1");
        m.GetProperty("distinctGhostTargets").GetInt32().Should().Be(1);
        m.GetProperty("orphans").GetInt32().Should().Be(2);
        m.GetProperty("orphansShareAll").GetDouble().Should().Be(66.7);
        m.GetProperty("orphansNoJournal").GetInt32().Should().Be(2);
        m.GetProperty("orphansShareNoJournal").GetDouble().Should().Be(66.7);
        m.GetProperty("unlinkedMentions").GetInt32().Should().Be(0);
        m.GetProperty("driftCount").GetInt32().Should().Be(0, "всё записано Хранителем: дрейфа нет");
        // Полей без аналога в базовой линии три — они остались под своими именами.
        m.GetProperty("wikiPages").GetInt32().Should().Be(3);
        m.GetProperty("wikiPagesWithoutSources").GetInt32().Should().Be(0);
        // Старых имён строки S3-3 в формате нет: их замена — предмет этой правки.
        m.TryGetProperty("orphansShare", out _).Should().BeFalse("имя базовой линии — orphansShareAll");
        m.TryGetProperty("danglingLinks", out _).Should().BeFalse("имя базовой линии — ghosts");
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
    // Тело страницы — необязательное: метрики и находки зависят от ссылок в тексте.
    private async Task WriteGoodPage(string projectId, string title, string body = "текст. [[Чат 1]]")
    {
        (await _keeper.WritePageAsync(User, "projects/" + projectId, $"Проекты/{projectId}/{title}.md",
            "---\ntype: topic\nsources: [Чат 1]\n---\n# " + title + "\n\n" + body))
            .Status.Should().Be(WikiOpStatus.Applied);
    }

    private string HealthOf(string projectId) => Path.Combine(_pages, "Проекты", projectId, WikiKeeper.HealthFileName);

    private string MetricsFile() => Path.Combine(WikiPaths.Keeper(_dir, User), "metrics.jsonl");

    private List<JsonElement> MetricsLines() =>
        File.Exists(MetricsFile())
            ? [.. File.ReadAllLines(MetricsFile()).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement)]
            : [];

    // Тело отказа возвращается: отказ обязан быть понятным человеку, а не только кодом.
    private static JsonElement AssertCode(IActionResult result, int status, string code)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>($"{status} с телом отказа").Which;
        obj.StatusCode.Should().Be(status);
        var body = JsonDocument.Parse(JsonSerializer.Serialize(obj.Value)).RootElement;
        body.GetProperty("code").GetString().Should().Be(code);
        return body;
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

    // On — состояние флага владельца: по умолчанию включён (иначе фикстура невидима графу);
    // выключен — предмет проверки «прогон при выключенном флаге пропускается».
    private sealed class WikiFlagGate : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool On = true;
        public bool IsEnabled(string userId, string key) => On && key == FeatureFlagKeys.LlmWiki;
    }
}
