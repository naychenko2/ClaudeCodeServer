using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
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
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// S6f (ADR-035, решение 12а): ступень 0 и балл ступени 1 — чистая функция WikiChatScorer,
// список и пересчёт — WikiChatsController в теневом режиме (ничего не запускают).
//
// Границы зон half-open: [15,∞) — triage, [6,15) — gray, [0,6) — skipped; тесты стоят ровно
// на границах (15, 6, 5) и на пустом/коротком входе. Тесты c2 построены так, что БЕЗ обхода
// по документам/объёму чат упал бы в «пропущено» — мутация «убрать обход» краснит тест.
//
// Числовые веса в тестах — умолчания WikiChatScorer (их подбирает пилот S8): тесты держат
// поведение (зона, наличие сигнала, порядок причины), а точные баллы — ровно на границах.
public class WikiChatScorerTests : IDisposable
{
    private static readonly DateTime Base = new(2026, 10, 20, 12, 0, 0);

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly SessionsStub _sessions = new();
    // S7b0a: конструктор контроллера требует сервис разбора чатов. Теневые тесты его не
    // зовут: список и пересчёт ничего не разбирают (running в списке — false на пустом
    // in-flight). Экземпляр настоящий, модель — Noop: он не вызывается ни разу.
    private readonly WikiChatIngestService _chat;

    public WikiChatScorerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_chats_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        var notes = new NotesService(projects, _config, NullLogger<NotesService>.Instance, null, new WikiFlagOn());
        var keeper = new WikiKeeper(notes, _config);
        _chat = new WikiChatIngestService(
            new WikiSourceCapture(notes, projects, _sessions, _config, keeper),
            new WikiIngestService(new NoopCheapRunner(), new NoSpheres(),
                new WikiApplyService(notes, keeper, new NoSpheres(), _config), _config),
            _sessions, keeper, _config);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── c1: ступень 0 ──────────────────────────────────────────────────────

    [Fact]
    public void ЧатИсполненияЗадачи_Исключён_ДажеСImportant()
    {
        var r = Score(In(RichChat(), taskExec: true, mark: WikiMarks.Important));
        r.Zone.Should().Be(WikiChatScorer.ZoneExcluded);
        r.Reason.Should().Contain("исполнения задачи");

        // Без пометки — тот же исход (исключение само по себе).
        Score(In(RichChat(), taskExec: true)).Zone.Should().Be(WikiChatScorer.ZoneExcluded);
    }

    [Fact]
    public void ПометкаSkip_ПобеждаетВысокийБалл()
    {
        // Тот же содержательный чат: без пометки он «разбирается»…
        var plain = Score(In(RichChat()));
        plain.Zone.Should().Be(WikiChatScorer.ZoneTriage);
        plain.Score.Should().BeGreaterThanOrEqualTo(WikiChatScorer.ThresholdTriage);

        // …с «не разбирать» — исключён: пометка человека перекрывает автомат (ADR-035:214).
        var skipped = Score(In(RichChat(), mark: WikiMarks.Skip));
        skipped.Zone.Should().Be(WikiChatScorer.ZoneExcluded);
        skipped.Reason.Should().Contain("не разбирать");
    }

    [Fact]
    public void ImportantИЧатСИтогом_Разбираются_ПриБаллеНоль()
    {
        // Короткий чат (диалог < 1500): без пометки — ступень 0…
        var short0 = In(ShortChat());
        Score(short0).Zone.Should().Be(WikiChatScorer.ZoneExcluded);

        // …с пометкой «важный» или с «Итогом» — «разбирается», минуя ступень 0
        // (кроме исполнения задач), балл не считается.
        var imp = Score(short0 with { WikiMark = WikiMarks.Important });
        imp.Zone.Should().Be(WikiChatScorer.ZoneTriage);
        imp.Score.Should().Be(0, "по метке балл не считается — причина называет метку");
        imp.Reason.Should().Contain("важный");

        var sum = Score(short0 with { HasSummary = true });
        sum.Zone.Should().Be(WikiChatScorer.ZoneTriage);
        sum.Reason.Should().Contain("Итог");

        var pinned = Score(short0 with { IsPinned = true });
        pinned.Zone.Should().Be(WikiChatScorer.ZoneTriage);
    }

