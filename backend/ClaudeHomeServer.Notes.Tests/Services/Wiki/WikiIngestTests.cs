using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models;
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

// Очередь заявок разбора (S6b-3, ADR-035 §12б этапы 5–8 и «Результат разбора»; ADR-036 §3):
// раздел даёт дом источника (код), проходы A/B идут через подменённый ICheapTextRunner,
// план судит настоящий WikiPlanValidator, пакет кладется в настоящий WikiPackageStore,
// неспорное применяется настоящим WikiApplyService — цепочка проверяется целая, на диске.
//
// Критерии: c1 — в промпт прохода B попадает _index только раздела назначения и цепочки
// вверх (соседний проект и личное — нет); c2 — «нечего добавить» → пакета нет, исход
// nothing_new, ровно два вызова модели; c3 — план с нарушениями → ровно один ремонт, после
// него весь пакет в «Входящих», страниц на диске не тронуто; c4 — дом personal → отказ
// «личный раздел — с Ф2», ноль вызовов модели; c5 — корректный план → auto применён
// (страница на диске), usage разбора в пакете. Плюс гонка: две заявки одного источника —
// один прогон (two calls, один пакет), и недоверенный ответ модели — отказ с причиной.
// Плюс доработка ревью S6b-3: M1 — пересказ в text и дословная цитата в quote (нетривиальная
// фикстура): в пакет и на страницу идёт подтверждённая цитата, сноска — она; M2 — нарушения
// состояния раздела (битая ссылка на чужой странице) не зовут ремонт и не решают исход,
// они называются в причине. Плюс доработка S4c (ревью d857da6c): настройки владельца (общий
// выключатель, выключенные классы) читаются в бою и решают «само / Входящие»; битый
// settings.json — умолчания, не падение разбора.
public class WikiIngestTests : IDisposable
{
    private const string User = "u1";
    private const string Section = "projects/p1";
    private const string CardTitle = "Карточка чата";
    private const string CardPath = "Проекты/p1/Источники/Карточка-чата.md";
    private const string PageTitle = "Сущность";
    private const string PagePath = "Проекты/p1/Сущность.md";

    // Текст источника: по нему QuoteMatcher подтверждает цитату (мера 1).
    private const string SourceText =
        "Решение: перенести сборку на Linux. Прежний план был остаться на Windows. Контенер собран на второй машине.";

    // Утверждение нетривиальное (доработка ревью S6b-3, M1): text — пересказ (не подстрока
    // источника), quote — дословная выдержка. Промпт просит ровно этого
    // (WikiKeeperPrompts.cs:126); вырожденная фикстура text == quote маскировала то,
    // что пересказ до применения не доходит.
    private const string ClaimText = "Сборку решено перенести на Linux";
    private const string ClaimQuote = "Решение: перенести сборку на Linux";

    private static readonly string ClaimsJson =
        $$"""{"claims":[{"text":"{{ClaimText}}","quote":"{{ClaimQuote}}"}]}""";

    private static readonly string PlanJson = $$"""
        {"chronicle":{"about":0,"decided":[0],"learned":[],"open_questions":[]},
        "operations":[{"op":"append","page":"{{PageTitle}}","claim":0}]}
        """;

    // План с нарушением: append на страницу, которой в разделе нет — валидатор её отвергнет.
    private static readonly string BadPlanJson = """
        {"operations":[{"op":"append","page":"Несуществующая","claim":0}]}
        """;

    private static readonly string NothingJson =
        $$"""{"outcome":"{{WikiKeeperPrompts.NothingToAdd}}","reason":"не о чём писать"}""";

    // Карточка с именем: _index называет страницу её заголовком (# …), а не именем файла —
    // поэтому у заполняющих карточек других разделов заголовки обязаны быть своими.
    private static string CardFileTitled(string title) => $$"""
        ---
        type: source
        origin: session
        ref: session:s1#m1-9
        hash: 0123456789012345678901234567890123456789012345678901234567890123
        captured: 2026-10-29
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{title}}
        """;

    private static readonly string CardFile = CardFileTitled(CardTitle);

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

