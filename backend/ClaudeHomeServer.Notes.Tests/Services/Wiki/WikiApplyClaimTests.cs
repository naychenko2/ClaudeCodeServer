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
// Две параллельные заявки одного пакета дают ровно один переход (окно гонки — чтение и запись
// пакета из 200 операций, паттерн теста S4a: два старта от одного барьера, без Task.Delay).
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
    public async Task ParallelApply_SamePage_SecondStale()
    {
        await WriteCardAndPage();
        var hashes = new Dictionary<string, string> { [PagePath] = WikiManifest.Sha256Hex(PageBase) };
        var p1 = CreateAccepted(Claim(WikiPackage.OpAppend, PagePath, Claim1, [Quote], baseHashes: hashes));
        var p2 = CreateAccepted(Claim(WikiPackage.OpAppend, PagePath, "Сборку переносят туда", [Quote], baseHashes: hashes));
        var gate = new TaskCompletionSource();

        var t1 = Task.Run(async () => { await gate.Task; return await _sut.ApplyAsync(User, p1, Ctx); });
        var t2 = Task.Run(async () => { await gate.Task; return await _sut.ApplyAsync(User, p2, Ctx); });
        gate.SetResult();
        var rs = await Task.WhenAll(t1, t2);

        rs.Count(x => x.Applied).Should().Be(1);
        // Второй отказан: база страницы изменена первым (sha256 диска ≠ снимок проверки → drift/stale).
        rs.Single(x => !x.Applied).Code.Should().BeOneOf(WikiApplyService.CodeStale, WikiApplyService.CodeDrift);
        File.ReadAllText(PageFull(PagePath)).Should().Contain("[^s2]").And.NotContain("[^s3]");
    }
}