    [Fact]
    public void НетРепликЧеловекаИлиКороткийДиалог_Исключены()
    {
        // Только ответы агента — нечего разбирать.
        var noHuman = Score(In([T(new string('а', 5_000))]));
        noHuman.Zone.Should().Be(WikiChatScorer.ZoneExcluded);
        noHuman.Reason.Should().Contain("нет ни одной реплики");

        // auto/systemDirective/viaAgent репликами человека не считаются (ADR-035:194).
        var onlyAuto = Score(In([
            U("авто-промпт задачи", auto: true),
            U("директива цикла", sysDir: true),
            U("ответ из чужого чата", viaAgent: true),
            T(new string('а', 5_000)),
        ]));
        onlyAuto.Zone.Should().Be(WikiChatScorer.ZoneExcluded);

        // Диалог ровно на границе (1 500) — не исключается; на символ короче — исключается.
        var atBoundary = In([U(new string('б', 1_000)), T(new string('а', 500))]);
        Score(atBoundary).Zone.Should().NotBe(WikiChatScorer.ZoneExcluded);
        var below = In([U(new string('б', 999)), T(new string('а', 500))]);
        Score(below).Zone.Should().Be(WikiChatScorer.ZoneExcluded);
        Score(below).Reason.Should().Contain("короче 1500");
    }

    // ─── c2: обход ступени 1 (не-разработческие чаты) ──────────────────────

    [Fact]
    public void ОднаРепликаИОтвет12000Символов_СераяЗона_ПоОбходуАНеПоБаллу()
    {
        // Балл такого чата — 4 (< 6): без обхода он был бы «пропущено». Мутация «убрать
        // обход» (BypassReason → null) краснит этот тест.
        var r = Score(In([U("Разбери протокол встречи"), T(new string('с', 12_000))]));
        r.Zone.Should().Be(WikiChatScorer.ZoneGray);
        r.Score.Should().BeLessThan(WikiChatScorer.ThresholdGray);
        r.Reason.Should().Contain("обход: ответ агента 12000");
    }

    [Fact]
    public void ЗаписьДокумента_ПоднимаетВСеруюЗону_ПриБаллеНижеПорога()
    {
        // Балл 3 (< 6): без обхода по записи документа чат был бы «пропущено».
        var r = Score(In([U("подготовь справку по встрече"), Tool("Write", "docs/справка.md"), T(new string('а', 1_500))]));
        r.Zone.Should().Be(WikiChatScorer.ZoneGray);
        r.Score.Should().BeLessThan(WikiChatScorer.ThresholdGray);
        r.Reason.Should().Contain("обход: записан документ");
    }

    [Fact]
    public void РазборТрёхДокументов_ОбходИРаботаСДокументами()
    {
        var r = Score(In([
            U("сравни три регламента"),
            Tool("Read", "docs/регламент-1.md"),
            Tool("Read", "docs/регламент-2.md"),
            Tool("Read", "docs/регламент-3.md"),
            T(new string('а', 1_500)),
        ]));
        r.Zone.Should().Be(WikiChatScorer.ZoneGray);
        r.Reason.Should().Contain("обход: прочитано документов 3");
        r.Signals.Should().Contain(s => s.Name == WikiChatScorer.SignalDocumentWork);
    }

    [Fact]
    public void СтруктурированныйОтвет4000_ОбходВСеруюЗону()
    {
        var structured = "## План встречи\n- первый пункт\n" + new string('п', 4_000);
        var r = Score(In([U("сделай обзор по материалам"), T(structured)]));
        r.Zone.Should().Be(WikiChatScorer.ZoneGray);
        r.Reason.Should().Contain("обход: структурированный ответ");
    }

