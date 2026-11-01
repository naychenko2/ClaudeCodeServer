using System.Security.Claims;
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
using Microsoft.Extensions.Logging; // ThrowingLogger: ILogger/EventId (GlobalUsings держит только Abstractions)

namespace ClaudeHomeServer.Tests.Services;

// S3-3: служба линта вики — ночное окно + запуск по кнопке. Проверяется поведение прогона:
// отказ одного раздела не роняет остальных и не выходит из службы (c1); прогон по всем
// разделам оставляет health и ровно одну строку метрик на раздел (c2); health чужого
// владельца — 403/404 (c3); кнопка и ночное окно не делают двойной запись (конкурентность).
// Доработка по вердикту ревью S3-3: выключенный флаг llm-wiki — прогон пропускается целиком
// (ни health, ни метрик, кнопка отказана с причиной); сверка манифеста с диском по байтам даёт
// строки дрейфа в health и его счётчик в метриках; строка метрик — в единицах и именах
// базовой линии S0b-2 (проценты с одним знаком). W3-4a: тело ночного тика проверяется
// напрямую (RunNightTickAsync): дата прохода ставится только после прохода, где все владельцы
// завершились; исключение в тике не выходит из службы; тик и кнопка одного владельца — один
// прогон. Календарное окно здесь не проверяется: тику даётся время в окне, а прогон
// вызывается напрямую, как его зовёт REST.
// Образец построения — WikiControllerTests: temp-дир + боевые ProjectManager/NotesService +
// страница пишется Хранителем (тот же путь, что у применения).
public class WikiLintServiceTests : IDisposable
{
    private const string User = "u1";
    private const string Foreign = "u2"; // второй владелец: тесты ночного тика проверяют отказ одного
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

    // ─── W3-4a: ночной тик — дата после успеха, исключение не выходит, тик и кнопка ──

    [Fact]
    public async Task Тик_ОтказНаОдномВладельце_ОстальныеПрошли_ДатаПроходаНеСтоитДоУспеха()
    {
        var mine = CreateProject("Целый");
        var other = CreateProject("Сбойный", owner: Foreign);
        await _keeper.EnsureInitializedAsync(User);
        await _keeper.EnsureInitializedAsync(Foreign);
        await WriteGoodPage(mine.Id, "Знание");
        await WriteGoodPage(other.Id, "Знание", user: Foreign);

        // Отказ одного владельца: проверка флага бросает для u2 — исключение за пределами
        // раздела-изоляции, его закрывает охрана владельца (так тест моделирует отказ прохода).
        _flags.ThrowFor = Foreign;
        await _sut.RunNightTickAsync(NightNow());

        // Отказ одного владельца не срывает остальных: целый получил обычный health,
        // отказавший — не писан вовсе.
        File.Exists(HealthOf(mine.Id)).Should().BeTrue("отказ другого владельца не останавливает проход");
        File.Exists(HealthOf(other.Id, Foreign)).Should().BeFalse("отказавший владелец не записан");

        // Отказавший проход не двинул дату: следующий тик тех же суток пробует снова —
        // отказавший получают в этом же окне, а не на следующих сутки.
        _flags.ThrowFor = null;
        await _sut.RunNightTickAsync(NightNow());
        File.Exists(HealthOf(other.Id, Foreign)).Should().BeTrue(
            "дата ставится только после успешного прохода: отказавший пробуют в том же окне");
        // Дата — на владельца: прошедший не перезапущен из-за отказа соседа (одна строка метрик).
        MetricsLines().Should().HaveCount(1, "прошедшего владельца второй тик не повторяет");
    }

