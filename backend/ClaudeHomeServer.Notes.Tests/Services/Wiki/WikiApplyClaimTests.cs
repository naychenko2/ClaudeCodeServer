using System.Security.Cryptography;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// S4b-1 (ADR-035, решение 4 шаги 1–3, решение 10): проверки применения пакета до записи.
// Эти тесты держат фазу проверок — PrepareAsync: на диске ничего не пишется, файлы раздела
// остаются байт в байт, меняется только статус пакета и только через WikiPackageStore.Transition.
// Случай «второй на ту же страницу» построен internal-разделителем Prepare/Write (тем же,
// которым делён сервис, WikiApplyService.cs:9-12): обе проверки на одной базе, затем записи —
// определённо, без рандомной гонки тредов.
// Запись (шаги 4–6: прообразы, файлы, журнал, откат) и полный ApplyAsync — WikiApplyTests.
public class WikiApplyClaimTests : IDisposable
{
    private const string User = "u1";
    private const string Section = "common";
    private const string CardTitle = "Карточка чата";
    private const string CardPath = "Общее/Карточка-чата.md";
    private const string PagePath = "Общее/Сущность.md";

    // Текст источника: по нему выводится цитата (мера 1). Утверждения тестов — его части.
    private const string SourceText =
        "Решение: перенести сборку на Linux. Прежний план был остаться на Windows. Контенер собран на второй машине.";

    private static readonly WikiApplyService.ApplyContext Ctx = new(CardTitle, SourceText);

    private static readonly string CardFile = $$"""
        ---
        type: source
        origin: session
        ref: session:s1#m1-9
        hash: 0123456789012345678901234567890123456789012345678901234567890123
        captured: 2026-10-27
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{CardTitle}}
        """;

    // Страница до применения: «Сводка» несёт замещаемое утверждение (цель supersede тестов).
    private static readonly string PageBase = $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Сущность