    // ─── Границы балла: 15 / 6 / 5 ──────────────────────────────────────────

    [Fact]
    public void БаллРовно15_Разбирается_НаМеньше_СераяЗона()
    {
        // 5 реплик (+5) + «решили» и «итог» (+8) + «Нет…» как правка направления (+2) = 15.
        var at15 = In(new List<StoredMessage> {
            U("Нет, решили по-другому: как в прошлый раз"),
            U("итог такой: оставляем прежний вариант"),
            U(Plain("первая реплика про логистику склада")),
            U(Plain("вторая реплика про поставку овощей")),
            U(Plain("третья реплика про график склада")),
            T(new string('а', 1_500)),
        });
        var r15 = Score(at15);
        r15.Score.Should().Be(15);
        r15.Zone.Should().Be(WikiChatScorer.ZoneTriage);

        // Ровно 14 — уже серая зона.
        var at14 = In(new List<StoredMessage> {
            U("Нет, решили по-другому: как в прошлый раз"),
            U("итог такой: оставляем прежний вариант"),
            U(Plain("первая реплика про логистику склада")),
            U(Plain("вторая реплика про поставку овощей")),
            T(new string('а', 1_500)),
        });
        Score(at14).Score.Should().Be(14);
        Score(at14).Zone.Should().Be(WikiChatScorer.ZoneGray);
    }

    [Fact]
    public void БаллРовно6_СераяЗона_Пять_Пропущено()
    {
        var at6 = In(PlainTurns(6));
        Score(at6).Score.Should().Be(6);
        Score(at6).Zone.Should().Be(WikiChatScorer.ZoneGray);

        var at5 = In(PlainTurns(5));
        Score(at5).Score.Should().Be(5);
        Score(at5).Zone.Should().Be(WikiChatScorer.ZoneSkipped);
    }

    // ─── Сигналы таблицы ступени 1 ──────────────────────────────────────────

    [Fact]
    public void СигналыФормулировокРешенийПравокНаправленияСвязейИВозвратаКТеме()
    {
        var r = Score(In(new List<StoredMessage> {
            U("стоп, так не пойдёт"), // правка направления
            U("лучше сделать по-старому"), // правка направления
            U(Plain("ссылка на регламент: docs/регламент.md и задача task:abcd")), // связи
            U(Plain("продолжим разговор про логистику склада")),
            T("итогом встречи: решили хранить прежний порядок " + new string('а', 1_500)), // решения в последнем ответе
        }) with { ContinuedFromSessionId = "chat-parent" }); // продолжение — возврат к теме

        r.Signals.Should().Contain(s => s.Name == WikiChatScorer.SignalDecisionWords && s.Weight == 8);
        r.Signals.Should().Contain(s => s.Name == WikiChatScorer.SignalCourseCorrections && s.Weight == 4);
        r.Signals.Should().Contain(s => s.Name == WikiChatScorer.SignalLinks);
        r.Signals.Should().Contain(s => s.Name == WikiChatScorer.SignalTopicReturn);
    }

    [Fact]
    public void ПравкиНаправления_ТолькоНачалоРепликиИТолькоСловом_НеПодстрокойВнутри()
    {
        var r = Score(In(new List<StoredMessage> {
            U("Нет"), // «нет» отдельным словом
            U("Не так делай"), // «не так»
            U("Не надо так делать"), // «не» — не из списка: не правка
            U(Plain("вот решение: нет смысла спорить о логистике склада")), // «нет» внутри — не правка
            T(new string('а', 1_500)),
        }));
        var corr = r.Signals.SingleOrDefault(s => s.Name == WikiChatScorer.SignalCourseCorrections);
        corr.Should().NotBeNull();
        corr.Weight.Should().Be(4, "только две реплики, начатые с правки направления");
    }