    // Страница с битой ссылкой (для M2): её нарушение — состояние раздела, а не вина плана
    // (WikiPlanValidator.cs:60-64): план её не создал и починить не может.
    private static readonly string BrokenLinkPage = $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Битая-страница

        ## Сводка
        Запись с битой ссылкой [[Несуществующая-страница]]

        ## Хронология

        ## Противоречия

        ## История
        """;

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly WikiApplyService _apply;

    public WikiIngestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_ingest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _notes = new NotesService(projects, _config, NullLogger<NotesService>.Instance, null, new WikiFlagOn());
        _keeper = new WikiKeeper(_notes, _config);
        _apply = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
        // Базовое состояние раздела назначения: карточка источника (цель сносок) и
        // страница знаний, которую план пополняет. Тот же проверенный shape, что в WikiApplyTests.
        Write(User, Section, CardPath, CardFile);
        Write(User, Section, PagePath, PageBase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Root => WikiPaths.Root(_dir, User);

    private void Write(string owner, string sectionKey, string pageKey, string content)
    {
        var r = _keeper.WritePageAsync(owner, sectionKey, pageKey, content).GetAwaiter().GetResult();
        r.Status.Should().Be(WikiOpStatus.Applied, r.Reason);
    }

    private static WikiIngestService.Request Req(
        string home = "projects/p1", string? target = null,
        string ref_ = "session:s1#m1-9", string card = CardPath) =>
        new("session", ref_, card, SourceText, home, target);

    private WikiIngestService Sut(FakeLlm llm) =>
        new(llm, new NoSpheres(), _apply, _config);

    private IReadOnlyList<WikiPackage> PackagesOnDisk() => WikiPackageStore.List(Root).Packages;

    private static string Sha256Of(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    // ── c1: в промпт прохода B — _index только раздела назначения и цепочки вверх ──

    [Fact]
    public async Task C1_ПромтПроходаB_видитТолькоРазделНазначенияИВерх()
    {
        // Соседний проект и личное: их _index есть на диске (тест генерирует сам) —
        // в промпт они не попадают (ADR-036, решение 3). «Общее» — вверх по цепочке: его
        // каталог в промпте обязан быть.
        Write(User, "projects/q1", "Проекты/q1/Источники/Соседняя-карточка.md", CardFileTitled("Соседняя-карточка"));
        Write(User, "personal", "Личное/Источники/Личная-карточка.md", CardFileTitled("Личная-карточка"));
        Write(User, "common", "Общее/Источники/Общая-карточка.md", CardFileTitled("Общая-карточка"));
        WikiProjections.RegenerateSection(Root, "projects/q1");
        WikiProjections.RegenerateSection(Root, "personal");

        var llm = new FakeLlm(claims: ClaimsJson, plan: NothingJson);
        var res = await Sut(llm).IngestAsync(User, Req());

        res.Outcome.Should().Be(WikiIngestService.OutcomeNothingNew);
        var promptB = llm.PromptOf("B");
        promptB.Should().Contain(PageTitle, "каталог раздела назначения — в промпте");
        promptB.Should().Contain("Общая-карточка", "цепочка вверх (common) — в промпте");
        promptB.Should().NotContain("Соседняя-карточка", "каталог соседнего проекта — вне промпта");
        promptB.Should().NotContain("Личная-карточка", "каталог личного раздела — вне промпта");
    }

    // ── c2: «нечего добавить» — пакет не создан, исход nothing_new, ровно два вызова ──

    [Fact]
    public async Task C2_НечегоДобавить_БезПакета_ДваВызоваМодели()
    {
        var llm = new FakeLlm(claims: ClaimsJson, plan: NothingJson);
        var res = await Sut(llm).IngestAsync(User, Req());

        res.Outcome.Should().Be(WikiIngestService.OutcomeNothingNew);
        res.Reason.Should().Be("не о чём писать", "причина модели доходит до вызывающего");
        llm.Calls.Should().HaveCount(2, "проходы A и B — и ничего больше");
        PackagesOnDisk().Should().BeEmpty("пакет при штатном исходе не создаётся");
    }

    // ── c3: план с нарушениями — ровно один ремонт, после него весь пакет в «Входящих» ──

    [Fact]
    public async Task C3_ПланСНарушениями_ОдинРемонт_ИВесьПакетВходящие()
    {
        var pageHashBefore = Sha256Of(Path.Combine(Root, "pages/" + PagePath));

        var llm = new FakeLlm(claims: ClaimsJson, plan: BadPlanJson, repair: PlanJson);
        var res = await Sut(llm).IngestAsync(User, Req());

        llm.Calls.Where(c => c.Pass == "B").Should().HaveCount(1);
        llm.Calls.Where(c => c.Pass == "R").Should().HaveCount(1, "ремонт — ровно одна попытка");
        res.Outcome.Should().Be(WikiIngestService.OutcomeAwaitingHuman);

        var packages = PackagesOnDisk();
        packages.Should().HaveCount(1);
        var pkg = packages[0];
        pkg.Status.Should().Be(WikiPackage.StatusProposed);
        pkg.Operations.Should().NotBeEmpty();
        pkg.Operations.Should().OnlyContain(o => o.Decision == WikiPackage.DecisionInbox,
            "пакет после ремонта модели — весь решает человек");

        Sha256Of(Path.Combine(Root, "pages/" + PagePath)).Should().Be(pageHashBefore,
            "на диске страниц не тронуто: спорное применяет только человек");
    }

    // ── c4: дом personal — отказ до единого вызова модели ──

    [Fact]
    public async Task C4_ЛичныйДом_ОтказБезВызоваМодели()
    {
        var llm = new FakeLlm(claims: ClaimsJson, plan: PlanJson);
        var res = await Sut(llm).IngestAsync(User, Req(home: "personal"));

        res.Outcome.Should().Be(WikiIngestService.OutcomeRejected);
        res.Reason.Should().Be("личный раздел — с Ф2");
        llm.Calls.Should().BeEmpty("отказ раздела — до обращения к модели");
        PackagesOnDisk().Should().BeEmpty();
    }

    // ── c5: корректный план — auto применён (страница на диске), usage в пакете ──

    [Fact]
    public async Task C5_КорректныйПлан_AutoПрименён_UsageВПакете()
    {
        var llm = new FakeLlm(claims: ClaimsJson, plan: PlanJson);
        var res = await Sut(llm).IngestAsync(User, Req());

        res.Outcome.Should().Be(WikiIngestService.OutcomeApplied, res.Reason);
        res.PackageId.Should().NotBeNullOrEmpty();

        var page = File.ReadAllText(Path.Combine(Root, "pages/" + PagePath));
        page.Should().Contain(ClaimQuote, "утверждение append'а — дословная цитата источника (M1)");

        var read = WikiPackageStore.TryRead(Root, res.PackageId!);
        read.State.Should().Be(WikiPackageFileState.Valid);
        read.Package!.Status.Should().Be(WikiPackage.StatusApplied);
        // usage разбора (два вызова × (10 вход / 5 выход)) — в каждой операции пакета.
        read.Package.Operations.Should().NotBeEmpty();
        read.Package.Operations.Should().OnlyContain(o =>
            o.InputTokens == 20 && o.OutputTokens == 10);
    }

    // ── N7d: анти-петля — откат человека за 7 дней запрещает автоприменение ──

    private void LogLine(string ts, string op, string actor, params string[] pages)
    {
        var path = Path.Combine(Root, "keeper", "log.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var arr = string.Join(",", pages.Select(p => "\"" + p + "\""));
        File.AppendAllText(path,
            $$"""{"ts":"{{ts}}","op":"{{op}}","section":"{{Section}}","pages":[{{arr}}],"actor":"{{actor}}"}""" + "\n");
    }

    private static string DaysAgo(int days) => DateTimeOffset.UtcNow.AddDays(-days).ToString("O");

    [Theory]
    [InlineData("revert")]
    [InlineData("retract")]
    public async Task N7d_ОткатЧеловекаВчера_ПакетВоВходящие_СПричинойИСтраницаНеТронута(string op)
    {
        LogLine(DaysAgo(1), op, "user", PagePath);
        var hashBefore = Sha256Of(Path.Combine(Root, "pages/" + PagePath));

        var res = await Sut(new FakeLlm(claims: ClaimsJson, plan: PlanJson)).IngestAsync(User, Req());

        res.Outcome.Should().Be(WikiIngestService.OutcomeAwaitingHuman, res.Reason);
        Sha256Of(Path.Combine(Root, "pages/" + PagePath)).Should().Be(hashBefore);
        var reasons = PackagesOnDisk().Single().Operations.Select(o => o.Reason).ToList();
        reasons.Should().ContainSingle().Which.Should().Contain("откатили вручную");
    }

    [Fact]
    public async Task N7d_ОткатНеЧеловека_НеБлокирует()
    {
        // Сторож старта пишет строки с другим actor: анти-петлю они не включают.
        LogLine(DaysAgo(1), "revert", "system", PagePath);

        var res = await Sut(new FakeLlm(claims: ClaimsJson, plan: PlanJson)).IngestAsync(User, Req());

        res.Outcome.Should().Be(WikiIngestService.OutcomeApplied, res.Reason);
    }

    private sealed class ShiftedTime(TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + shift;
    }

    [Fact]
    public async Task N7d_ПодменённыеЧасы_ТотЖеОткат_ЧерезВосемьДнейАвтоПрименён()
    {
        LogLine(DaysAgo(1), "revert", "user", PagePath);

        var res = await new WikiIngestService(new FakeLlm(claims: ClaimsJson, plan: PlanJson),
            new NoSpheres(), _apply, _config, new ShiftedTime(TimeSpan.FromDays(7))).IngestAsync(User, Req());

        res.Outcome.Should().Be(WikiIngestService.OutcomeApplied, res.Reason);
    }

    [Fact]
    public async Task N7d_ОткатВосемьДнейНазад_ОкноИстекло_ПрименяетсяСамо()
    {
        LogLine(DaysAgo(8), "revert", "user", PagePath);

        var res = await Sut(new FakeLlm(claims: ClaimsJson, plan: PlanJson)).IngestAsync(User, Req());

        res.Outcome.Should().Be(WikiIngestService.OutcomeApplied, res.Reason);
    }

    [Fact]
    public async Task N7d_БитаяСтрокаЖурнала_ПропускБезИсключения()
    {
        var path = Path.Combine(Root, "keeper", "log.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, "{обрыв посреди записи\n[1,2]\n");

        var res = await Sut(new FakeLlm(claims: ClaimsJson, plan: PlanJson)).IngestAsync(User, Req());

        res.Outcome.Should().Be(WikiIngestService.OutcomeApplied, res.Reason);
    }

    [Fact]
    public void N7d_ГраницаОкна_ОткатРовноНаГранице_НеСчитается_ПустойЖурнал_ПустоеМножество()
    {
        WikiJournalReader.ReadRecentHumanReverts(Root, DateTimeOffset.UtcNow.AddDays(-7))
            .Should().BeEmpty("журнала ещё нет");

        var ts = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        LogLine(ts.ToString("O"), "revert", "user", PagePath);

        WikiJournalReader.ReadRecentHumanReverts(Root, ts).Should().BeEmpty("окно полуоткрытое: ts == since — вне");
        WikiJournalReader.ReadRecentHumanReverts(Root, ts.AddMilliseconds(-1))
            .Should().ContainKey(PagePath).WhoseValue.Should().Be(ts);
    }

    // ── B4a: правка человека (ADR-038) — отдельный читатель, не откат ───────────────

    [Fact]
    public void ReadLastHumanEdits_БерётПоследнююПравкуЧеловека_BezОкна_ИгнорируетАвтоматикуИОткаты()
    {
        var t1 = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddDays(300); // «без окна»: правка почти годичной давности всё равно видна
        LogLine(t1.ToString("O"), "edit", "user", PagePath);
        LogLine(t2.ToString("O"), "restore", "user", PagePath);
        LogLine(t2.AddDays(1).ToString("O"), "edit", "keeper", PagePath);   // автоматика — не человек
        LogLine(t2.AddDays(2).ToString("O"), "revert", "user", PagePath);   // откат — механизм N7d
        LogLine(t2.AddDays(3).ToString("O"), "adopt", "user", PagePath);    // «Принять как есть» — не правка
        LogLine(t1.ToString("O"), "edit", "user", "Проекты/p1/Другая.md");

        var edits = WikiJournalReader.ReadLastHumanEdits(Root);

        edits[PagePath].Should().Be(t2, "последняя правка человека — restore, позже неё только не-правки");
        edits["Проекты/p1/Другая.md"].Should().Be(t1);
        WikiJournalReader.ReadRecentHumanReverts(Root, t1).Should().ContainKey(PagePath, "откат читает прежний читатель");
    }

    [Fact]
    public void ReadLastHumanEdits_НетЖурналаИБитыеСтроки_ПустоБезИсключения()
    {
        WikiJournalReader.ReadLastHumanEdits(Root).Should().BeEmpty("журнала ещё нет");

        var path = Path.Combine(Root, "keeper", "log.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, "{обрыв\n[1,2]\n");

        WikiJournalReader.ReadLastHumanEdits(Root).Should().BeEmpty();
    }

    // ── M1 (доработка ревью S6b-3): подтверждённая цитата доходит до пакета и сноски ──
    // Модель отвечает как просит промпт: пересказ в text, дословная выдержка в quote
    // (WikiKeeperPrompts.cs:126). В пакет идёт цитата: применение рендерит строку
    // утверждения и сноску по op.Text и требует его дословности в источнике
    // (WikiApplyService.cs:564) — пересказ был бы отказан по цитате, уже подтверждённой кодом.

    [Fact]
    public async Task M1_ПересказИДословнаяЦитата_АвтоПрименён_СноскаСЦитатойПроходаA()
    {
        var llm = new FakeLlm(claims: ClaimsJson, plan: PlanJson);
        var res = await Sut(llm).IngestAsync(User, Req());

        res.Outcome.Should().Be(WikiIngestService.OutcomeApplied, res.Reason);

        var read = WikiPackageStore.TryRead(Root, res.PackageId!);
        read.State.Should().Be(WikiPackageFileState.Valid);
        read.Package!.Operations.Should().NotBeEmpty();
        read.Package.Operations.Should().OnlyContain(o => o.Text == ClaimQuote,
            "в пакет идёт цитата, подтверждённая валидатором (мера 1), а не пересказ");

        var page = File.ReadAllText(Path.Combine(Root, "pages/" + PagePath));
        page.Should().Contain(ClaimQuote, "строка утверждения — дословная выдержка источника");
        page.Should().NotContain(ClaimText, "пересказ в страницу не пишется: там только подтверждённое дословно");
        page.Should().Contain("[^s2]: [[" + CardTitle + "]] — «" + ClaimQuote + "»",
            "сноска принятого утверждения — цитата из прохода A");
    }

    // ── M2 (доработка ревью S6b-3): нарушения состояния раздела — не вина плана ──
    // Битая ссылка на чужой странице — Violations (WikiPlanValidator.cs:60-64): ремонт из-за
    // них не вызывают (модель состояние раздела не чинит), решение по ним не принимают;
    // они называются в причине исхода отдельно.

    [Fact]
    public async Task M2_БитаяСсылкаВРазделе_БезРемонта_ДоПакета_НарушениеВПричине()
    {
        Write(User, Section, "Проекты/p1/Битая-страница.md", BrokenLinkPage);

        var llm = new FakeLlm(claims: ClaimsJson, plan: PlanJson);
        var res = await Sut(llm).IngestAsync(User, Req());

        llm.Calls.Should().HaveCount(2, "состояние раздела — не вина плана: третьего вызова модели нет");
        llm.Calls.Should().NotContain(c => c.Pass == "R", "ремонт из-за Violations не вызывают");
        PackagesOnDisk().Should().HaveCount(1, "разбор дошёл до пакета: нарушения плана не решили его");
        res.Reason.Should().Contain("в разделе есть нарушения:",
            "нарушения состояния раздела доходят до вызывающего отдельной частью причины");
        res.Reason.Should().Contain("[[Несуществующая-страница]]", "названо конкретное нарушение");
    }

    // ── гонка: две заявки одного источника — один прогон, один пакет ──

    [Fact]
    public async Task Гонка_ДвеЗаявкиОдногоИсточника_ОдинПрогон()
    {
        var llm = new FakeLlm(claims: ClaimsJson, plan: PlanJson);
        var sut = Sut(llm);
        var req = Req();

        var t1 = sut.IngestAsync(User, req);
        var t2 = sut.IngestAsync(User, req);
        var results = await Task.WhenAll(t1, t2);

        // Мутация «снять присоединение к незавершённой заявке»: вторая проходит свой цикл —
        // четыре вызова модели и (до дедупа по свежей странице) вторая постановка пакета.
        llm.Calls.Should().HaveCount(2, "одна заявка — один прогон; вторая получает её результат");
        results.Should().OnlyContain(r => r.Outcome == WikiIngestService.OutcomeApplied);
        results[0].PackageId.Should().Be(results[1].PackageId);
        PackagesOnDisk().Should().HaveCount(1, "двух пакетов на один источник нет");
    }

    // ── недоверенный ответ модели: нечитаемый JSON — отказ с причиной, не исключение ──

    [Fact]
    public async Task ОтветМоделиНеJSON_ОтказСПричиной()
    {
        var llm = new FakeLlm(claims: "модель ответила прозой вместо JSON", plan: PlanJson);
        var res = await Sut(llm).IngestAsync(User, Req());

        res.Outcome.Should().Be(WikiIngestService.OutcomeRejected);
        res.Reason.Should().Contain("проход A");
        llm.Calls.Should().HaveCount(1, "после отказа прохода A дальше не идут");
        PackagesOnDisk().Should().BeEmpty();
    }

    // ── доработка S4c (ревью d857da6c): настройки владельца доходят до боевой политики ──
    // Мера 7 (ADR-035 §12г): «выключенный класс идёт во Входящие», «общий выключатель выключен
    // — всё идёт во Входящие». Боевой ApplyContext (WikiIngestService.cs) знал только дом
    // источника: settings.json в бою не читался, и обе настройки ничего не меняли.

    [Fact]
    public async Task S4c_КлассВыключенВНастройках_ОперацияВОходящих_СПричиной()
    {
        var pageHashBefore = Sha256Of(Path.Combine(Root, "pages/" + PagePath));
        // Та же запись, что делает ручка настроек: реальный settings.json на диске.
        WikiSettingsStore.Mutate(_dir, User, s => s with
        {
            Classes = [new WikiSettingsStore.ClassState(
                WikiPackage.ClassKey("session", WikiPackage.OpAppend), true,
                "класс выключен человеком", DateTimeOffset.UtcNow, null)],
        });

        var llm = new FakeLlm(claims: ClaimsJson, plan: PlanJson);
        var res = await Sut(llm).IngestAsync(User, Req());

        res.Outcome.Should().Be(WikiIngestService.OutcomeAwaitingHuman, res.Reason);
        llm.Calls.Should().HaveCount(2, "выключенный класс — не вина плана: ремонта модели нет");

        var read = WikiPackageStore.TryRead(Root, res.PackageId!);
        read.State.Should().Be(WikiPackageFileState.Valid);
        read.Package!.Status.Should().Be(WikiPackage.StatusProposed, "пакет со спорными операциями не принимается сам");
        read.Package!.Operations.Should().NotBeEmpty();
        read.Package!.Operations.Should().OnlyContain(o => o.Decision == WikiPackage.DecisionInbox);
        read.Package!.Operations.Should().Contain(o => o.Class == "session×append", "класс операции — пары источника × операции");
        foreach (var op in read.Package!.Operations)
            op.Reason.Should().Contain("выключен", "Входящие приходят с объяснением");

        Sha256Of(Path.Combine(Root, "pages/" + PagePath)).Should().Be(pageHashBefore,
            "на диске страниц не тронуто: выключенный класс применяет только человек");
    }

    [Fact]
    public async Task S4c_ОбщийВыключательВыключен_ВесьПакетВОходящих()
    {
        var pageHashBefore = Sha256Of(Path.Combine(Root, "pages/" + PagePath));
        WikiSettingsStore.Mutate(_dir, User, s => s with { AutoApplyEnabled = false });

        var llm = new FakeLlm(claims: ClaimsJson, plan: PlanJson);
        var res = await Sut(llm).IngestAsync(User, Req());

        res.Outcome.Should().Be(WikiIngestService.OutcomeAwaitingHuman, res.Reason);
        var read = WikiPackageStore.TryRead(Root, res.PackageId!);
        read.Package!.Status.Should().Be(WikiPackage.StatusProposed);
        read.Package!.Operations.Should().NotBeEmpty();
        read.Package!.Operations.Should().OnlyContain(o => o.Decision == WikiPackage.DecisionInbox,
            "общий выключатель выключен — каждая правка идёт через «Входящие»");
        foreach (var op in read.Package!.Operations)
            op.Reason.Should().Contain("Применять без ревью", "причина — общий выключатель");

        Sha256Of(Path.Combine(Root, "pages/" + PagePath)).Should().Be(pageHashBefore);
    }

    [Fact]
    public async Task S4c_БитыйSettingsJson_Умолчания_АвтоРаботает()
    {
        // Файл настроек испорчен руками: стор даёт умолчания («Применять без ревью» включён,
        // выключенных классов нет) и не бросает наружу — разбор не роняется на битом файле.
        var settingsPath = Path.Combine(Root, "keeper", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, "{ это не json");

        var llm = new FakeLlm(claims: ClaimsJson, plan: PlanJson);
        var res = await Sut(llm).IngestAsync(User, Req());

        res.Outcome.Should().Be(WikiIngestService.OutcomeApplied, res.Reason);
        var page = File.ReadAllText(Path.Combine(Root, "pages/" + PagePath));
        page.Should().Contain(ClaimQuote, "при умолчаниях неспорное применяется само, как раньше");
    }

    // ── W3-1: авто-путь без исключения, «applying» не висит ────────────────────────────
    // Сбой вне контракта ApplyResult на авто-пути (исключение из WithSectionsLockedAsync
    // или Root — единственные места вне собственных обхватов сервиса) — результат-отказ с
    // причиной и кодом, тот же, что у пути решения человека (AfterDecision/CrashedApply).

    [Fact]
    public async Task АвтоПуть_ИсключениеИзApply_ПакетНеВApplying_БезИсключенияНаружу()
    {
        // Сбой здесь — формат владельца: Root бросает ArgumentException до заявки, тот же
        // случай, что ИсключениеИзПрименения в контролере. Исход — отказ с причиной и кодом,
        // а не исключение вызывающему; пакет не двинут в «applying» (сбой до заявки его не
        // открывает). Мутация «снять try/catch» в ApplyAutoAsync: тест падает с исключением,
        // а не с исходом.
        var id = PackageOnDisk(WikiPackage.StatusAccepted);
        var card = WikiPackageStore.TryRead(Root, id).Package!;

        var res = await Sut(new FakeLlm(claims: ClaimsJson, plan: PlanJson)).ApplyAutoAsync(
            "..", Root, card, new WikiApplyService.ApplyContext(CardTitle, SourceText));

        res.Outcome.Should().Be(WikiIngestService.OutcomeRejected);
        res.Reason.Should().Contain("внутренний сбой применения")
            .And.Contain(WikiApplyService.CodeInternal, "отказ называет код");
        var after = WikiPackageStore.TryRead(Root, id);
        after.State.Should().Be(WikiPackageFileState.Valid);
        after.Package!.Status.Should().NotBe(WikiPackage.StatusApplying,
            "сбой до заявки не открывает «applying»");
    }

    [Fact]
    public void СбойПослеЗаявкиНаАвтоПути_ПакетЗакрытApplyFailed_АНеЗависаетВApplying()
    {
        // Тот же приём, что СбойПослеЗаявки в контролере: файловый сбой ПОСЛЕ заявки
        // детерминированно в живой поток не поставить, поэтому закрытие «applying»
        // проверяется на диске с нужным статусом (W3-1). Мутация «не закрывать переходом»
        // (убрать Transition в CrashedApply): статус на диске краснеет — пакет остался бы
        // в «applying» до сторожа старта.
        var id = PackageOnDisk(WikiPackage.StatusApplying);

        var reason = WikiIngestService.CrashedApply(Root, id, new IOException("диск отказал"));

        reason.Should().Contain("внутренний сбой применения").And.Contain(WikiApplyService.CodeInternal);
        var after = WikiPackageStore.TryRead(Root, id);
        after.State.Should().Be(WikiPackageFileState.Valid);
        after.Package!.Status.Should().Be(WikiPackage.StatusApplyFailed);
        after.Package.StatusReason.Should().Contain("внутренний сбой применения",
            "причина сбоя читается в карточке пакета");
    }

    // Пакет в своём корне, доведён до статуса разрешёнными переходами стора: форма отказа
    // авто-пути проверяется на диске с настоящим статусом пакета (W3-1).
    private string PackageOnDisk(string status)
    {
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = Section,
            Sources = [new WikiPackage.SourceRef("session", "session:s1#m1-9")],
            Operations = [new WikiPackage.Operation
            {
                Op = WikiPackage.OpAppend,
                Page = PagePath,
                Text = ClaimQuote,
                Target = "прежнее утверждение",
                Class = WikiPackage.ClassKey("session", WikiPackage.OpAppend),
                Decision = WikiPackage.DecisionAuto,
                BaseHashes = new Dictionary<string, string>
                    { [PagePath] = Sha256Of(Path.Combine(Root, "pages/" + PagePath)) },
            }],
            Status = WikiPackage.StatusChecking,
        };
        WikiPackageStore.Create(Root, pkg).Ok.Should().BeTrue();
        var steps = status == WikiPackage.StatusAccepted
            ? new[] { WikiPackage.StatusProposed, WikiPackage.StatusAccepted }
            : new[] { WikiPackage.StatusProposed, WikiPackage.StatusApplying };
        foreach (var step in steps)
            WikiPackageStore.Transition(Root, pkg.Id, step).Ok.Should().BeTrue($"{status}: {step}");
        return pkg.Id;
    }

    // ── подменённый ICheapTextRunner: классифицирует вызов по промпту, помечает всё ──

    private sealed class FakeLlm : ICheapTextRunner
    {
        public List<(string ActionKey, string Prompt, string Pass)> Calls { get; } = [];

        private readonly Dictionary<string, Queue<string>> _answers = new();

        public FakeLlm(string claims, string plan, string? repair = null)
        {
            _answers["A"] = new Queue<string>([claims]);
            _answers["B"] = new Queue<string>([plan]);
            _answers["R"] = new Queue<string>([repair ?? plan]);
        }

        public string PromptOf(string pass) =>
            Calls.First(c => c.Pass == pass).Prompt;

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(Answer(actionKey, prompt));

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult<string?>(Answer(actionKey, prompt));

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(new OneShotResult(
                Answer(actionKey, prompt), new OneShotUsage(10, 0, 0, 5, 0.0, "fake"), 1));

        private string Answer(string actionKey, string prompt)
        {
            // Проход определяется по директиве своего промпта (WikiKeeperPrompts),
            // ремонт — по префиксу ремонта: порядок вызовов в тесте не угадывается.
            var pass = prompt.Contains("YOUR PREVIOUS PLAN WAS REJECTED") ? "R"
                : prompt.Contains("extract durable") ? "A"
                : prompt.Contains("turn the claims") ? "B"
                : "?";
            Calls.Add((actionKey, prompt, pass));
            actionKey.Should().Be(LocalActionCatalog.WikiIngest, "место разбора — wiki-ingest");
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
}