    [Fact]
    public async Task Тик_ОтказПрохода_ИОтказЖурнала_ВсеВладельцыПопытаны()
    {
        var first = CreateProject("Сбойный первый", owner: User);
        var second = CreateProject("Целый второй", owner: Foreign);
        await _keeper.EnsureInitializedAsync(User);
        await _keeper.EnsureInitializedAsync(Foreign);
        await WriteGoodPage(first.Id, "Знание");
        await WriteGoodPage(second.Id, "Знание", user: Foreign);

        // Две неудачи сразу: проверка флага бросает для первого владельца (его закрывает
        // охрана владельца), и журнал бросает на любой записи — и лог об отказе охраны.
        // Без LogSafe в охране исключение журнала выходит из неё и срывает второго владельца:
        // обход упорядочен (u1 раньше u2), поэтому порядок не маскирует провал.
        var sut = new WikiLintService(_notes, _keeper, _spheres, _config,
            new ThrowingLogger(), _flags);
        _flags.ThrowFor = User;
        await sut.RunNightTickAsync(NightNow());

        // Тавтологию «не бросило» заменяет проверка по существу: о каждом владельце спросили.
        _flags.Asked.Should().Contain([User, Foreign],
            "отказ журнала на первом владельце не срывает попытку на втором");
        File.Exists(HealthOf(second.Id, Foreign)).Should().BeTrue(
            "второй владелец прошёл, несмотря на отказ первого и отказ журнала о нём");
    }

    [Fact]
    public async Task Тик_УспехСтавитДатуВладельца_ВторогоПрогонаВТомЖеОкнеНет()
    {
        var p = CreateProject("Однажды");
        await _keeper.EnsureInitializedAsync(User);
        await WriteGoodPage(p.Id, "Знание");

        await _sut.RunNightTickAsync(NightNow());
        MetricsLines().Should().HaveCount(1);

        // Тот же календарный день, окно то же: второй тик владельца не трогает.
        await _sut.RunNightTickAsync(NightNow().AddMinutes(20));
        MetricsLines().Should().HaveCount(1, "дата прохода владельца стоит: строки метрик не плодятся");
    }

    [Fact]
    public async Task Тик_ПрогонУжеИдёт_ДатуНеСтавит_СледующийТикДелаетПрогон()
    {
        var p = CreateProject("Параллельный REST");
        var key = "projects/" + p.Id;
        await _keeper.EnsureInitializedAsync(User);
        await WriteGoodPage(p.Id, "Знание");

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = _keeper.WithSectionsLockedAsync(User, [key], () => tcs.Task);
        // REST-прогон занял слот владельца; тик получает «уже идёт» и закончен сразу.
        var manual = _sut.TryRunOwnerAsync(User);
        await _sut.RunNightTickAsync(NightNow());

        tcs.SetResult(0);
        (await manual).Started.Should().BeTrue();
        await held;
        MetricsLines().Should().HaveCount(1, "строку написал ручной прогон, тик ничего не писал");

        // «Уже идёт» не успех: даты нет, следующий тик окна выполняет проход сам.
        await _sut.RunNightTickAsync(NightNow().AddMinutes(10));
        MetricsLines().Should().HaveCount(2, "дата не ставилась на «уже идёт»");
    }

    [Fact]
    public async Task Тик_ФункцияВыключена_ДатуНеСтавит_ПослеВключенияТикДелаетПрогон()
    {
        var p = CreateProject("Флаг позже");
        await _keeper.EnsureInitializedAsync(User);
        await WriteGoodPage(p.Id, "Знание");

        _flags.On = false;
        await _sut.RunNightTickAsync(NightNow());
        MetricsLines().Should().BeEmpty("прогона не было");

        _flags.On = true;
        await _sut.RunNightTickAsync(NightNow().AddMinutes(10));
        MetricsLines().Should().HaveCount(1, "«функция выключена» не считается успехом для даты");
    }

    [Fact]
    public void ЦелиСносок_СтрокиВнутриFenced_НеСчитаются()
    {
        const string text = "тело\n\n```\n[^s1]: [[Пример]] — «в коде»\n```\n\n[^s2]: [[Настоящая]] — «цитата»\n";
        WikiLintService.FootnoteTargets(text).Should().Equal(["Настоящая"],
            "строка в code fence — пример синтаксиса, а не источник");
    }

    [Fact]
    public async Task СноскаТолькоВCodeFence_НеГлушитМетрикуБезИсточников()
    {
        var p = CreateProject("Пример в коде");
        await _keeper.EnsureInitializedAsync(User);
        await WriteGoodPage(p.Id, "Знание", "текст.\n\n```\n[^s1]: [[Карточка]] — «пример синтаксиса»\n```");

        (await _sut.TryRunOwnerAsync(User)).SectionsOk.Should().Be(1);

        MetricsLines().Single().GetProperty("wikiPagesWithoutSources").GetInt32().Should().Be(1,
            "сноска внутри code fence — не источник: страница остаётся без источников");
    }