    [Fact]
    public void УшлоВЗаметкиИЗадачи_ИнструментИПросьбаЧеловека()
    {
        var r = Score(In([
            U("запиши это в заметки"), // просьба
            Tool("mcp__notes__notes_create"),
            Tool("mcp__tasks__tasks_create"),
            U(Plain("продолжим про логистику склада")),
            T(new string('а', 1_500)),
        ]));
        r.Signals.Should().Contain(s => s.Name == WikiChatScorer.SignalWentToNotesTasks && s.Weight == 9);
    }

    [Fact]
    public void РаботаСДокументами_ЧтениеЗаписьИВложенияЧеловека()
    {
        var r = Score(In([
            U(Plain("вот материалы встречи"), attached: ["встреча.docx", "схема.xlsx"]),
            Tool("Read", "docs/встреча.md"),
            Tool("Edit", "docs/встреча.md"),
            Tool("Read", "src/Program.cs"), // не документ — не считается
            U(Plain("продолжим про логистику склада")),
            T(new string('а', 1_500)),
        ]));
        r.Signals.Should().Contain(s => s.Name == WikiChatScorer.SignalDocumentWork && s.Weight == 8);
    }

    [Fact]
    public void ПонижениеЗаТриНечегоДобавитьПодряд_ДваРядаНеПонижают()
    {
        // Тот же чат со значением балла 16 (т15 + ещё одна реплика): streak 2 — без штрафа,
        // streak 3 — штраф −10 и падение зоны (16 → 6, серая зона).
        var baseInput = In(new List<StoredMessage> {
            U("Нет, решили по-другому: как в прошлый раз"),
            U("итог такой: оставляем прежний вариант"),
            U(Plain("первая реплика про логистику склада")),
            U(Plain("вторая реплика про поставку овощей")),
            U(Plain("третья реплика про график склада")),
            U(Plain("четвёртая реплика про поставку овощей")),
            T(new string('а', 1_500)),
        });
        Score(baseInput).Zone.Should().Be(WikiChatScorer.ZoneTriage);

        var two = Score(baseInput with { NothingNewStreak = 2 });
        two.Signals.Should().NotContain(s => s.Name == WikiChatScorer.SignalNothingNewPenalty);

        var three = Score(baseInput with { NothingNewStreak = 3 });
        three.Signals.Should().Contain(s => s.Name == WikiChatScorer.SignalNothingNewPenalty && s.Weight == -10);
        three.Zone.Should().Be(WikiChatScorer.ZoneGray);
    }

    [Fact]
    public void Причина_БаллИТриГлавныхСигнала_ПоУбываниюВеса()
    {
        var r = Score(In(RichChat()));
        r.Reason.Should().StartWith($"балл {r.Score}: ");
        var top = r.Signals.OrderByDescending(s => s.Weight).Take(3);
        foreach (var s in top)
            r.Reason.Should().Contain(SignalLabel(s.Name));
        r.Reason.Should().NotContain(
            SignalLabel(r.Signals.OrderByDescending(s => s.Weight).Skip(3).First().Name),
            "в причине — три главных сигнала, не все");
    }

    [Fact]
    public void СкорерЧистаяФункция_ТотЖеВход_ТотЖеВыход_БезОбращенияКФайламИВремени()
    {
        var input = In(RichChat());
        var a = Score(input);
        var b = Score(input);
        a.Zone.Should().Be(b.Zone);
        a.Score.Should().Be(b.Score);
        a.Reason.Should().Be(b.Reason);
        a.Signals.Should().Equal(b.Signals);
    }

    // ─── c5 + гонка: контроллер в теневом режиме ────────────────────────────

