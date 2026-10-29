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
// На диске ничего не пишется: файлы раздела остаются байт в байт — меняется только статус
// пакета, и только через WikiPackageStore.Transition. Две параллельные заявки одного
// пакета дают ровно один переход (окно гонки — чтение и запись пакета из 200 операций,
// паттерн теста S4a: два старта от одного барьера, без Task.Delay).
public class WikiApplyCheckTests : IDisposable
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

    public WikiApplyCheckTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_apply_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(config, users, new AppSettingsService(config));
        // Флаг llm-wiki включён: состояние проверки читается моделью (BuildFresh), а источник
        // wiki в модель попадает только с включённым флагом (тот же приём, что в WikiKeeperTests).
        _notes = new NotesService(projects, config, NullLogger<NotesService>.Instance, null, new WikiFlagOn());
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

    private Task WriteCardAndPage() => WriteCardAndPage(PageBase);
    private async Task WriteCardAndPage(string pageContent)
    {
        await WriteViaKeeper(CardPath, CardFile);
        await WriteViaKeeper(PagePath, pageContent);
    }

    // Снимок всего дерева pages/ — путь → sha256 байтов: «байт в байт» проверяется по файлам,
    // а не по признаку «ничего не написали».
    private Dictionary<string, string> SnapshotPages()
    {
        var pagesDir = WikiPaths.Pages(_dir, User);
        var snap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(pagesDir, "*", SearchOption.AllDirectories))
            snap[Path.GetRelativePath(pagesDir, f).Replace('\\', '/')] =
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant();
        return snap;
    }

    private static WikiPackage.Operation Op(
        string op, string page, string? text, string? target = null,
        string decision = WikiPackage.DecisionAuto, string? human = null,
        IReadOnlyDictionary<string, string>? baseHashes = null) => new()
    {
        Op = op,
        Page = page,
        Text = text,
        Target = target,
        Class = WikiPackage.ClassKey("session", op),
        Decision = decision,
        Reason = decision == WikiPackage.DecisionInbox ? "спорный класс" : null,
        BaseHashes = baseHashes ?? new Dictionary<string, string>(),
        HumanDecision = human,
        HumanReason = human == WikiPackage.HumanRejected ? "не нужно" : null,
    };

    // Пакет доводится до «accepted» штатными переходами хранилища (checking → proposed →
    // accepted): карточка для ApplyAsync читается с диска, как её прочтёт REST.
    private WikiPackage CreateAccepted(params WikiPackage.Operation[] ops)
    {
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = Section,
            Sources = [new WikiPackage.SourceRef("session", "chat-1")],
            Operations = ops,
            Status = WikiPackage.StatusChecking,
        };
        var created = WikiPackageStore.Create(Root, pkg);
        created.Ok.Should().BeTrue(created.Reason);
        WikiPackageStore.Transition(Root, pkg.Id, WikiPackage.StatusProposed).Ok.Should().BeTrue();
        var accepted = WikiPackageStore.Transition(Root, pkg.Id, WikiPackage.StatusAccepted);
        accepted.Ok.Should().BeTrue(accepted.Reason);
        var read = WikiPackageStore.TryRead(Root, pkg.Id);
        read.State.Should().Be(WikiPackageFileState.Valid);
        return read.Package!;
    }

    private string ReadStatus(string id)
    {
        var read = WikiPackageStore.TryRead(Root, id);
        read.State.Should().Be(WikiPackageFileState.Valid);
        return read.Package!.Status;
    }

    // ─── 1. Провал гейта на итоговом состоянии → apply_failed, диск цел (c1) ───

    [Fact]
    public async Task ПакетНарушающийГейтНаИтоге_ApplyFailedСПеречнем_ФайлыРазделаБайтВБайтЦелы()
    {
        await WriteCardAndPage();
        // link на несуществующую страницу: рендер добавляет строку, гейт её не принимает.
        var pkg = CreateAccepted(Op(WikiPackage.OpLink, PagePath, "Контенер собран на второй машине", "Нет-такой-страницы"));
        var before = SnapshotPages();

        var r = await _sut.ApplyAsync(User, pkg, Ctx);

        r.Ready.Should().BeFalse();
        r.Code.Should().Be(WikiApplyService.CodeGate);
        r.Violations.Should().Contain(v => v.Contains("Нет-такой-страницы"), "перечень нарушений — то, с чем пакет возвращается человеку");
        SnapshotPages().Should().Equal(before, "провал проверок не трогает ни одного файла раздела");
        ReadStatus(pkg.Id).Should().Be(WikiPackage.StatusApplyFailed);

        // Замкнутый статус: повторного применения нет ни при какой заявке.
        var again = await _sut.ApplyAsync(User, WikiPackageStore.TryRead(Root, pkg.Id).Package!, Ctx);
        again.Ready.Should().BeFalse();
        again.Code.Should().Be(WikiApplyService.CodeStatus);
    }

    // ─── 2. Дрейф, stale и чужой append (c2) ───────────────────────────────────

    [Fact]
    public async Task ДрейфРазделаПравкаМимоХранителя_ApplyFailed_ЧужаяПравкаОстаётсяНаДиске()
    {
        await WriteCardAndPage();
        // Правка человека мимо Хранителя: манифест и диск разошлись — применять нельзя.
        File.WriteAllText(PageFull(PagePath), PageBase + "\nчужая правка человека.\n", new System.Text.UTF8Encoding(false));
        var pkg = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"));
        var before = SnapshotPages();

        var r = await _sut.ApplyAsync(User, pkg, Ctx);

        r.Code.Should().Be(WikiApplyService.CodeDrift);
        SnapshotPages().Should().Equal(before, "дрейф не откатывается и не перезаписывается молча");
        ReadStatus(pkg.Id).Should().Be(WikiPackage.StatusApplyFailed);
    }

    [Fact]
    public async Task ИсчезшееЗамещаемоеУтверждение_StaleБезАвтоRebase_ФайлыЦелы()
    {
        // Цель supersede уже замещена другим применением: базовая страница (BaseHashes)
        // больше не та, и замещать нечего — пакет требует нового разбора, а не дописывания.
        await WriteCardAndPage(PageReplaced);
        var pkg = CreateAccepted(Op(
            WikiPackage.OpSupersede, PagePath, "Контенер собран на второй машине",
            "Прежний план был остаться на Windows",
            baseHashes: new Dictionary<string, string> { [PagePath] = WikiManifest.Sha256Hex(PageBase) }));
        var before = SnapshotPages();

        var r = await _sut.ApplyAsync(User, pkg, Ctx);

        r.Ready.Should().BeFalse();
        r.Code.Should().Be(WikiApplyService.CodeStale);
        r.Reason.Should().Contain("больше не в странице");
        SnapshotPages().Should().Equal(before);
        ReadStatus(pkg.Id).Should().Be(WikiPackage.StatusStale);
    }

    [Fact]
    public async Task ЧужойAppendНаТойЖеСтранице_ПроверкаПроходит_ГотовоКЗаписи()
    {
        await WriteCardAndPage(PageAfterForeign);
        // База изменилась (baseHash больше не текущий), но намерение цело: страница есть,
        // append ни на что не опирался — проверка проходит, авто-rebase не нужен.
        var pkg = CreateAccepted(Op(
            WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине",
            baseHashes: new Dictionary<string, string> { [PagePath] = WikiManifest.Sha256Hex(PageBase) }));
        var before = SnapshotPages();

        var r = await _sut.ApplyAsync(User, pkg, Ctx);

        r.Ready.Should().BeTrue(r.Reason);
        var rendered = r.Rendered.Single(d => d.Path == PagePath).Content;
        rendered.Should().Contain("Контенер собран на второй машине");
        rendered.Should().Contain("После проверки: прежний план был оставлен", "рендер надцает чужой append, а не затирает его");
        SnapshotPages().Should().Equal(before, "до записи (S4b-2) на диске ничего не меняется");
        ReadStatus(pkg.Id).Should().Be(WikiPackage.StatusApplying, "заявка держит повторное применение до конца цикла");
    }

    // ─── 3. «inbox» без решения человека не применяется (c3) ────────────────────

    [Fact]
    public async Task InboxБезРешенияЧеловекаНеПопадаетВРендер_ПринятоеРешениемБерётся()
    {
        // Мутация «брать все операции» (убрать фильтр по HumanDecision): вторая строка
        // появляется в rendered — NotContain краснеет стабильно, это не гонка.
        await WriteCardAndPage();
        var pkg = CreateAccepted(
            Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"),
            Op(WikiPackage.OpAppend, PagePath, "Решение: перенести сборку на Linux", decision: WikiPackage.DecisionInbox));

        var r = await _sut.ApplyAsync(User, pkg, Ctx);

        r.Ready.Should().BeTrue(r.Reason);
        var rendered = r.Rendered.Single(d => d.Path == PagePath).Content;
        rendered.Should().Contain("Контенер собран на второй машине");
        rendered.Should().NotContain("Решение: перенести сборку на Linux", "inbox без решения человека из рендера отброшен");

        // Решение человека «принять» читается только из HumanDecision: пакет с ним рендерит обе строки.
        var decided = CreateAccepted(
            Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"),
            Op(WikiPackage.OpAppend, PagePath, "Решение: перенести сборку на Linux",
                decision: WikiPackage.DecisionInbox, human: WikiPackage.HumanAccepted));
        var r2 = await _sut.ApplyAsync(User, decided, Ctx);
        r2.Ready.Should().BeTrue(r2.Reason);
        r2.Rendered.Single(d => d.Path == PagePath).Content
            .Should().Contain("Решение: перенести сборку на Linux");
    }

    // ─── 4. Две параллельные заявки одного пакета — один переход (уроки S4a MAJOR 1) ───

    [Fact]
    public async Task ДваПараллельныхПримененияОдногоПакета_РовноОдноДоходитДоПроверок_ВтороеОтказаноПоСтатусу()
    {
        // Окно гонки — чтение с диска и заявка: чтение и запись пакета из 200 операций
        // занимают миллисекунды, два старта от барьера пересекаются не случайно (паттерн S4a).
        // Проигравший отказан по дисковому состоянию: «applying» уже занял первый — это
        // CodeStatus (прочитал после его записи) или CodeClaim (Transition под блоком).
        // Мутация «без заявки» (убрать Transition, проверять статус карточки): оба вызова
        // доходят до проверок — Count(Ready)==2, тест красный стабильно.
        await WriteCardAndPage();
        var facts = Enumerable.Range(0, 200).Select(i => $"Факт {i:D3}: сборка контейнера прошла").ToArray();
        var pkg = CreateAccepted([.. facts.Select(t => Op(WikiPackage.OpAppend, PagePath, t))]);
        // Текст источника этого пакета: его карточка — та же, что у старой сноски страницы,
        // значит и её цитата обязана быть в тексте (GivenOnly проверяет карточки словаря).
        var ctx = new WikiApplyService.ApplyContext(CardTitle,
            "Прежний план был остаться на Windows. " + string.Join(". ", facts) + ".");

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () => { await barrier.Task; return await _sut.ApplyAsync(User, pkg, ctx); });
        var second = Task.Run(async () => { await barrier.Task; return await _sut.ApplyAsync(User, pkg, ctx); });
        barrier.SetResult();

        var r1 = await first.WaitAsync(TimeSpan.FromSeconds(30));
        var r2 = await second.WaitAsync(TimeSpan.FromSeconds(30));
        new[] { r1.Ready, r2.Ready }.Count(x => x).Should().Be(1, "заявка под блоком id: ровно один вызов доходит до проверок");
        new[] { WikiApplyService.CodeClaim, WikiApplyService.CodeStatus }
            .Should().Contain((r1.Ready ? r2 : r1).Code, "проигравший гонку отказан по дисковому состоянию");
        ReadStatus(pkg.Id).Should().Be(WikiPackage.StatusApplying, "пакет занят одним применением, а не применён дважды");
    }

    // ─── 5. Рендер применения — общий с рендером плана (S6b-2), второй копии нет ───

    [Fact]
    public async Task РендерПримененияТотЖеЧтоРендерПлана_Паритет()
    {
        await WriteCardAndPage();
        var text = "Контенер собран на второй машине";
        var op = Op(WikiPackage.OpAppend, PagePath, text);
        var pkg = CreateAccepted(op);

        var r = await _sut.ApplyAsync(User, pkg, Ctx);
        r.Ready.Should().BeTrue(r.Reason);

        // Та же операция на том же состоянии — прямым вызовом общего рендера.
        List<WikiGate.Draft> state =
        [
            new(CardPath, File.ReadAllText(PageFull(CardPath))),
            new(PagePath, File.ReadAllText(PageFull(PagePath))),
        ];
        var direct = WikiPlanValidator.RenderApply(state, op, text, CardTitle);
        direct.Should().NotBeNull();
        r.Rendered.Single(d => d.Path == PagePath).Content.Should().Be(direct!.Value.Content);
    }

    // ─── 6. Неожиданный вход — отказ с причиной, не исключение; статус не тронут ───

    [Fact]
    public async Task NullПакетИОтказБезКонтекста_РезультатОтказ_СтатусНеТронут()
    {
        var nullPkg = await _sut.ApplyAsync(User, null, Ctx);
        nullPkg.Ready.Should().BeFalse();
        nullPkg.Code.Should().Be(WikiApplyService.CodeShape);

        var pkg = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"));
        var noCtx = await _sut.ApplyAsync(User, pkg);
        noCtx.Ready.Should().BeFalse();
        noCtx.Code.Should().Be(WikiApplyService.CodeSource);
        // Отказ до заявки: статус пакета не двигался ни на шаг.
        ReadStatus(pkg.Id).Should().Be(WikiPackage.StatusAccepted);

        // Пакет в «proposed» к применению не принят (переход в applying ему не положен).
        var proposed = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = Section,
            Sources = [new WikiPackage.SourceRef("session", "chat-1")],
            Operations = [Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине")],
            Status = WikiPackage.StatusChecking,
        };
        WikiPackageStore.Create(Root, proposed).Ok.Should().BeTrue();
        var r = await _sut.ApplyAsync(User, proposed, Ctx);
        r.Code.Should().Be(WikiApplyService.CodeStatus);
        ReadStatus(proposed.Id).Should().Be(WikiPackage.StatusChecking);
    }

    // ─── 7. Гейт судит по дисковой копии, не по карточке вызывающего (ревью S4b-1, MAJOR 1) ───

    [Fact]
    public async Task ДисковыйProposedСКарточкойAccepted_Отказ_СпорнаяОперацияНеВРендее_СтатусНеТронут()
    {
        // Пакет на диске — «proposed», спорная операция без решения человека. Вызывающий
        // принёс устаревшую карточку: Status=accepted, HumanDecision=accepted. Гейт обязан
        // судить по диску: «proposed» на диске → отказ до заявки, спорная операция не
        // попадает ни в рендер, ни на диск, статус пакета не двигается.
        // Мутация «брать карточку аргумента» (status = package.Status вместо disk.Status):
        // заявка проходит по таблице (на диске proposed→applying разрешён), HumanDecision
        // из карточки рендерит спорную — r.Ready.Should().BeFalse() краснеет.
        await WriteCardAndPage();
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = Section,
            Sources = [new WikiPackage.SourceRef("session", "chat-1")],
            Operations = [Op(WikiPackage.OpAppend, PagePath, "Решение: перенести сборку на Linux", decision: WikiPackage.DecisionInbox)],
            Status = WikiPackage.StatusChecking,
        };
        WikiPackageStore.Create(Root, pkg).Ok.Should().BeTrue();
        WikiPackageStore.Transition(Root, pkg.Id, WikiPackage.StatusProposed).Ok.Should().BeTrue();
        var staleCopy = pkg with
        {
            Status = WikiPackage.StatusAccepted,
            Operations = [pkg.Operations[0] with { HumanDecision = WikiPackage.HumanAccepted }],
        };
        var before = SnapshotPages();

        var r = await _sut.ApplyAsync(User, staleCopy, Ctx);

        r.Ready.Should().BeFalse();
        r.Code.Should().Be(WikiApplyService.CodeStatus);
        r.Reason.Should().Contain("не принят к применению");
        r.Rendered.Should().BeEmpty("спорная операция не попала в рендер");
        SnapshotPages().Should().Equal(before, "отказ до заявки не трогает файлов раздела");
        ReadStatus(pkg.Id).Should().Be(WikiPackage.StatusProposed, "отказ по дисковому статусу не двигает его");
    }

    // ─── 8. Сбой под блоком раздела — apply_failed с причиной, не исключение наружу (MAJOR 2) ───

    [Fact]
    public async Task НечитаемыйФайлСтраницы_ApplyFailedСПричиной_ПакетНеОстаётсяВApplying()
    {
        // Файл страницы есть, но его байты прочитать нельзя — сбой внутри проверок
        // (File.ReadAllBytes в сверке с манифестом). Без обёртки исключение ушло бы
        // наружу, и пакет навсегда остался бы в «applying».
        // Мутация «снять try/catch» в PrepareUnderLock: ApplyAsync бросает — тест красный.
        await WriteCardAndPage();
        var pkg = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"));
        var pageFull = PageFull(PagePath);
        using (var gate = new FileStream(pageFull, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var r = await _sut.ApplyAsync(User, pkg, Ctx);

            r.Ready.Should().BeFalse();
            r.Code.Should().Be(WikiApplyService.CodeInternal);
            r.Reason.Should().NotBeNullOrWhiteSpace("причина сбоя — в тексте отказа");
        }
        ReadStatus(pkg.Id).Should().Be(WikiPackage.StatusApplyFailed, "сбой проверки не оставляет пакет в «applying»");
    }
}