    [Fact]
    public void ReadMtime_ОшибкаЧтения_ДатаНеизвестна_ИначеМетка()
    {
        WikiLintService.MtimeOrNull("x", _ => throw new IOException("нет доступа")).Should().BeNull();
        WikiLintService.MtimeOrNull("x", _ => throw new UnauthorizedAccessException()).Should().BeNull();
        var t = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        WikiLintService.MtimeOrNull("x", _ => t).Should().Be(new DateTimeOffset(t));
    }

    [Fact]
    public async Task ТикИРучнойПрогонОдногоВладельца_ОдинПрогон_ВторойУжеИдёт_БезДвойнойЗаписи()
    {
        var p = CreateProject("Гонка с тиком");
        var key = "projects/" + p.Id;
        await _keeper.EnsureInitializedAsync(User);
        await WriteGoodPage(p.Id, "Знание");

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Держим блокировку раздела: тик встанет на записи и не успеет закончиться до ручного
        // вызова — ровно та ситуация, когда ночное окно и кнопка сходятся.
        var held = _keeper.WithSectionsLockedAsync(User, [key], () => tcs.Task);

        // Тело тика выполняется на вызывающем потоке до первого реального await (запись под
        // блокировкой раздела): к моменту возврата слот владельца занят тиком — ручной вызов
        // отвергается детерминированно (TryRunOwnerAsync не менялся).
        var tick = _sut.RunNightTickAsync(NightNow());
        var manual = _sut.TryRunOwnerAsync(User);

        tcs.SetResult(0);
        await Task.WhenAll(tick, manual);
        await held;

        var manualRes = await manual; // завершён на отвержении: await не блокирует
        manualRes.Started.Should().BeFalse("тик и кнопка на одного владельца — один прогон");
        // Двойной записи нет: ровно одна строка метрик и один health.
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

    // ─── MAJOR 2 вердикта (доработка S3-2): дата «без изменений» берётся с mtime файла ──

    [Fact]
    public async Task ДатаШапкиПустая_СтрокаБезИзмененийПоMtimeФайла_СноскаСчитаетсяИсточником()
    {
        var p = CreateProject("Возраст");
        var rel = "Проекты/" + p.Id + "/Знание.md";
        await _keeper.EnsureInitializedAsync(User);
        await WriteGoodPage(p.Id, "Знание", "текст. [^s1]\n\n[^s1]: [[Карточка-источника]] — «Выдержка из источника.»");
        var page = Path.Combine(_pages, "Проекты", p.Id, "Знание.md");
        var healthPath = "без изменений · страница `" + rel + "`";

        // Шапка без updated: возраст свежей страницы не должен давать находки — mtime сейчас,
        // не порог. И наоборот: та же страница с older mtime обязана дать строку — иначе
        // пустая дата шапки глушит находку, как в дефекте.
        File.SetLastWriteTimeUtc(page, DateTime.UtcNow.AddDays(-89));
        (await _sut.TryRunOwnerAsync(User)).SectionsOk.Should().Be(1);
        File.ReadAllText(HealthOf(p.Id)).Should().NotContain("без изменений ·",
            "90 дней по часам файла: 89 — ещё не находка");

        File.SetLastWriteTimeUtc(page, DateTime.UtcNow.AddDays(-91));
        (await _sut.TryRunOwnerAsync(User)).SectionsOk.Should().Be(1);
        File.ReadAllText(HealthOf(p.Id)).Should().Contain(healthPath,
            "даты в шапке нет — возраст берётся с диска (дата факта, не вывод лентера)");

        var m = MetricsLines().Last();
        m.GetProperty("pagesWithoutDate").GetInt32().Should().Be(0,
            "дата с диска есть — страницы без даты нет; без этого счётчика ноль находок Stale читался бы как «всё свежее»");
        m.GetProperty("wikiPagesWithoutSources").GetInt32().Should().Be(0,
            "цель сноски текста — источник: её считает служба, а не поле шапки sources");
    }

    // ─── Минор 3 вердикта: единицы и имена метрик — базовая линия S0b-2 ──────────────

    [Fact]
    public async Task МетрикиВЕдиницахБазовойЛинии_ПроцентыСОднимЗнаком_ИменаПолейЕеИмени()
    {
        var p = CreateProject("Метрики");
        await _keeper.EnsureInitializedAsync(User);
        // Фикстура с известными долями: 3 страницы раздела; 2 ссылки — одна разрешённая
        // (Страница-а → Страница-б), одна в призрака; сироты — Страница-а и Страница-в.
        // Сноска Страницы-а — на ту же цель, что её же ссылка: ребро одно (декуп по цели
        // в пределах страницы), доли не меняются; но именно она делает Страницу-у страницей
        // с источником — см. wikiPagesWithoutSources ниже.
        await WriteGoodPage(p.Id, "Страница-а", "[[Страница-б]] и [[Призрак икс]]. [^s1]\n\n[^s1]: [[Страница-б]] — «Точная цитата из источника.»");
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
        // Источники — сносики текста, а не поле шапки sources (его ни один писатель страниц
        // не заполняет): без снок здесь две страницы, «Страница-б» и «Страница-в».
        m.GetProperty("wikiPagesWithoutSources").GetInt32().Should().Be(2);
        m.GetProperty("pagesWithoutDate").GetInt32().Should().Be(0,
            "дата есть с mtime файла: страниц, чей возраст не проверялся, нет");
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
    // user — владелец страницы: тесты ночного тика заводят вики двух владельцев.
    private async Task WriteGoodPage(string projectId, string title, string body = "текст. [[Чат 1]]", string user = User)
    {
        (await _keeper.WritePageAsync(user, "projects/" + projectId, $"Проекты/{projectId}/{title}.md",
            "---\ntype: topic\nsources: [Чат 1]\n---\n# " + title + "\n\n" + body))
            .Status.Should().Be(WikiOpStatus.Applied);
    }

    private string HealthOf(string projectId, string user = User) =>
        Path.Combine(WikiPaths.Pages(_dir, user), "Проекты", projectId, WikiKeeper.HealthFileName);

    // Время в окне (03:30 локальное, умолчание 03–4): тело тика проверяется тестом
    // напрямую, с той же проверкой окна, что у службы, — без ожидания ночи.
    private static DateTimeOffset NightNow() => new(DateTime.Today.AddHours(3).AddMinutes(30));

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
        // S7c-0: конструктор контроллера держит читателя журнала — эти тесты его не зовут,
        // экземпляр нужен, чтобы резолвился конструктор (свои сценарии журнала — в
        // WikiJournalReaderTests).
        var journal = new WikiJournalReader(_notes, new NoSessions(), _config);
        // S7b0b: то же про захват и очередь разбора — эти тесты их не зовут, экземпляры
        // нужны, чтобы резолвился конструктор (свои сценарии — в WikiIngestNoteTests).
        var apply = new WikiApplyService(_notes, _keeper, _spheres, _config);
        var capture = new WikiSourceCapture(_notes, _projects, new NoSessions(), _config, _keeper);
        var ingest = new WikiIngestService(new NoopCheapRunner(), _spheres, apply, _config);
        return new WikiController(_keeper, _projects, _spheres, _sut, journal, _flags, _config, capture, ingest)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
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
    // ThrowFor — владелец, для которого проверка бросает: моделирует отказ обработки
    // владельца в ночном тике (тесты даты прохода и обороны тика).
    private sealed class WikiFlagGate : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool On = true;
        public string? ThrowFor;
        // О каких владельцах спрашивали (по порядку): тест обороны требует обоих.
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Asked = new();
        public bool IsEnabled(string userId, string key)
        {
            if (key == FeatureFlagKeys.LlmWiki) Asked.Enqueue(userId);
            if (userId == ThrowFor) throw new InvalidOperationException("проверка флага бросает: " + userId);
            return On && key == FeatureFlagKeys.LlmWiki;
        }
    }

    // Логгер, бросящий на любой записи: моделирует отказ самого журнала (отказ sink'а,
    // диск полон) — логирование в оборонах не должно становиться новым источником
    // исключения: ни для хоста (catch тика), ни для остальных владельцев (catch охраны).
    private sealed class ThrowingLogger : ILogger<WikiLintService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            throw new InvalidOperationException("запись журнала не удалась: " + formatter(state, exception));
        }
    }
}