    [Fact]
    public async Task СписокИПересчётНеЗапускаютРазбор_МодельНеПозванаИХроникаНеСоздана()
    {
        // Структурная гарантия (S7b0a поправила тень S6f): у контроллера нет прямой зависимости
        // от дешёвой модели — разбор достижим только через сервис разбора (он в ctor и есть,
        // звать его может только POST analyze). Список и пересчёт его не зовут — это
        // поведенческая половина ниже: после пересчёта ни хроник, ни пакетов.
        var ctor = typeof(WikiChatsController).GetConstructors().Single();
        var deps = ctor.GetParameters().Select(p => p.ParameterType).ToList();
        deps.Should().NotContain(t => t == typeof(ICheapTextRunner),
            "модель не достижима из контроллера напрямую — только через сервис разбора");
        deps.Should().Contain(t => t == typeof(WikiChatIngestService),
            "analyze идёт через сервис (S7b0a)");
        typeof(WikiChatsController).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(f => f.FieldType).Should().NotContain(t => t == typeof(ICheapTextRunner));

        SeedChats();
        var res = await Controller().Recalculate();
        ((OkObjectResult)res).Value.Should().BeAssignableTo<WikiChatsRecalcDto>();

        // Поведенческая половина: после пересчёта в корне вики — только стор отбора.
        // Ни хроник (Хроники/), ни пакетов (packages/): разбор не запускался.
        Directory.GetFiles(_wikiRoot, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_wikiRoot, f).Replace('\\', '/'))
            .Should().Equal("keeper/chat-triage.json");
    }

    [Fact]
    public async Task Пересчёт_ЗоныВСторе_КурсорСохраняется_ЧужойВладелецНеТронут() {
        SeedChats();
        // Курсор существующей записи принадлежит разбору (S6b), не пересчёту.
        WikiChatTriageStore.Upsert(_wikiRoot, new WikiChatTriageEntry("chat-a", 42, null, null, null, 0, default)).Ok.Should().BeTrue();

        var res = (OkObjectResult)await Controller().Recalculate();
        var dto = ((WikiChatsRecalcDto)res.Value!).Counted;
        dto.Should().Be(3, "свои чаты: a, b, c; чат чужого владельца не пересчитывается");

        var entries = WikiChatTriageStore.Get(_wikiRoot).Entries;
        entries.Select(e => e.SessionId).Should().Equal(new[] { "chat-a", "chat-b", "chat-c" });
        var a = entries.Single(e => e.SessionId == "chat-a");
        a.Cursor.Should().Be(42, "пересчёт не двигает курсор разбора");
        // Зона пересчёта — в своём поле (доработка S6f, M1): общий zone принадлежит разбору,
        // и пересчёт его не пишет — ни на существующей записи, ни на новой.
        a.Zone.Should().BeNull("исхода разбора здесь не было: zone — не поле пересчёта");
        a.Reason.Should().BeNull();
        a.Stage1Zone.Should().Be(WikiChatScorer.ZoneTriage);
        a.Score.Should().BeGreaterThanOrEqualTo(WikiChatScorer.ThresholdTriage);
        a.Signals.Should().NotBeEmpty();
        entries.Single(e => e.SessionId == "chat-b").Stage1Zone.Should().Be(WikiChatScorer.ZoneGray);
        entries.Single(e => e.SessionId == "chat-c").Stage1Zone.Should().Be(WikiChatScorer.ZoneExcluded);
    }

    [Fact]
    public async Task Список_ПоЗонамСПричиной_НесчитанныхЧатовНет_НечитаемыйСтор_409()
    {
        // До пересчёта список пуст даже при существующих чатах: он показывает посчитанное,
        // а не считает сам.
        SeedChats();
        ((OkObjectResult)Controller().List()).Value.Should().BeAssignableTo<WikiChatsListDto>()
            .Which.Items.Should().BeEmpty();

        await Controller().Recalculate();
        var list = ((WikiChatsListDto)((OkObjectResult)Controller().List()).Value!).Items;
        list.Select(i => i.Zone).Should().Equal(new[] {
            WikiChatScorer.ZoneTriage, WikiChatScorer.ZoneGray, WikiChatScorer.ZoneExcluded });
        list.Should().OnlyContain(i => i.Reason != "" && i.Name != null);
        list.Single(i => i.Zone == WikiChatScorer.ZoneExcluded).Reason.Should().Contain("ступень 0");

        // Чужой владелец своей вики не видит (стор — в его же корне, пустом).
        ((OkObjectResult)Controller("u2").List()).Value.Should().BeAssignableTo<WikiChatsListDto>()
            .Which.Items.Should().BeEmpty();

        // Нечитаемый стор — 409 с кодом, список не притворяется пустым.
        var file = Path.Combine(_wikiRoot, "keeper", "chat-triage.json");
        File.WriteAllText(file, "{ не json");
        var code = ((IStatusCodeActionResult)Controller().List()).StatusCode;
        code.Should().Be(409);
        ((IStatusCodeActionResult)await Controller().Recalculate()).StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task Список_ИсходРазбораНечегоДобавить_ДержитсяПослеПересчёта()
    {
        // Доработка S6f: чат с исходом разбора nothing_new не переселяется пересчётом в
        // «разбирается» — в списке его зона (и причина) разбора, а зона ступени 1 остаётся
        // в своём поле стора.
        SeedChats();
        WikiChatTriageStore.Upsert(_wikiRoot, new WikiChatTriageEntry(
            "chat-a", 42, null, WikiChatIngestService.ZoneNothingNew, "нет новых утверждений", 3, default))
            .Ok.Should().BeTrue();

        await Controller().Recalculate();

        var list = ((OkObjectResult)Controller().List()).Value.Should().BeAssignableTo<WikiChatsListDto>().Which;
        var a = list.Items.Single(i => i.SessionId == "chat-a");
        a.Zone.Should().Be(WikiChatIngestService.ZoneNothingNew, "исход разбора приоритетнее зоны ступени 1");
        a.Reason.Should().Be("нет новых утверждений", "причина показанной зоны — причина разбора");
        a.Score.Should().NotBeNull("балл пересчёта остаётся в строке: поле скорера не стёрто");
        // Чаты без исхода разбора — по зоне ступени 1.
        list.Items.Single(i => i.SessionId == "chat-b").Zone.Should().Be(WikiChatScorer.ZoneGray);
        // counts — по показанной зоне.
        list.Counts[WikiChatIngestService.ZoneNothingNew].Should().Be(1);

        // На диске оба владения на своих местах: исход разбора и балл со своей зоной.
        var stored = WikiChatTriageStore.Get(_wikiRoot).Entries.Single(e => e.SessionId == "chat-a");
        stored.Zone.Should().Be(WikiChatIngestService.ZoneNothingNew);
        stored.NothingNewStreak.Should().Be(3, "пересчёт не сбрасывает счётчик разбора");
        stored.Stage1Zone.Should().Be(WikiChatScorer.ZoneTriage);
    }

    [Fact]
    public void Список_СтараяЗаписьБезЗоныСтупени1_ПоказываетПрежнийZone()
    {
        // Файл до разведения владения (пересчёт писал общий zone, stage1Zone нет) остаётся
        // показуемым без пересчёта: fallback на написанную зону, а не пустая строка.
        SeedChats();
        var file = Path.Combine(_wikiRoot, "keeper", "chat-triage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """[{"sessionId":"chat-a","cursor":7,"triagedAt":"2026-10-20T12:00:00+00:00","zone":"gray","reason":"балл 8: …","score":8}]""");

        var items = ((WikiChatsListDto)((OkObjectResult)Controller().List()).Value!).Items;
        var a = items.Single(i => i.SessionId == "chat-a");
        a.Zone.Should().Be(WikiChatScorer.ZoneGray, "своей зоны ступени 1 нет — показывается написанная");
        a.Reason.Should().Be("балл 8: …");
        a.Score.Should().Be(8);
    }

    [Fact]
    public async Task ГонкаДвухПересчётов_ОбаЗавершены_СторЦелый()
    {
        SeedChats();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await barrier.Task;
            return await Controller().Recalculate();
        })).ToArray();
        barrier.SetResult();

        foreach (var t in tasks)
            ((IStatusCodeActionResult)await t.WaitAsync(TimeSpan.FromSeconds(30))).StatusCode.Should().Be(200);

        var read = WikiChatTriageStore.Get(_wikiRoot);
        read.State.Should().Be(WikiTriageFileState.Valid);
        read.Entries.Select(e => e.SessionId).Should().Equal(new[] { "chat-a", "chat-b", "chat-c" },
            "параллельные пересчёты не теряют и не дублируют записи (блок стора на корень)");
    }

    // ─── NA-3: пересчёт как служба (прямой вызов, без контроллера) ──────────

    [Fact]
    public async Task ПересчётСлужбыНаНечитаемомСторе_ОтказСКодом_ФайлБайтВБайтНеТронут()
    {
        // Тот же контракт, что был у контрольной ручки: нечитаемый стор — отказ с машинным
        // кодом и строкой для человека, а не «пустой пересчёт» и не молчаливая перезапись
        // (fail-closed стора). Служба обязана держать его одна: её зовёт и ночной разбор.
        SeedChats();
        var file = Path.Combine(_wikiRoot, "keeper", "chat-triage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ не json");
        var before = File.ReadAllBytes(file);

        var res = await new WikiChatRecalc(_sessions, _config).RecalculateAsync("u1");

        res.Ok.Should().BeFalse("нечитаемый стор — отказ, а не пустой пересчёт");
        res.Code.Should().Be(WikiChatTriageStore.CodeInvalid);
        // Точная строка для фронта (прежний message 409): сторинный текст «…— запись в
        // него невозможна» означал бы, что отказ пришёл не ранним провером, а с первой
        // попытки записи — вызывающий (ночной разбор) получил бы другую формулировку.
        res.Reason.Should().Be("файл разбора чатов не читается",
            "ранний отказ несёт ровно прежний текст контракта");
        File.ReadAllBytes(file).Should().Equal(before, "пересчёт не перезаписывает нечитаемый файл молча");
    }

    // ─── Помощники ──────────────────────────────────────────────────────────

    private string _wikiRoot => WikiPaths.Root(_dir, "u1");

    private static string Plain(string what) => $"{what} без формулировок и оценок";

    private static StoredUserMessage U(string text, bool auto = false, bool sysDir = false, bool viaAgent = false, string[]? attached = null) =>
        new(text, attached, viaAgent: viaAgent ? true : null, systemDirective: sysDir ? true : null, auto: auto ? true : null);

    private static StoredTextMessage T(string text) => new(text);

    private static StoredToolUseMessage Tool(string name, string? path = null) =>
        new() { Name = name, Input = path is null ? null : JsonSerializer.SerializeToElement(new { path }) };

    private static List<StoredMessage> ShortChat() => [U("короткая реплика"), T("короткий ответ")];

    // Диалог в примерах держится выше порога ступени 0 (1 500 символов): тесты про балл
    // не должны спотыкаться об исключение по длине.
    private static List<StoredMessage> PlainTurns(int n) =>
        [.. Enumerable.Range(1, n).Select(i => (StoredMessage)U(Plain($"реплика номер {i} про логистику склада"))), T(new string('а', 1_500))];

    // Содержательный чат: много реплик, substantive, решения, правки, заметки, документы —
    // заведомо выше порога «разбирается» при любых разумных весах.
    private static List<StoredMessage> RichChat() =>
    [.. PlainTurns(8),
        U(Plain("пятая реплика про логистику склада и про поставку овощей на склад") + new string('д', 300)),
        U(Plain("шестая реплика про логистику склада и про поставку овощей на склад") + new string('д', 300)),
        U("стоп, так не пойдёт"),
        U("Нет, решили иначе: договорились держать прежний порядок и не делаем новый виток"),
        U("итог такой: оставляем вариант А"),
        U("выбираем вариант Б"),
        Tool("mcp__notes__notes_create"),
        Tool("mcp__memory__memory_remember"),
        Tool("Read", "docs/регламент-1.md"),
        Tool("Edit", "docs/регламент-1.md"),
        T(new string('а', 6_000))];

    private static WikiChatScorer.WikiChatInput In(
        IReadOnlyList<StoredMessage> messages,
        bool taskExec = false, string? mark = null, bool pinned = false, bool summary = false,
        int streak = 0) =>
        new(taskExec, mark, pinned, summary, null, null, Base, Base, streak, messages);

    private static WikiChatScorer.WikiChatScoreResult Score(WikiChatScorer.WikiChatInput input) =>
        WikiChatScorer.Score(input);

    // Метка сигнала — та же таблица, что в причине скорера (проверяем её по месту).
    private static string SignalLabel(string name) => name switch
    {
        WikiChatScorer.SignalDecisionWords => "формулировки решений",
        WikiChatScorer.SignalHumanTurns => "вовлечённость человека",
        WikiChatScorer.SignalSubstantiveTurns => "содержательные реплики",
        WikiChatScorer.SignalCourseCorrections => "правки направления",
        WikiChatScorer.SignalWentToNotesTasks => "ушло в заметки и задачи",
        WikiChatScorer.SignalDocumentWork => "работа с документами",
        WikiChatScorer.SignalAnswerVolume => "объём содержательного ответа",
        WikiChatScorer.SignalTopicReturn => "возврат к теме",
        WikiChatScorer.SignalLinks => "связи",
        WikiChatScorer.SignalNothingNewPenalty => "«нечего добавить» подряд",
        _ => name,
    };

    private void SeedChats()
    {
        _sessions.Known["chat-a"] = new Session { Id = "chat-a", OwnerId = "u1", Name = "Чат А", CreatedAt = Base, UpdatedAt = Base };
        _sessions.Known["chat-b"] = new Session { Id = "chat-b", OwnerId = "u1", Name = "Чат Б", CreatedAt = Base, UpdatedAt = Base };
        _sessions.Known["chat-c"] = new Session { Id = "chat-c", OwnerId = "u1", Name = "Чат В", TaskExecution = true, CreatedAt = Base, UpdatedAt = Base };
        _sessions.Known["chat-foreign"] = new Session { Id = "chat-foreign", OwnerId = "u2", Name = "Чужой", CreatedAt = Base, UpdatedAt = Base };
        _sessions.History["chat-a"] = RichChat();
        _sessions.History["chat-b"] = [U("Разбери протокол встречи"), T(new string('с', 12_000))];
        _sessions.History["chat-c"] = RichChat();
        _sessions.History["chat-foreign"] = RichChat();
    }

    private WikiChatsController Controller(string user = "u1")
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, user)], "test"));
        return new WikiChatsController(_sessions, _chat, new WikiChatRecalc(_sessions, _config), _config, new WikiFlagOn())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private sealed class SessionsStub : ISessionDirectory
    {
        public readonly Dictionary<string, Session> Known = new();
        public readonly Dictionary<string, IReadOnlyList<StoredMessage>> History = new();
        public Session? GetById(string id) => Known.TryGetValue(id, out var s) ? s : null;
        public IReadOnlyCollection<Session> GetAll() => Known.Values;
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>(History.TryGetValue(sessionId, out var h) ? h : []);
        public bool SetWikiMark(string sessionId, string? mark) => Known.ContainsKey(sessionId);
    }

    // Стабы для сборки настоящего сервиса разбора (он в теневых тестах не вызывается):
    // модель, которая обязана не вызываться, и пустые справочники с включённым флагом вики.
    private sealed class NoopCheapRunner : ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "none";
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Throw<string>(actionKey);
        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) => Throw<string?>(actionKey);
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Throw<OneShotResult>(actionKey);
        private static Task<T> Throw<T>(string actionKey) =>
            Task.FromException<T>(new InvalidOperationException($"ИИ-действие {actionKey} здесь не ожидается"));
    }

    private sealed class NoSpheres : ISphereDirectory
    {
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public bool Enabled(string ownerId) => false;
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }

    private sealed class WikiFlagOn : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }
}