        ## Сводка
        Прежний план был остаться на Windows [^s1]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[{{CardTitle}}]] — «Прежний план был остаться на Windows»
        """;

    // Страница после чужого применения: цель supersede заменена — её больше нет в «Сводке».
    private static readonly string PageReplaced = $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Сущность

        ## Сводка
        Новый план: сборку не переносим [^s1]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[{{CardTitle}}]] — «Прежний план был остаться на Windows»
        """;

    // Страница после чужого append: исходная строка цела, добавлена ещё одна.
    private static readonly string PageAfterForeign = $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Сущность

        ## Сводка
        Прежний план был остаться на Windows [^s1]
        После проверки: прежний план был оставлен [^s1]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[{{CardTitle}}]] — «Прежний план был остаться на Windows»
        """;

    private readonly string _dir;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly WikiApplyService _sut;

    public WikiApplyClaimTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_claim_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(config, users, new AppSettingsService(config));
        // Флаг llm-wiki включён: состояние проверки читается моделью (BuildFresh), а источник
        // wiki в модель попадает только с включённым флагом (тот же приём, что в WikiKeeperTests).
        _notes = new NotesService(projects, config, NullLogger<NotesService>.Instance, null, flags: new WikiFlagOn());
        _keeper = new WikiKeeper(_notes, config);
        _sut = new WikiApplyService(_notes, _keeper, new NoSpheres(), config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // Флаг llm-wiki включён для всех ключей, кроме прочего (тот же фиксатор, что в WikiKeeperTests).
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

    private string Root => WikiPaths.Root(_dir, User);
    private string PageFull(string pageKey) => Path.Combine(WikiPaths.Pages(_dir, User), pageKey);

    private async Task WriteViaKeeper(string pageKey, string content)
    {
        var r = await _keeper.WritePageAsync(User, Section, pageKey, content);
        r.Status.Should().Be(WikiOpStatus.Applied, r.Reason);
    }

    private static WikiPackage.Operation Claim(
        string op, string page, string claim, string[] quotes, string decision = WikiPackage.DecisionAuto,
        string? kind = null, string? asOf = null, string? text = null, string? human = null,
        IReadOnlyDictionary<string, string>? baseHashes = null) => new()
    {
        Op = op,
        Page = page,
        Text = text ?? claim,
        Claim = claim,
        Quotes = quotes,
        Kind = kind,
        AsOf = asOf,
        Class = WikiPackage.ClassKey("session", op),
        Decision = decision,
        Reason = decision == WikiPackage.DecisionInbox ? "спорный класс" : null,
        BaseHashes = baseHashes ?? new Dictionary<string, string>(),
        HumanDecision = human,
    };

    private WikiPackage CreateAccepted(params WikiPackage.Operation[] ops) => CreateAccepted("chat-1", ops);

    private WikiPackage CreateAccepted(string sourceRef, params WikiPackage.Operation[] ops)
    {
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = Section,
            Sources = [new WikiPackage.SourceRef("session", sourceRef)],
            Operations = ops,
            Status = WikiPackage.StatusChecking,
        };
        WikiPackageStore.Create(Root, pkg).Ok.Should().BeTrue();
        WikiPackageStore.Transition(Root, pkg.Id, WikiPackage.StatusProposed).Ok.Should().BeTrue();
        WikiPackageStore.Transition(Root, pkg.Id, WikiPackage.StatusAccepted).Ok.Should().BeTrue();
        return WikiPackageStore.TryRead(Root, pkg.Id).Package!;
    }

    private async Task WriteCardAndPage()
    {
        await WriteViaKeeper(CardPath, CardFile);
        await WriteViaKeeper(PagePath, PageBase);
    }

    private const string Quote = "перенести сборку на Linux";
    private const string Claim1 = "Сборку переносят на Linux";

    [Fact]
    public async Task Синтез_ДвеЦитатыДвеСноски_ВидИAsOfНовойСтраницы()
    {
        await WriteCardAndPage();
        const string NewPage = "Общее/Состояние-сборки.md";
        var pkg = CreateAccepted(Claim(WikiPackage.OpCreate, NewPage, Claim1,
            [Quote, "остаться на Windows"], kind: WikiFrontmatter.TypeState, asOf: "2026-11-09"));

        var r = await _sut.PrepareAsync(User, pkg, Ctx);

        r.Ready.Should().BeTrue(r.Reason);
        var content = r.Rendered.Single(d => d.Path == NewPage).Content;
        content.Should().Contain("type: state").And.Contain("as_of: 2026-11-09");
        content.Should().Contain(Claim1 + " [^s1][^s2]");
        content.Should().Contain("«" + Quote + "»").And.Contain("«остаться на Windows»");
    }

    [Fact]
    public async Task Синтез_СозданиеKindRule_ВидRuleИДвеСноски()
    {
        // Мутация «вид не из op.Kind» (в рендере создания писать type: topic/state вместо Kind):
        // Contain("type: rule") краснеет.
        await WriteCardAndPage();
        const string NewPage = "Общее/Правило-сборки.md";
        var pkg = CreateAccepted(Claim(WikiPackage.OpCreate, NewPage, Claim1,
            [Quote, "остаться на Windows"], kind: WikiFrontmatter.TypeRule));

        var r = await _sut.PrepareAsync(User, pkg, Ctx);

        r.Ready.Should().BeTrue(r.Reason);
        var content = r.Rendered.Single(d => d.Path == NewPage).Content;
        content.Should().Contain("type: rule").And.NotContain("type: state").And.NotContain("as_of:");
        content.Should().Contain(Claim1 + " [^s1][^s2]");
        content.Should().Contain("«" + Quote + "»").And.Contain("«остаться на Windows»");
    }

    [Fact]
    public async Task Синтез_ТекстНеРавенClaim_Отказ()
    {
        await WriteCardAndPage();
        var pkg = CreateAccepted(Claim(WikiPackage.OpAppend, PagePath, Claim1, [Quote], text: "Другой текст"));

        var r = await _sut.PrepareAsync(User, pkg, Ctx);

        r.Ready.Should().BeFalse();
        r.Code.Should().Be(WikiApplyService.CodeSource);
        r.Reason.Should().Contain("не совпадает");
    }

    [Fact]
    public async Task Синтез_ЦитатаНеВИсточнике_Отказ()
    {
        await WriteCardAndPage();
        var pkg = CreateAccepted(Claim(WikiPackage.OpAppend, PagePath, Claim1, [Quote, "этого в источнике нет"]));

        var r = await _sut.PrepareAsync(User, pkg, Ctx);

        r.Ready.Should().BeFalse();
        r.Code.Should().Be(WikiApplyService.CodeSource);
        r.Reason.Should().Contain("цитата не выводится");
    }

    // Дефект 3b6cef0e (QA-3): цитата с переносом строки из многострочного фрагмента
    // источника (абзац с пустой строкой, markdown-таблица) писалась в сноску как есть —
    // FootnoteDefRx её не читал, и гейт отвергал весь пакет. Рендер складывает цитату
    // в одну строку тем же помощником, что у хроники (WikiPage.OneLine); дословность
    // проверена по исходному тексту, а сверка гейта нормализует whitespace — она это переживает.
    private const string MultiSource =
        "Прежний план был остаться на Windows.\n\n" +
        "Сначала строка абзаца:\n\nпродолжение абзаца на второй строке.\n\n" +
        "| узел | сборка |\n|---|---|\n| прод | Linux |";

    private static readonly WikiApplyService.ApplyContext MultiCtx = new(CardTitle, MultiSource);

    [Fact]
    public async Task Синтез_МногострочныеЦитаты_СноскиОднойСтрокой()
    {
        await WriteCardAndPage();
        var pkg = CreateAccepted(Claim(WikiPackage.OpAppend, PagePath, "Сборку переносят на другой машине",
            ["Сначала строка абзаца:\n\nпродолжение абзаца на второй строке.",
             "| узел | сборка |\n|---|---|\n| прод | Linux |"]));

        var r = await _sut.PrepareAsync(User, pkg, MultiCtx);

        r.Ready.Should().BeTrue(r.Reason);
        var content = r.Rendered.Single(d => d.Path == PagePath).Content;
        content.Should().Contain("Сборку переносят на другой машине [^s2][^s3]");
        // Сноска читается форматом страницы: Parse — тот же FootnoteDefRx, который
        // раньше её не определял. Мутация «убрать складывание в рендере» краснит раньше
        // этой строки: сноска расползается, и уже гейт отвергает пакет («ссылка на
        // несуществующую сноску») — Ready ложный.
        var parsed = WikiPage.Parse(content);
        parsed.Errors.Should().BeEmpty(string.Join("; ", parsed.Errors));
        parsed.Model!.Footnotes.Should().HaveCount(3);
        foreach (var f in parsed.Model.Footnotes)
            f.Quote.Should().NotContain("\n").And.NotContain("\r");
        parsed.Model.Footnotes[1].Quote.Should().Contain("Сначала строка абзаца:").And.Contain("продолжение абзаца");
        parsed.Model.Footnotes[2].Quote.Should().Contain("| прод | Linux |");
    }

    [Fact]
    public async Task Синтез_МногострочнаяЦитатаНеДословная_Отказ()
    {
        // Складывание не ослабило меру 1: недословная цитата с переносом строки
        // отклоняется по-прежнему — проверка идёт по исходному тексту операции.
        await WriteCardAndPage();
        var pkg = CreateAccepted(Claim(WikiPackage.OpAppend, PagePath, "Сборку переносят на другой машине",
            ["этого в источнике нет:\nи продолжения тоже"]));

        var r = await _sut.PrepareAsync(User, pkg, Ctx);

        r.Ready.Should().BeFalse();
        r.Code.Should().Be(WikiApplyService.CodeSource);
        r.Reason.Should().Contain("цитата не выводится");
    }

    [Fact]
    public async Task Синтез_АвтоСНеподтверждённымЧислом_НеПрименяется()
    {
        await WriteCardAndPage();
        var pkg = CreateAccepted(Claim(WikiPackage.OpAppend, PagePath, "Сборку переносят на Linux за 17 дней", [Quote]));

        var r = await _sut.PrepareAsync(User, pkg, Ctx);

        r.Ready.Should().BeFalse();
        r.Reason.Should().Contain("число");
    }

    [Fact]
    public async Task Синтез_ИнбоксПринятыйЧеловеком_СЧислом_Применяется()
    {
        await WriteCardAndPage();
        var pkg = CreateAccepted(Claim(WikiPackage.OpAppend, PagePath, "Сборку переносят на Linux за 17 дней", [Quote],
            decision: WikiPackage.DecisionInbox, human: WikiPackage.HumanAccepted));

        var r = await _sut.PrepareAsync(User, pkg, Ctx);

        r.Ready.Should().BeTrue(r.Reason);
    }

    [Fact]
    public async Task ПутьРешенияЧеловека_БезКонтекста_РендерИзClaimИQuotes()
    {
        await WriteCardAndPage();
        // Карточка источника пакета в разделе: ищется по Origin/Ref — здесь нужна карточка с ref chat-1.
        var pkg = CreateAccepted("session:s1#m1-9", Claim(WikiPackage.OpAppend, PagePath, Claim1, [Quote]));

        var r = await _sut.PrepareAsync(User, pkg, null);

        r.Ready.Should().BeTrue(r.Reason);
        r.Rendered.Single(d => d.Path == PagePath).Content.Should().Contain(Claim1 + " [^s2]");
    }

    [Fact]
    public async Task InterleavedPrepare_SamePage_SecondStale()
    {
        // Инвариант: база сверяется со снимком проверки; молчаливой перезаписи чужого применения нет
        // (сверка шага 5, WikiApplyService.cs:305-314). Переплетение строит internal-разделитель
        // Prepare/Write — тот же, которым делён сервис (WikiApplyService.cs:9-12): обе проверки
        // на одной базе, затем записи по очереди. Определённо, без рандомной гонки тредов:
        // под нагрузой параллельного батча тестов планирование сериализовало вызовы, и прежняя
        // версия (барьер + два ApplyAsync) краснела — второй пакет законно применялся.
        await WriteCardAndPage();
        var hashes = new Dictionary<string, string> { [PagePath] = WikiManifest.Sha256Hex(PageBase) };
        var p1 = CreateAccepted(Claim(WikiPackage.OpAppend, PagePath, Claim1, [Quote], baseHashes: hashes));
        var p2 = CreateAccepted(Claim(WikiPackage.OpAppend, PagePath, "Сборку переносят туда", [Quote], baseHashes: hashes));

        // Обе проверки на одной базе: между ними диск не меняется, снимки обоих — PageBase.
        var prep1 = await _sut.PrepareAsync(User, p1, Ctx);
        var prep2 = await _sut.PrepareAsync(User, p2, Ctx);
        prep1.Ready.Should().BeTrue(prep1.Reason);
        prep2.Ready.Should().BeTrue(prep2.Reason);

        var r1 = await _sut.WriteAsync(User, p1.Id, prep1, Ctx);
        // Диск изменён записью первого: второй остановлен сверкой базового состояния.
        var r2 = await _sut.WriteAsync(User, p2.Id, prep2, Ctx);

        r1.Applied.Should().BeTrue(r1.Reason);
        r2.Applied.Should().BeFalse();
        r2.Code.Should().Be(WikiApplyService.CodeDrift);
        // Записанное первым цело, второй ничего не перезаписал.
        var page = File.ReadAllText(PageFull(PagePath));
        page.Should().Contain(Claim1).And.NotContain("Сборку переносят туда");
    }
}
