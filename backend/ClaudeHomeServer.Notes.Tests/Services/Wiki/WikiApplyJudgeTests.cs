using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// J-J2b: решение облачного судьи применяется ТЕМ ЖЕ путём, что решение человека, и со всеми
// теми же гейтами кода. Здесь — то, что живёт в сервисе применения: правило «берётся ли
// операция» (Applies), автор строки журнала по решённому пакету (actor) и один путь
// применения для двух вызывающих (ApplyDecidedAsync). Пакеты доведены до «accepted» вердиктом
// судьи (JudgeOperations, J-J2a); дальше — обычный путь: проверки, рендер, гейт, запись.
//
// Отличия от пути человека ровно две: кто назван автором решения в журнале и что вердикт
// никто не переворачивает. Всё остальное — тот же код, поэтому и тесты берут тот же
// боевой раздел, что WikiApplyTests / WikiPackagesControllerTests.
public class WikiApplyJudgeTests : IDisposable
{
    private const string User = "u1";
    private const string Section = "common";
    private const string CardTitle = "Карточка чата";
    private const string CardPath = "Общее/Карточка-чата.md";
    private const string PagePath = "Общее/Сущность.md";
    private const string Page2Path = "Общее/Сущность-2.md";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Claim = "Решение: перенести сборку на Linux";
    private const string OldClaim = "Прежний план был остаться на Windows";
    private const string Base2Claim = "Контенер собран на второй машине";
    private const string AutoClaim = "Третий план был собрать его на третьей машине";
    private const string NewClaim = "Четвёртый план был вести поиск на новой машине";
    private const string JudgeModel = "cloud-judge-1";

    private static readonly string CardFile = $$"""
        ---
        type: source
        origin: session
        ref: session:chat-1#m1-9
        hash: {{Hash}}
        captured: 2026-10-30
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{CardTitle}}
        """;

    private static readonly string BasePage = $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Сущность

