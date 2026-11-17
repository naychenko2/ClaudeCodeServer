using System.Globalization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// S2-8b: один проход синтеза вместо A/B (решение Андрея 10.11 №1). Пути к инварианту «ни одна
// операция синтеза не попадает в пакет мимо ValidateSynth»: первый вызов, повтор при пустом
// разделе, ремонт. Инвариант 2 — ночь не зовёт облако. Инварианты 3–4 — kind/as_of доходят до
// операции пакета (первый вызов и ремонт), у каждой операции непустые Text (= Claim) и Quotes,
// пакет проходит WikiPackage.ShapeErrors без ошибок.
public class WikiIngestSynthTests : IDisposable
{
    private const string User = "u1";
    private const string Section = "projects/p1";
    private const string CardTitle = "Карточка чата";
    private const string CardPath = "Проекты/p1/Источники/Карточка-чата.md";
    private const string PageTitle = "Сущность";
    private const string PagePath = "Проекты/p1/Сущность.md";
    private const string EmptySection = "projects/p2";
    private const string EmptyCardPath = "Проекты/p2/Источники/Карточка-чата.md";

    private const string SourceText =
        "Решение: перенести сборку на Linux. Прежний план был остаться на Windows. Контейнер собран на второй машине.";
    private const string Quote1 = "Решение: перенести сборку на Linux";
    private const string Quote2 = "Контейнер собран на второй машине";
    private const string Claim1 = "Сборку решено перенести на Linux";

    private static readonly string CardFile = """
        ---
        type: source
        origin: session
        ref: session:s1#m1-9
        hash: 0123456789012345678901234567890123456789012345678901234567890123
        captured: 2026-10-29
        written: 2026-11-10T12:00:00Z
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Карточка чата
        """;

