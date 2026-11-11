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
    private readonly IConfiguration _config;
    private readonly WikiKeeper _keeper;
    private readonly WikiApplyService _apply;

    public WikiIngestSynthTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_ingest_synth_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        var notes = new NotesService(projects, _config, NullLogger<NotesService>.Instance, null, flags: new WikiFlagOn());
        _keeper = new WikiKeeper(notes, _config);
        _apply = new WikiApplyService(notes, _keeper, new NoSpheres(), _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
        Write(Section, CardPath, CardFile);
        Write(Section, PagePath, PageBase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
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
        pkg.Operations.Select(o => o.Claim).Should().BeEquivalentTo(Claim1, "Контейнер собран отдельно");
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

    // ── подмена модели ──

    private sealed class FakeLlm : ICheapTextRunner
    {
        public List<(string ActionKey, string Prompt, string Pass)> Calls { get; } = [];
        public bool LocalRoute { get; init; } = true;

        private readonly Dictionary<string, Queue<string>> _answers = new();

        public FakeLlm(string synth, string? repair = null, string? emptySection = null)
        {
            _answers["S"] = new Queue<string>([synth]);
            _answers["R"] = new Queue<string>([repair ?? synth]);
            _answers["E"] = new Queue<string>([emptySection ?? "нет ответа для прохода E"]);
        }

        public bool UsesLocal(string actionKey) => LocalRoute;
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
            var pass = prompt.Contains("YOUR PREVIOUS PLAN WAS REJECTED") ? "R"
                : prompt.Contains("THE TARGET SECTION HAS NO KNOWLEDGE PAGES") ? "E"
                : prompt.Contains("single pass") ? "S"
                : "?";
            lock (Calls) Calls.Add((actionKey, prompt, pass));
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