        ## Сводка
        {{OldClaim}} [^s1]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[{{CardTitle}}]] — «{{OldClaim}}»
        """;

    // Вторая страница — свой раздел-цель для авто-операции: вывод про «keeper» не должен
    // зависеть от того, что сделал предыдущий пакет с первой страницей.
    private static readonly string BasePage2 = $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Сущность-2

        ## Сводка
        {{Base2Claim}} [^s1]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[{{CardTitle}}]] — «{{Base2Claim}}»
        """;

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly WikiApplyService _sut;

    public WikiApplyJudgeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_apply_judge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        // Флаг llm-wiki и инициализированная вики: без них применение отказано до рендера
        // (CodeOff), а здесь нужен именно рендер и запись (тот же приём, что в WikiApplyTests).
        _notes = new NotesService(projects, _config, NullLogger<NotesService>.Instance, null, flags: new WikiFlagOn());
        _keeper = new WikiKeeper(_notes, _config);
        _sut = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
        WriteViaKeeper(CardPath, CardFile).GetAwaiter().GetResult();
        WriteViaKeeper(PagePath, BasePage).GetAwaiter().GetResult();
        WriteViaKeeper(Page2Path, BasePage2).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── 1. Правило «берётся ли операция»: таблица, а не два места в коде ──────────────
    //
    // Одно правило на рендер (шаг 3) и на политику журнала (шаг 6): «что применилось» в них
    // не может разойтись. Решение человека старше: рядом их не бывает (И1 — и то и другое
    // ставится только в нерешённую операцию), но порядок здесь назван, а не выведен.

    [Theory]
    [InlineData(WikiPackage.DecisionAuto, null, null, true)]                          // неспорное решает код
    [InlineData(WikiPackage.DecisionInbox, WikiPackage.HumanAccepted, null, true)]   // принял человек
    [InlineData(WikiPackage.DecisionInbox, WikiPackage.HumanRejected, null, false)]  // отклонил человек
    [InlineData(WikiPackage.DecisionInbox, null, WikiPackage.HumanAccepted, true)]   // принял судья
    [InlineData(WikiPackage.DecisionInbox, null, WikiPackage.HumanRejected, false)]  // отклонил судья
    [InlineData(WikiPackage.DecisionInbox, WikiPackage.HumanRejected, WikiPackage.HumanAccepted, false)] // вердикт человека не перекрывает
    public void ВРендерБерётсяТолькоПринятоеРешение(string decision, string? human, string? judge, bool expected) =>
        WikiApplyService.Applies(new WikiPackage.Operation
        {
            Op = WikiPackage.OpSupersede,
            Page = PagePath,
            Text = Claim,
            Target = OldClaim,
            Class = WikiPackage.ClassKey("session", WikiPackage.OpSupersede),
            Decision = decision,
            HumanDecision = human,
            JudgeDecision = judge,
            BaseHashes = new Dictionary<string, string> { [PagePath] = Hash },
        }).Should().Be(expected);

    // ─── 2. Пакет, принятый судьёй, применяется тем же путём и с тем же гейтом ─────────

    [Fact]
    public async Task ПринятыйСудьейПакетПрименяется_СтраницаЗаписана_ЖурналСАвторомJudge()
    {
        // Мутация «вердикт не решение» (Applies без ветки JudgeDecision): op не берётся в
        // рендер → страницы нет, строки журнала нет, Applied=false — краснеет весь тест.
        var id = NewJudgeAcceptedPackage(RealOp());

        var r = await _sut.ApplyDecidedAsync(User, Root(), Read(id));

        r.Outcome.Should().Be(WikiApplyService.DecidedOutcome.Ok);
        var apply = Applied(r);
        apply.Applied.Should().BeTrue(apply.Reason);
        r.Package!.Status.Should().Be(WikiPackage.StatusApplied);
        File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), PagePath)).Should()
            .Contain(Claim, "вердикт судьи применяет ту же запись, что решение человека");
        StatusOnDisk(id).Should().Be(WikiPackage.StatusApplied);

        // Автор строки применения — тот, кто решил: человека в пакете нет, есть вердикт.
        // Политика — «inbox»: операцию спрашивали, её не решил код.
        var lines = LogLines("apply");
        lines.Should().HaveCount(1);
        var line = JsonDocument.Parse(lines[0]).RootElement;
        line.GetProperty("actor").GetString().Should().Be("judge");
        line.GetProperty("policy").GetString().Should().Be(WikiPackage.DecisionInbox);
        line.GetProperty("pages").EnumerateArray().Select(p => p.GetString()).Should().Contain(PagePath);
    }

    [Fact]
    public async Task РешённыйЧеловекомПакетОстаётсяСАвторомUser_АвтоПутьСКейпером()
    {
        // Мутация «автора всегда выводить по Author пакета» (ActorOf без ветки HumanDecision):
        // первое Should краснеет — применение по решению человека читалось бы автоматикой.
        var humanId = NewDecidedPackage(RealOp());
        var r = await _sut.ApplyDecidedAsync(User, Root(), Read(humanId));
        Applied(r).Applied.Should().BeTrue("решение человека применяет пакет тем же путём");
        JsonDocument.Parse(LogLines("apply").Last()).RootElement
            .GetProperty("actor").GetString().Should().Be("user", "решение вынес человек");

        // Авто-пакет (ни одной спорной, вердикт проверок — accepted): решений в нём нет, его
        // применил код — автор строки остаётся «keeper». Другая страница: этот вывод не должен
        // зависеть от того, что сделал предыдущий пакет с первой.
        var autoId = NewAutoPackage();
        var ra = await _sut.ApplyDecidedAsync(User, Root(), Read(autoId));
        Applied(ra).Applied.Should().BeTrue("авто-пакет применяется тем же путём");
        var autoLine = JsonDocument.Parse(LogLines("apply").Last()).RootElement;
        autoLine.GetProperty("actor").GetString().Should().Be("keeper", "решений нет — применил код");
        autoLine.GetProperty("policy").GetString().Should().Be(WikiPackage.DecisionAuto);
    }

    // ─── 3. Гонка: судья ‖ «принять всё» человека — пакет применяется ровно один раз ────

    [Fact]
    public async Task ДваПараллельныхПримененияОдногоПакета_ОдинApply_ВторойОтказБезИсключения()
    {
        // Пакет доведён до «accepted» вердиктом судьи; рядом тот же путь зовёт второй
        // вызывающий (в бою — «принять всё» человека). Ровно одно применение, проигравший —
        // результат-отказ, а не исключение и не второй набор файлов.
        var id = NewJudgeAcceptedPackage(RealOp());
        var decided = Read(id);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = Task.Run(async () => { await gate.Task; return await _sut.ApplyDecidedAsync(User, Root(), decided); });
        var b = Task.Run(async () => { await gate.Task; return await _sut.ApplyDecidedAsync(User, Root(), decided); });
        gate.SetResult();
        var rs = await Task.WhenAll(a, b);

        rs.Should().HaveCount(2, "сбой применения — результат, а не исключение вызывающего");
        rs.Count(x => x.Apply?.Applied == true).Should().Be(1, "ровно одно применение одного пакета");
        rs.Single(x => x.Apply?.Applied != true).Apply!.Value.Code.Should()
            .BeOneOf(WikiApplyService.CodeStatus, WikiApplyService.CodeClaim,
                "проигравший отказан по статусу или по несостоявшейся заявке — оба законные отказы");
        LogLines("apply").Should().HaveCount(1, "одна строка применения на двоих вызовов");
        Directory.GetFiles(Path.Combine(WikiPaths.Keeper(_dir, User), "preimages", id), "*", SearchOption.AllDirectories)
            .Should().HaveCount(1, "прообразы пишет тот, кто заявил пакет");
        StatusOnDisk(id).Should().Be(WikiPackage.StatusApplied);
    }

    // ─── 4. D0: отказ по спорным не выбрасывает авто-операции ────────────────

    [Fact]
    public async Task ОтклонённоеСпорноеПриАвто_АвтоПрименяется_ПакетApplied()
    {
        // Мутация «вернуть старое условие rejected» (StatusAfterDecisions без проверки
        // авто-операций): пакет стал бы rejected, ApplyDecidedAsync не позвал бы применение
        // (Apply=null) — краснеет Applied(r).
        var id = NewPackage([RealOp(), AutoOp()]);
        var decided = WikiPackageStore.DecideOperation(Root(), id, 0, false, "не подтверждено источником");
        decided.Ok.Should().BeTrue(decided.Reason);
        decided.Package!.Status.Should().Be(WikiPackage.StatusPartiallyAccepted,
            "отказ по спорным не выбрасывает авто-операции (D0)");

        var r = await _sut.ApplyDecidedAsync(User, Root(), Read(id));

        r.Outcome.Should().Be(WikiApplyService.DecidedOutcome.Ok);
        var apply = Applied(r);
        apply.Applied.Should().BeTrue(apply.Reason);
        r.Package!.Status.Should().Be(WikiPackage.StatusApplied);
        // Отклонённая спорная в рендер не вошла: её страница осталась как была.
        File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), PagePath)).Should()
            .Contain(OldClaim).And.NotContain(Claim);
        // Авто-операция применена обычным путём — тем же, что в пакете без спорных.
        File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), Page2Path)).Should()
            .Contain(AutoClaim, "отказ по спорным не снял авто-операцию");
        StatusOnDisk(id).Should().Be(WikiPackage.StatusApplied);
    }

    [Fact]
    public async Task АвтоAppendКОтклонённомуCreateЭтогоЖеПакета_ОтказПримененияСПричиной()
    {
        // Риск 7 (D0): авто-операция опирается на create этого же пакета, отклонённый
        // решением человека: страницы не было никогда — это не «stale» (база не исчезала,
        // новый разбор предложил бы тот же create). Отказ применения с причиной, страница
        // не записана, пакет не висит в «applying». Мутация «снять ветку CodeDependency»:
        // краснеет StatusOnDisk — пакет стал бы «stale» с причиной про несуществующую базу.
        var newPage = "Общее/Новая-сущность.md";
        var id = NewPackage([CreateOp(newPage), AppendToNewPageOp(newPage)]);
        var decided = WikiPackageStore.DecideOperation(Root(), id, 0, false, "новая страница не нужна");
        decided.Ok.Should().BeTrue(decided.Reason);
        decided.Package!.Status.Should().Be(WikiPackage.StatusPartiallyAccepted);

        var r = await _sut.ApplyDecidedAsync(User, Root(), Read(id));

        r.Outcome.Should().Be(WikiApplyService.DecidedOutcome.Ok);
        var apply = Applied(r);
        apply.Applied.Should().BeFalse();
        apply.Code.Should().Be(WikiApplyService.CodeDependency);
        apply.Reason.Should().NotBeNullOrWhiteSpace("отказ обязан называть причину");
        r.Package!.Status.Should().Be(WikiPackage.StatusApplyFailed);
        StatusOnDisk(id).Should().Be(WikiPackage.StatusApplyFailed);
        File.Exists(Path.Combine(WikiPaths.Pages(_dir, User), newPage)).Should().BeFalse();
    }

    // ─── Фикстура ───────────────────────────────────────────────────────────

    private string Root() => WikiPaths.Root(_dir, User);

    // ApplyResult? → значение: null здесь значит «применять было нечего» (пакет не принят),
    // и это отдельная проверка, а не молчание.
    private static WikiApplyService.ApplyResult Applied(WikiApplyService.DecidedResult r)
    {
        r.Apply.Should().NotBeNull("принятый пакет обязан нести исход применения");
        return r.Apply!.Value;
    }

    private async Task WriteViaKeeper(string pageKey, string content)
    {
        var r = await _keeper.WritePageAsync(User, Section, pageKey, content);
        r.Status.Should().Be(WikiOpStatus.Applied, r.Reason);
    }

    private static WikiPackage.Operation RealOp(string page = PagePath) => new()
    {
        Op = WikiPackage.OpSupersede,
        Page = page,
        Text = Claim,
        Target = OldClaim,
        Class = WikiPackage.ClassKey("session", WikiPackage.OpSupersede),
        Decision = WikiPackage.DecisionInbox,
        Reason = "замещает действующее утверждение",
        BaseHashes = new Dictionary<string, string> { [page] = Hash },
    };

    // Неспорная операция на своей странице: вердикт проверок доводит такой пакет до accepted
    // сам (Sportных нет), решений в нём не появляется ни чужих, ни своих.
    private static WikiPackage.Operation AutoOp() => new()
    {
        Op = WikiPackage.OpAppend,
        Page = Page2Path,
        Text = AutoClaim,
        Class = WikiPackage.ClassKey("session", WikiPackage.OpAppend),
        Decision = WikiPackage.DecisionAuto,
        BaseHashes = new Dictionary<string, string> { [Page2Path] = Hash },
    };

    // create новой страницы — той же формой, что у плана: BaseHashes пуст, базы нет.
    private static WikiPackage.Operation CreateOp(string page) => new()
    {
        Op = WikiPackage.OpCreate,
        Page = page,
        Text = NewClaim,
        Class = WikiPackage.ClassKey("session", WikiPackage.OpCreate),
        Decision = WikiPackage.DecisionInbox,
        Reason = "новая страница знаний",
        BaseHashes = new Dictionary<string, string>(),
    };

    // авто-append к странице, которой на диске нет (она — create этого же пакета): базы нет.
    private static WikiPackage.Operation AppendToNewPageOp(string page) => new()
    {
        Op = WikiPackage.OpAppend,
        Page = page,
        Text = AutoClaim,
        Class = WikiPackage.ClassKey("session", WikiPackage.OpAppend),
        Decision = WikiPackage.DecisionAuto,
        BaseHashes = new Dictionary<string, string>(),
    };

    // Пакет в proposed (его создал бы Хранитель), доведённый до «accepted» вердиктом судьи:
    // тот же путь, что у прохода J-J3a — стор + StatusAfterDecisions.
    private string NewJudgeAcceptedPackage(WikiPackage.Operation op)
    {
        var id = NewPackage([op]);
        var res = WikiPackageStore.JudgeOperations(Root(), id,
            [new WikiPackageStore.JudgeOutcome(0, true, null, JudgeModel, null)]);
        res.Ok.Should().BeTrue(res.Reason);
        StatusOnDisk(id).Should().Be(WikiPackage.StatusAccepted, "вердикт ведёт статус так же, как решение человека");
        return id;
    }

    // Тот же пакет, решённый человеком (для сравнения автора строки журнала).
    private string NewDecidedPackage(WikiPackage.Operation op)
    {
        var id = NewPackage([op]);
        var res = WikiPackageStore.DecideOperation(Root(), id, 0, true, "подтверждаю");
        res.Ok.Should().BeTrue(res.Reason);
        return id;
    }

    // Пакет без спорных операций: вердикт проверок доводит его до accepted сам (спорных нет,
    // «все приняты» — пустое множество), решений человека или судьи в нём не появляется.
    private string NewAutoPackage()
    {
        var id = NewPackage([AutoOp()]);
        WikiPackageStore.Transition(Root(), id, WikiPackage.StatusAccepted).Ok.Should().BeTrue();
        return id;
    }

    private string NewPackage(WikiPackage.Operation[] ops)
    {
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = Section,
            Sources = [new WikiPackage.SourceRef("session", "session:chat-1#m1-9")],
            Operations = ops,
            Status = WikiPackage.StatusChecking,
        };
        WikiPackageStore.Create(Root(), pkg).Ok.Should().BeTrue();
        WikiPackageStore.Transition(Root(), pkg.Id, WikiPackage.StatusProposed).Ok.Should().BeTrue();
        return pkg.Id;
    }

    private WikiPackage Read(string id)
    {
        var res = WikiPackageStore.TryRead(Root(), id);
        res.State.Should().Be(WikiPackageFileState.Valid);
        return res.Package!;
    }

    private string StatusOnDisk(string id) => Read(id).Status;

    private List<string> LogLines(string op)
    {
        var p = Path.Combine(WikiPaths.Keeper(_dir, User), "log.jsonl");
        return File.Exists(p)
            ? File.ReadAllLines(p).Where(l => l.Contains($"\"op\":\"{op}\"")).ToList()
            : [];
    }

    private sealed class WikiFlagOn : IFeatureFlagGate
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