    private static readonly string PageBase = $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{PageTitle}}

        ## Сводка
        Прежний план был остаться на Windows [^s1]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[{{CardTitle}}]] — «Прежний план был остаться на Windows»
        """;

    private readonly string _dir;
    private readonly string _roots; // рабочие каталоги проектов — вне {data} (тот же порядок, что в WikiSourceCaptureTests)
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly WikiApplyService _apply;

    public WikiIngestSynthTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_ingest_synth_" + Guid.NewGuid().ToString("N"));
        _roots = Path.Combine(Path.GetTempPath(), "wiki_ingest_synth_roots_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_roots);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, null, flags: new WikiFlagOn());
        _keeper = new WikiKeeper(_notes, _config);
        _apply = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
        Write(Section, CardPath, CardFile);
        Write(Section, PagePath, PageBase);
    }

    public void Dispose()
    {
        foreach (var d in new[] { _dir, _roots })
        {
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
        }
    }

    private string Root => WikiPaths.Root(_dir, User);

    private void Write(string sectionKey, string pageKey, string content)
    {
        var r = _keeper.WritePageAsync(User, sectionKey, pageKey, content).GetAwaiter().GetResult();
        r.Status.Should().Be(WikiOpStatus.Applied, r.Reason);
    }

    private static WikiIngestService.Request Req(string home = Section, string card = CardPath) =>
        new("session", "session:s1#m1-9", card, SourceText, home, null);

    private WikiIngestService Sut(FakeLlm llm) => new(llm, new NoSpheres(), _apply, _config);

    private IReadOnlyList<WikiPackage> PackagesOnDisk() => WikiPackageStore.List(Root).Packages;

    // 20 открытых пакетов на всю вики (лимит ADR-035, решение 4): следующий Create отказан
    // (CodeLimit) — так ставится сбой хранилища пакета на живом пути разбора. Окна у пакетов
    // разные: на одном окне двух открытых попыток быть не может (гашение, дефект 03a50f53),
    // одинаковая ref ломала бы саму фикстуру.
    private void FillOpenPackages()
    {
        for (var i = 0; i < WikiPackage.MaxOpenPackages; i++)
        {
            var created = WikiPackageStore.Create(Root, new WikiPackage
            {
                Id = WikiPackageStore.NewId(),
                Section = Section,
                Sources = [new WikiPackage.SourceRef("session", $"session:s{i}#m1-1")],
                Operations =
                [
                    new WikiPackage.Operation
                    {
                        Op = WikiPackage.OpAppend,
                        Page = PagePath,
                        Text = "утверждение " + i,
                        Class = WikiPackage.ClassKey("session", WikiPackage.OpAppend),
                        Decision = WikiPackage.DecisionAuto,
                    },
                ],
                Status = WikiPackage.StatusChecking,
            });
            created.Ok.Should().BeTrue($"пакет для лимита обязан быть поставлен: {created.Reason ?? created.Code}");
        }
        WikiPackageStore.CountOpen(Root).Should().Be(WikiPackage.MaxOpenPackages);
    }

    // ── первый вызов ──

    [Fact]
    public async Task ПервыйВызов_Append_ПакетСClaimQuotes_ФормаБезОшибок_ОдинВызов()
    {
        var llm = new FakeLlm(WikiSynthJson.Append(PageTitle, Claim1, Quote1));

        var res = await Sut(llm).IngestAsync(User, Req());

        res.Outcome.Should().Be(WikiIngestService.OutcomeApplied, res.Reason);
        llm.Calls.Should().ContainSingle().Which.Pass.Should().Be("S");
        var pkg = WikiPackageStore.TryRead(Root, res.PackageId!).Package!;
        pkg.Operations.Should().NotBeEmpty();
        // Инвариант 4: Text = Claim, Quotes непусты, форма пакета — без ошибок.
        pkg.Operations.Should().OnlyContain(o => o.Text == Claim1 && o.Claim == Claim1
            && o.Quotes != null && o.Quotes.Count == 1 && o.Quotes[0] == Quote1);
        WikiPackage.ShapeErrors(pkg).Should().BeEmpty();
    }

    [Fact]
    public async Task ПервыйВызов_CreateState_KindИAsOfДоходятДоОперацииПакета()
    {
        // Инвариант 3: ключи kind и as_of ответа → SynthOp.Kind/AsOf → Operation.Kind/AsOf.
        var llm = new FakeLlm(WikiSynthJson.Create("Состояние сборки", Claim1, Quote1, "state", "1999-01-01"));

        var res = await Sut(llm).IngestAsync(User, Req());

        res.Outcome.Should().BeOneOf(WikiIngestService.OutcomeApplied, WikiIngestService.OutcomeAwaitingHuman);
        var pkg = WikiPackageStore.TryRead(Root, res.PackageId!).Package!;
        var op = pkg.Operations.Should().ContainSingle().Which;
        op.Kind.Should().Be("state");
        op.AsOf.Should().Be("2026-11-10", "as_of ставит код из written карточки; значение модели (1999) игнорируется");
        WikiPackage.ShapeErrors(pkg).Should().BeEmpty();
    }

    [Fact]
    public async Task СтатеБезДатыУИсточника_AsOfМодели_НеПодставляется_ОперацияОтклонена()
    {
        // Карточка без written: даты у кода нет, значение модели не спасает state.
        Write(Section, CardPath, CardFile.Replace("written: 2026-11-10T12:00:00Z\n", ""));
        var llm = new FakeLlm(WikiSynthJson.Create("Состояние сборки", Claim1, Quote1, "state", "2026-11-10"),
            repair: WikiSynthJson.Create("Состояние сборки", Claim1, Quote1, "state", "2026-11-10"));

        var res = await Sut(llm).IngestAsync(User, Req());

        res.Outcome.Should().Be(WikiIngestService.OutcomeRejected);
        res.Reason.Should().Contain("as_of");
    }

    // ── источник-заметка: дату ставит код из даты записи заметки ──

    [Fact]
    public async Task Заметка_Источник_AsOfРавнаДатеЗаписиЗаметки_НеДатеМодели()
    {
        // Критерий c2 (C2R-Sh4): цепочка даты заметки — UpdatedAt (mtime файла) → written
        // карточки (CaptureNote) → WithCodeAsOf → AsOf операции пакета. Значение модели (1999)
        // игнорируется. Мутация «не ставить дату» (WithCodeAsOf, asOf = null): state без as_of
        // отклоняется — тест красный.
        var project = _projects.Create("П-note", Path.Combine(_roots, "proj_note"), User, User,
            createDirectory: true);
        var note = _notes.Create(User, new CreateNoteRequest("Заметка о сборке", SourceText, project.Id));
        var cap = await new WikiSourceCapture(_notes, _projects, new NoSessions(), _config, _keeper)
            .CaptureNote(User, note.Id);
        cap.Ok.Should().BeTrue(cap.Reason);

        var llm = new FakeLlm(WikiSynthJson.Create("Состояние сборки", Claim1, Quote1, "state", "1999-01-01"));

        var res = await Sut(llm).IngestAsync(User,
            new("note", "note:" + note.Id, cap.CardRelPath!, cap.SourceText!, "projects/" + project.Id, null));

        res.Outcome.Should().BeOneOf(WikiIngestService.OutcomeApplied, WikiIngestService.OutcomeAwaitingHuman);
        var pkg = WikiPackageStore.TryRead(Root, res.PackageId!).Package!;
        var op = pkg.Operations.Should().ContainSingle().Which;
        // Та же дата и тот же формат, что ставит код (WithCodeAsOf): ГГГГ-ММ-ДД по UTC.
        var noteDate = DateTimeOffset.Parse(note.UpdatedAt, CultureInfo.InvariantCulture)
            .UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        op.AsOf.Should().Be(noteDate, "as_of — дата записи заметки (written карточки), значение модели (1999) игнорируется");
        WikiPackage.ShapeErrors(pkg).Should().BeEmpty();
    }

    // ── повтор при пустом разделе ──

    [Fact]
    public async Task ПустойРаздел_Повтор_ПланСоздаётСтраницу_KindИAsOfДоходят()
    {
        Write(EmptySection, EmptyCardPath, CardFile);
        var llm = new FakeLlm(WikiSynthJson.Ok(),
            emptySection: WikiSynthJson.Create("Состояние сборки", Claim1, Quote1, "state", "1999-01-01"));

        var res = await Sut(llm).IngestAsync(User, Req(EmptySection, EmptyCardPath));

        llm.Calls.Select(c => c.Pass).Should().Equal("S", "E");
        res.Outcome.Should().Be(WikiIngestService.OutcomeApplied, res.Reason);
        var op = WikiPackageStore.TryRead(Root, res.PackageId!).Package!.Operations.Should().ContainSingle().Which;
        op.Kind.Should().Be("state");
        op.AsOf.Should().Be("2026-11-10");
        op.Text.Should().Be(Claim1);
        op.Quotes.Should().Equal(Quote1);
    }

    // ── ремонт ──

    [Fact]
    public async Task Ремонт_ЦитатаНеДословна_ПричинаВПромптеРемонта_КindДоходит_AsOfСтавитКод()
    {
        var bad = WikiSynthJson.Create("Состояние сборки", Claim1, "Цитаты в источнике нет вообще", "state", "1998-02-02");
        var fixedPlan = WikiSynthJson.Create("Состояние сборки", Claim1, Quote1, "state", "1999-01-01");
        var llm = new FakeLlm(bad, repair: fixedPlan);

        var res = await Sut(llm).IngestAsync(User, Req());

        llm.Calls.Select(c => c.Pass).Should().Equal("S", "R");
        llm.Calls[1].Prompt.Should().Contain("отсутствует в источнике дословно", "причина отказа кода видна в ремонте");
        res.Outcome.Should().Be(WikiIngestService.OutcomeAwaitingHuman, res.Reason);
        var pkg = WikiPackageStore.TryRead(Root, res.PackageId!).Package!;
        var op = pkg.Operations.Should().ContainSingle().Which;
        op.Decision.Should().Be(WikiPackage.DecisionInbox, "после ремонта пакет — во «Входящие»");
        op.Kind.Should().Be("state");
        op.AsOf.Should().Be("2026-11-10", "as_of — дата карточки, не значение модели ни из первого ответа, ни из ремонта");
        WikiPackage.ShapeErrors(pkg).Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"outcome":"ok","operations":[{"op":"append","page":"Сущность","claim":"Сборку решено перенести"}]}""")]
    [InlineData("""{"outcome":"ok","operations":[{"op":"append","page":"Сущность","claim":"Сборку решено перенести","quotes":[]}]}""")]
    public async Task ОперацияБезQuotes_НеВПакет_ПричинаВидаВРемонте(string noQuotes)
    {
        // Ключа quotes нет или он пуст: ParseSynthPlan отдаёт пустой список, ValidateSynth отказывает.
        var llm = new FakeLlm(noQuotes, repair: WikiSynthJson.Nothing("нечего"));

        var res = await Sut(llm).IngestAsync(User, Req());

        llm.Calls.Select(c => c.Pass).Should().Equal("S", "R");
        llm.Calls[1].Prompt.Should().Contain("утверждение без цитат");
        res.Outcome.Should().Be(WikiIngestService.OutcomeNothingNew);
        PackagesOnDisk().Should().BeEmpty("операция без цитат в пакет не попала");
    }

    // ── поштучный приём ──

    private const string BadQuote = "Цитаты в источнике нет вообще";

    [Fact]
    public async Task Поштучно_ИзТрёхОднаНеДословна_ВПакетДве_БезРемонта_ПричинаВDropped()
    {
        var plan = WikiSynthJson.Ok(
            WikiSynthJson.Op("append", PageTitle, Claim1, [Quote1]),
            WikiSynthJson.Op("append", PageTitle, "Другое утверждение", [BadQuote]),
            WikiSynthJson.Op("append", PageTitle, "Контейнер собран отдельно", [Quote2]));
        var llm = new FakeLlm(plan);

        var res = await Sut(llm).IngestAsync(User, Req());

        llm.Calls.Select(c => c.Pass).Should().Equal("S");
        res.Outcome.Should().NotBe(WikiIngestService.OutcomeRejected, res.Reason);
        res.Dropped.Should().Be(1);
        res.DropReasons.Should().ContainSingle().Which.Should().Contain("отсутствует в источнике дословно");
        var pkg = WikiPackageStore.TryRead(Root, res.PackageId!).Package!;
        pkg.Operations.Select(o => o.Claim)
            .Should().BeEquivalentTo(new[] { Claim1, "Контейнер собран отдельно" }, "в пакете ровно две принятые операции");
    }

    [Fact]
    public async Task Поштучно_ВсеПринятыДва_Dropped0()
    {
        var plan = WikiSynthJson.Ok(
            WikiSynthJson.Op("append", PageTitle, Claim1, [Quote1]),
            WikiSynthJson.Op("append", PageTitle, "Контейнер собран отдельно", [Quote2]));
        var llm = new FakeLlm(plan);

        var res = await Sut(llm).IngestAsync(User, Req());

        res.Dropped.Should().Be(0);
        res.DropReasons.Should().BeNull();
        WikiPackageStore.TryRead(Root, res.PackageId!).Package!.Operations.Should().HaveCount(2);
    }

    // Минор ревью C2R-Sh1, п.3: сбой хранилища пакета не отменяет, что часть операций была
    // отброшена при его сборке. Мутация «вернуть RejectedUsed(...) без WithSectionIssues» →
    // Dropped 0 и пустые причины, тест красный.
    [Fact]
    public async Task Поштучно_МагазинНеПринялПакет_DroppedИПричиныДоходят()
    {
        var plan = WikiSynthJson.Ok(
            WikiSynthJson.Op("append", PageTitle, Claim1, [Quote1]),
            WikiSynthJson.Op("append", PageTitle, "Другое утверждение", [BadQuote]),
            WikiSynthJson.Op("append", PageTitle, "Третье утверждение", [BadQuote]));
        FillOpenPackages();
        var llm = new FakeLlm(plan);

        var res = await Sut(llm).IngestAsync(User, Req());

        res.Outcome.Should().Be(WikiIngestService.OutcomeRejected, res.Reason);
        res.Reason.Should().Contain("магазин пакетов не принял пакет");
        res.Dropped.Should().Be(2, "отказ записи пакета не отменяет двух отброшенных при его сборке операций");
        res.DropReasons.Should().NotBeEmpty("причины отброшенных доходят и на этом пути");
        res.InputTokens.Should().BeGreaterThan(0, "вызов модели был — расход понесён");
    }

    [Fact]
    public async Task Поштучно_РемонтДалПланСОдной_Плохой_ПринятаяВПакете_ОтброшеннаяВDropped()
    {
        var bad = WikiSynthJson.Append(PageTitle, Claim1, BadQuote);
        var repaired = WikiSynthJson.Ok(
            WikiSynthJson.Op("append", PageTitle, Claim1, [Quote1]),
            WikiSynthJson.Op("append", PageTitle, "Другое утверждение", [BadQuote]),
            WikiSynthJson.Op("append", PageTitle, "Контейнер собран отдельно", [Quote2]));
        var llm = new FakeLlm(bad, repair: repaired);

        var res = await Sut(llm).IngestAsync(User, Req());

        llm.Calls.Select(c => c.Pass).Should().Equal("S", "R");
        res.Outcome.Should().Be(WikiIngestService.OutcomeAwaitingHuman, res.Reason);
        res.Dropped.Should().Be(1);
        var pkg = WikiPackageStore.TryRead(Root, res.PackageId!).Package!;
        pkg.Operations.Should().HaveCount(2).And.OnlyContain(o => o.Decision == WikiPackage.DecisionInbox);
    }

    // C2R-Sh2b (M2): Operations исхода — число операций, ждущих человека (inbox), а не всех
    // операций пакета. Мутация «Operations = всего в пакете» → красное (там было бы 2).
    [Fact]
    public async Task AwaitingHuman_Operations_СчитаетТолькоСпорные_ПакетИзAutoИInbox()
    {
        var plan = WikiSynthJson.Ok(
            WikiSynthJson.Op("append", PageTitle, Claim1, [Quote1]),
            WikiSynthJson.Op("create", "Состояние сборки", "Контейнер собран отдельно", [Quote2], kind: "state"));
        // append класса session×append выключен человеком → inbox; create-state идёт как auto.
        WikiSettingsStore.Mutate(_dir, User, st => st with
        {
            Classes = [new WikiSettingsStore.ClassState(
                WikiPackage.ClassKey("session", WikiPackage.OpAppend), true,
                "класс выключен человеком", DateTimeOffset.UtcNow, null)],
        });
        var llm = new FakeLlm(plan);

        var res = await Sut(llm).IngestAsync(User, Req());

        var pkg = WikiPackageStore.TryRead(Root, res.PackageId!).Package!;
        pkg.Operations.Count(o => o.Decision == WikiPackage.DecisionAuto).Should().Be(1, "предпосылка: в пакете одна auto");
        pkg.Operations.Count(o => o.Decision == WikiPackage.DecisionInbox).Should().Be(1, "предпосылка: и одна inbox");
        res.Outcome.Should().Be(WikiIngestService.OutcomeAwaitingHuman, res.Reason);
        res.Operations.Should().Be(1);
    }

    [Fact]
    public async Task Поштучно_РемонтНичегоНеПринял_Rejected_СПервойПричиной()
    {
        var bad = WikiSynthJson.Append(PageTitle, Claim1, BadQuote);
        var llm = new FakeLlm(bad, repair: bad);

        var res = await Sut(llm).IngestAsync(User, Req());

        res.Outcome.Should().Be(WikiIngestService.OutcomeRejected);
        res.Reason.Should().Contain("отсутствует в источнике дословно");
        res.Dropped.Should().Be(0);
        PackagesOnDisk().Should().BeEmpty();
    }

    // ── разбор ответа ──

    [Fact]
    public async Task ОтветОбрезанПоЛимиту_ОтказОтветНеРазобран_БезПакета()
    {
        var cut = WikiSynthJson.Append(PageTitle, Claim1, Quote1);
        var llm = new FakeLlm(cut[..(cut.Length - 20)]);

        var res = await Sut(llm).IngestAsync(User, Req());

        res.Outcome.Should().Be(WikiIngestService.OutcomeRejected);
        res.Reason.Should().Contain("ответ модели не разобран");
        llm.Calls.Should().HaveCount(1);
        PackagesOnDisk().Should().BeEmpty("частичного пакета нет");
    }

    // ── гонка ──

    [Fact]
    public async Task SameSource_Parallel_ВторойНеЗапускаетСвойРазбор()
    {
        // Мутация «снять InFlight» (WikiIngestService.IngestAsync): второй вызов идёт в модель сам.
        var llm = new FakeLlm(WikiSynthJson.Append(PageTitle, Claim1, Quote1));
        var sut = Sut(llm);

        var results = await Task.WhenAll(sut.IngestAsync(User, Req()), sut.IngestAsync(User, Req()));

        llm.Calls.Should().HaveCount(1, "один источник — один прогон; вторая заявка получает его результат");
        results.Should().OnlyContain(r => r.Outcome == WikiIngestService.OutcomeApplied);
        results[0].PackageId.Should().Be(results[1].PackageId);
        PackagesOnDisk().Should().HaveCount(1);
    }

    // ── ночь без облака ──

    [Fact]
    public async Task Ночь_ОблачныйМаршрут_ОтказБезВызоваМодели()
    {
        var llm = new FakeLlm(WikiSynthJson.Append(PageTitle, Claim1, Quote1)) { LocalRoute = false };

        var res = await Sut(llm).IngestAsync(User, Req(), actionKey: LocalActionCatalog.WikiNightly);

        res.Outcome.Should().Be(WikiIngestService.OutcomeRejected);
        res.Reason.Should().Contain("ночной разбор только локальной моделью");
        llm.Calls.Should().BeEmpty("облако ночью не зовётся ни одним шагом цепочки");
        PackagesOnDisk().Should().BeEmpty();
    }

    [Fact]
    public async Task Ночь_ЛокальныйМаршрут_Разбирает()
    {
        var llm = new FakeLlm(WikiSynthJson.Append(PageTitle, Claim1, Quote1)) { LocalRoute = true };

        var res = await Sut(llm).IngestAsync(User, Req(), actionKey: LocalActionCatalog.WikiNightly);

        res.Outcome.Should().Be(WikiIngestService.OutcomeApplied, res.Reason);
        llm.Calls.Should().ContainSingle();
    }

    // ── сторож И3, расширение J-J3a: ночь + проход судьи ──

    [Fact]
    public async Task НочьИПроходСудьи_NightlyНикогдаНеОблаком_СудьяЗовётТолькоWikiJudge()
    {
        // Класс выключен человеком → append идёт во «Входящие» с допущенным кодом
        // class_disabled: это кандидат прохода судьи (J-J3a). Ночь — на локальном маршруте.
        // Мутация «судья зовёт место wiki-nightly» (RunDetailedAsync с ключом ночи) или
        // «ночь уходит в облачный шаг» (RunDetailedAsync вместо RunLocalOrChosen) — краснит
        // этот тест (проверено на копии).
        WikiSettingsStore.Mutate(_dir, User, st => st with
        {
            Classes = [new WikiSettingsStore.ClassState(
                WikiPackage.ClassKey("session", WikiPackage.OpAppend), true,
                "класс выключен человеком", DateTimeOffset.UtcNow, null)],
        });
        var llm = new FakeLlm(WikiSynthJson.Append(PageTitle, Claim1, Quote1),
            judge: """{"verdicts":[{"index":0,"decision":"apply","why":"цитаты подтверждают"}]}""");

        var res = await Sut(llm).IngestAsync(User, Req(), actionKey: LocalActionCatalog.WikiNightly);
        res.Outcome.Should().Be(WikiIngestService.OutcomeAwaitingHuman, res.Reason);
        var pkgId = res.PackageId!;

        var sweep = await new WikiJudgeSweep(llm, _apply, _config).RunAsync(User, Root);

        llm.Calls.Should().NotContain(c => c.ActionKey == LocalActionCatalog.WikiNightly && c.Route != "local",
            "И3: ни один шаг ночи не уходит в облако");
        llm.Calls.Should().NotContain(c => c.ActionKey == LocalActionCatalog.WikiJudge && c.Route != "detailed",
            "судья зовёт обычную цепочку места (RunDetailedAsync), а не локальный шаг ночи");
        llm.Calls.Select(c => c.ActionKey).Distinct().Should().BeEquivalentTo(
            new[] { LocalActionCatalog.WikiNightly, LocalActionCatalog.WikiJudge },
            "за ночной проход и проход судьи звались ровно эти два места: судья — только своим");
        sweep.Calls.Should().Be(1);
        var pkg = WikiPackageStore.TryRead(Root, pkgId).Package!;
        pkg.Operations[0].JudgeDecision.Should().Be(WikiPackage.HumanAccepted,
            "вердикт дошёл до стора: сквозной путь ночной операции закрыт");
    }

    // ── Q1-S2 (1А): каталог промпта из снимка раздела ──

    // Страница знаний с заполненной «Сводкой» (без summary в шапке): сводка каталога
    // проектируется из неё.
    private static string KnowledgePage(string title, string swodka) =>
        $"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {title}

        ## Сводка
        {swodka}

        ## Хронология

        ## Противоречия

        ## История

        """;

    [Fact]
    public void КаталогПромпта_200Страниц_ПотолокКарточекНетНоваяПоследняя()
    {
        // 200 страниц знаний с длинными «Сводками» + карточка источника + 201-я новая
        // страница. Порядок записи — обратный именам: порядок каталога проверяет именно
        // created, а не путь. Мутация «снять потолок» (PromptCatalogMaxChars → int.MaxValue):
        // все строки получают сводки, каталог перерастает потолок — первый assert красный.
        for (var i = 199; i >= 0; i--)
            Write(Section, $"Проекты/p1/П{i:000}.md", KnowledgePage($"П{i:000}", new string('с', 160)));
        Write(Section, "Проекты/p1/ПНовая.md", KnowledgePage("ПНовая", new string('с', 160)));

        var upChain = WikiSections.UpChain(Section, User, new NoSpheres());
        var snapshot = WikiIngestService.ReadSectionState(Root, Section, upChain);
        var catalog = WikiIngestService.BuildSectionIndex(Root, Section, upChain, snapshot);

        // Потолок — договорённость, она названа числом: мутация «снять потолок»
        // (PromptCatalogMaxChars → int.MaxValue) краснит именно эту строку.
        WikiIngestService.PromptCatalogMaxChars.Should().Be(14_000);
        catalog.Length.Should().BeLessOrEqualTo(14_000,
            "потолок каталога промпта (риск 1 разреза): окно вместе с ним помещается в контекст места");
        catalog.Should().NotContain("— source").And.NotContain(CardTitle,
            "карточки source в каталог промпта не входят");

        var titles = catalog.Split('\n')
            .Where(l => l.StartsWith("- [[", StringComparison.Ordinal))
            .Select(l => l[4..l.IndexOf("]]", StringComparison.Ordinal)])
            .ToList();
        var expected = new List<string> { PageTitle };
        for (var i = 199; i >= 0; i--) expected.Add($"П{i:000}");
        expected.Add("ПНовая");
        titles.Should().Equal(expected,
            "порядок — по created, новые в конце: позиция не зависит от имени и правок содержания");
        titles.Should().HaveCount(202, "потолок не покупается выбрасыванием хвоста: новая страница в каталоге");
    }

    [Fact]
    public void КаталогПромпта_ПотолокОбщийДляРазделаНазначенияИЦепочкиВверх()
    {
        // Раздел назначения съедает потолок своими сводками; цепочка вверх обязана получить
        // остаток — голые строки новых страниц. Сводки назначения — тоже длина каталога:
        // при их невписании в бюджет следующего раздела суммарный каталог перерастает
        // потолок (мутация «учёт без приписанных сводок» → первый assert красный).
        for (var i = 0; i < 100; i++)
            Write(Section, $"Проекты/p1/Т{i:000}.md", KnowledgePage($"Т{i:000}", new string('с', 160)));
        for (var i = 0; i < 10; i++)
            Write("common", $"Общее/В{i:000}.md", KnowledgePage($"В{i:000}", new string('с', 160)));

        var upChain = WikiSections.UpChain(Section, User, new NoSpheres());
        var snapshot = WikiIngestService.ReadSectionState(Root, Section, upChain);
        var catalog = WikiIngestService.BuildSectionIndex(Root, Section, upChain, snapshot);

        catalog.Length.Should().BeLessOrEqualTo(14_000,
            "потолок считается суммарно по разделу назначения и цепочке вверх");
        catalog.Should().Contain("[[В009]]", "новая страница цепочки вверх остаётся, даже когда потолок плотный");
    }

    [Fact]
    public void КаталогПромпта_СтраницаПослеСнимка_ВКаталогОкнаНеПопадает()
    {
        // Каталог и кандидаты — один снимок под шлюзом раздела. Страница, появившаяся на
        // диске после снимка (Хранитель перегенерировал её _index.md — прежняя реализация
        // читала каталог оттуда и пустила бы её), в каталог этого окна не входит.
        var upChain = WikiSections.UpChain(Section, User, new NoSpheres());
        var snapshot = WikiIngestService.ReadSectionState(Root, Section, upChain);

        Write(Section, "Проекты/p1/ПослеСнимка.md", KnowledgePage("ПослеСнимка", "то, чего в снимке нет"));

        var catalog = WikiIngestService.BuildSectionIndex(Root, Section, upChain, snapshot);
        catalog.Should().NotContain("ПослеСнимка", "второй точки правды о состоянии раздела в окне нет");
    }

    [Fact]
    public void КаталогПромпта_ПустойРаздел_КаталогаНетВовсе()
    {
        // Пустой раздел был в промпте без описания и раньше: ни заголовка, ни строк.
        // На этом держатся точные оценки ночи: пустой раздел не платит каталога.
        var upChain = WikiSections.UpChain(EmptySection, User, new NoSpheres());
        var snapshot = WikiIngestService.ReadSectionState(Root, EmptySection, upChain);
        WikiIngestService.BuildSectionIndex(Root, EmptySection, upChain, snapshot).Should().BeEmpty();
    }

    // ── подмена модели ──

    private sealed class FakeLlm : ICheapTextRunner
    {
        // Route — метод шва, которым позвали: "local" (RunLocalOrChosen*, без облачного
        // шага), "detailed"/"cloud" (полная цепочка). Сторож И3 различает по нему, каким
        // путём прошло место: ключ сам по себе этого не говорит.
        public List<(string ActionKey, string Prompt, string Pass, string Route)> Calls { get; } = [];
        public bool LocalRoute { get; init; } = true;

        private readonly Dictionary<string, Queue<string>> _answers = new();

        public FakeLlm(string synth, string? repair = null, string? emptySection = null, string? judge = null)
        {
            _answers["S"] = new Queue<string>([synth]);
            _answers["R"] = new Queue<string>([repair ?? synth]);
            _answers["E"] = new Queue<string>([emptySection ?? "нет ответа для прохода E"]);
            _answers["J"] = new Queue<string>([judge ?? """{"verdicts":[]}"""]);
        }

        public bool UsesLocal(string actionKey) => LocalRoute;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(Answer(actionKey, prompt, "cloud"));

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult<string?>(Answer(actionKey, prompt, "local"));

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(new OneShotResult(
                Answer(actionKey, prompt, "detailed"), new OneShotUsage(10, 0, 0, 5, 0.0, "fake"), 1));

        private string Answer(string actionKey, string prompt, string route)
        {
            var pass = prompt.Contains("СПОРНЫЕ ОПЕРАЦИИ ПАКЕТА") ? "J"
                : prompt.Contains("YOUR PREVIOUS PLAN WAS REJECTED") ? "R"
                : prompt.Contains("THE TARGET SECTION HAS NO KNOWLEDGE PAGES") ? "E"
                : prompt.Contains("single pass") ? "S"
                : "?";
            lock (Calls) Calls.Add((actionKey, prompt, pass, route));
            var queue = _answers.TryGetValue(pass, out var q) ? q : null;
            return queue is { Count: > 0 } ? queue.Dequeue() : "нет ответа для прохода " + pass;
        }
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

    // Каталог чатов пуст: захват заметки чаты не читает (CaptureNote зовёт только NotesService).
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
