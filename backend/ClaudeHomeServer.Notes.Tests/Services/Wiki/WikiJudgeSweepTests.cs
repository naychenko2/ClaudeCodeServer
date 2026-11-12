using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// J-J3a: проход облачного судьи «Входящих». Что здесь проверяется и почему именно так:
// - И4′: отказ по коду выносит код БЕЗ вызова модели, и его claim не попадает в вход
//   судьи (критерий c1: один вызов на группу, внешний текст к судье не идёт);
// - ручной режим (c2): выключатель «Применять без ревью» выключен — ни вызова, ни
//   записи; при включённом «личное в Общем» — допущено и идёт модели (решение Андрея
//   12.11: без отдельного согласия);
// - попытка и исход (c3): отказ вызова — попытка +1, пакет остаётся proposed; на 3-й
//   неудаче отказ выносит код; исключение модели наружу не выходит;
// - замок и гонка (c4): замок по корню — второй параллельный проход того же владельца
//   не ведёт второй вызов той же группы; решение человека во время облачного вызова
//   сохраняется, вердикт судьи его не перекрывает (И1);
// - счётчики (ревью J-J2a): в Applied/Rejected/Deferred идёт только то, что реально
//   записалось в файл.
// - группа вызова (доработка ревью J-J3a, Н1): пакет с допущенными операциями на разных
//   страницах — отдельный вызов на каждую страницу: в вход группы идут текст и claim
//   только своей страницы (потеря GroupBy по Page не красила ни одного теста раньше).
//
// Фейк модели — тот же приём, что в WikiIngestSynthTests: запись каждого вызова
// (ключ, метод шва, prompt), ответ — JSON вердиктов. Пакеты ставит сам стор (тот же
// путь, что в WikiApplyJudgeTests): Create → proposed.
public class WikiJudgeSweepTests : IDisposable
{
    private const string User = "u1";
    private const string Section = "common";
    private const string CardTitle = "Карточка чата";
    private const string CardPath = "Общее/Карточка-чата.md";
    private const string PagePath = "Общее/Сущность.md";
    private const string Page2Path = "Общее/Сущность-2.md";
    private const string OldClaim = "Прежний план был остаться на Windows";
    private const string Page2OldClaim = "Прежний план второй страницы был остаться на второй машине";
    private const string ExternalClaim = "ВНЕШНИЙ-ТЕКСТ-СУДЬЕ-НЕ-ПОКАЗАТЬ";

    private static readonly string CardFile = """
        ---
        type: source
        origin: session
        ref: session:chat-1#m1-9
        hash: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
        captured: 2026-10-30
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Карточка чата
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

    // Вторая целевая страница — со своим текстом и своим прежним планом: по нему видно,
    // чей текст попал в вход судьи (тест группы вызова, ревью J-J3a Н1). Ставится не в
    // фикстуре, а в самом тесте: остальным тестам класса вторая страница не нужна.
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
        {{Page2OldClaim}} [^s1]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[{{CardTitle}}]] — «{{Page2OldClaim}}»
        """;

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly WikiApplyService _apply;

    public WikiJudgeSweepTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_judge_sweep_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _notes = new NotesService(projects, _config, NullLogger<NotesService>.Instance, null, flags: new WikiFlagOn());
        _keeper = new WikiKeeper(_notes, _config);
        _apply = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
        WriteViaKeeper(CardPath, CardFile).GetAwaiter().GetResult();
        WriteViaKeeper(PagePath, BasePage).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── c1: один вызов на группу; отказ по коду к модели не идёт (И4′) ────────────────

    [Fact]
    public async Task ТриДопущенныеОднаПоКоду_ОдинВызовWikiJudge_КодНеВХоде_ОтказПоКодуБезМодели()
    {
        var id = NewPackage([
            Supersede(0, "Первый план: перенести сборку на Linux"),
            Supersede(1, "Второй план: собрать на второй машине"),
            Supersede(2, "Третий план: собрать в контейнере"),
            External(ExternalClaim),
        ]);
        var llm = new FakeJudge(VerdictsJson((0, true, "цитаты подтверждают"), (1, false, "противоречит странице"), (2, true, "подтверждено")));

        var res = await Sut(llm).RunAsync(User, Root());

        res.Calls.Should().Be(1, "одна страница — один вызов группы");
        llm.Calls.Should().ContainSingle().Which.ActionKey.Should().Be(LocalActionCatalog.WikiJudge);
        llm.Calls[0].Prompt.Should().NotContain(ExternalClaim,
            "отказ по коду (origin_url) не доходит до модели: её вход — только допущенные операции (И4′)");
        llm.Calls[0].Prompt.Should().Contain("Первый план: перенести сборку на Linux", "допущенные операции группы — входе");

        var pkg = Read(id);
        var byCode = pkg.Operations[3];
        byCode.JudgeDecision.Should().Be(WikiPackage.HumanRejected);
        byCode.JudgeModel.Should().Be(WikiPackage.JudgeModelCode, "вердикт по коду отказа — не вердикт судьи");
        byCode.JudgeReason.Should().StartWith("отклонено кодом:");
        pkg.Operations[0].JudgeDecision.Should().Be(WikiPackage.HumanAccepted);
        pkg.Operations[1].JudgeDecision.Should().Be(WikiPackage.HumanRejected);
        res.Applied.Should().Be(2, "принятых вердикта записано два");
        res.Rejected.Should().Be(1, "один отказ вынесла модель");
        res.CodeRejected.Should().Be(1, "один отказ вынес код, без модели");
        res.FirstFailure.Should().BeNull();
    }

    // ─── c2: ручной режим не зовёт модель и не трогает файл; «личное» допущено ─────────

    [Fact]
    public async Task АвтоПрименениеВыключено_НиОдногоВызова_ФайлПакетаНеТронут()
    {
        var id = NewPackage([Supersede(0, "Первый план: перенести сборку на Linux")]);
        var before = File.ReadAllText(PackageFile(id));
        WikiSettingsStore.Mutate(_dir, User, st => st with { AutoApplyEnabled = false });
        var llm = new FakeJudge(VerdictsJson((0, true, "не должен быть позван")));

        var res = await Sut(llm).RunAsync(User, Root());

        res.Calls.Should().Be(0, "ручной режим: всё как раньше, решает человек");
        llm.Calls.Should().BeEmpty();
        File.ReadAllText(PackageFile(id)).Should().Be(before, "файл пакета — побайтно тот же");
        Read(id).Operations[0].JudgeDecision.Should().BeNull();
    }

    [Fact]
    public async Task ЛичноеВОбщем_ПриВключённомТумблере_ИдетВызовМодели_БезОтдельногоСогласия()
    {
        var id = NewPackage([OpWithCode(WikiPackage.OpAppend, WikiApplyPolicy.CodePersonalToCommon,
            "личное в «Общем»: решение о видимости принимает человек", "Личное знание про сборку")]);
        var llm = new FakeJudge(VerdictsJson((0, true, "личное, но подтверждено")));

        var res = await Sut(llm).RunAsync(User, Root());

        res.Calls.Should().Be(1);
        llm.Calls[0].Prompt.Should().Contain("Личное знание про сборку");
        Read(id).Operations[0].JudgeDecision.Should().Be(WikiPackage.HumanAccepted);
    }

    // Старый пакет без кода причины и операция autoapply_off — не допущены (И4′),
    // и отказа нет: их просто не трогать.
    [Fact]
    public async Task БезКодаИАвтоapplyOff_НеТрогает_НиМодели_НиВердикта()
    {
        var id = NewPackage([
            OpWithCode(WikiPackage.OpAppend, null, "старый пакет без кода", "Старое знание"),
            OpWithCode(WikiPackage.OpAppend, WikiApplyPolicy.CodeAutoApplyOff, "ручной режим", "Ручное знание"),
        ]);
        var llm = new FakeJudge(VerdictsJson());

        var res = await Sut(llm).RunAsync(User, Root());

        res.Calls.Should().Be(0);
        res.Deferred.Should().Be(0);
        res.FirstFailure.Should().BeNull();
        var pkg = Read(id);
        pkg.Operations.Should().OnlyContain(o => o.JudgeDecision == null && o.JudgeAttempts == 0);
        pkg.Status.Should().Be(WikiPackage.StatusProposed);
    }

    // ─── c3: попытка, 3 попытки — отказ кода; исключение наружу не выходит ─────────────

    [Fact]
    public async Task ТриПроходаСОтказомМодели_Попытки1И2ПотомОтказКода_ИсключениеНаружуНеВышло()
    {
        var id = NewPackage([
            Supersede(0, "Первый план: перенести сборку на Linux"),
            Supersede(1, "Второй план: собрать на второй машине"),
        ]);
        var llm = new FakeJudge(null) { Throw = new InvalidOperationException("облако недоступно (429)") };
        var sut = Sut(llm);

        var r1 = await sut.RunAsync(User, Root());
        r1.Calls.Should().Be(1);
        r1.Deferred.Should().Be(2, "отказ вызова — попытка +1 всем допущенным группы");
        r1.FirstFailure.Should().Contain("облако недоступно");
        Read(id).Status.Should().Be(WikiPackage.StatusProposed);
        Read(id).Operations.Should().OnlyContain(o => o.JudgeAttempts == 1 && o.JudgeDecision == null);

        var r2 = await sut.RunAsync(User, Root());
        r2.Deferred.Should().Be(2);
        Read(id).Status.Should().Be(WikiPackage.StatusProposed);
        Read(id).Operations.Should().OnlyContain(o => o.JudgeAttempts == 2 && o.JudgeDecision == null);

        var r3 = await sut.RunAsync(User, Root());
        r3.Rejected.Should().Be(2, "на 3-й неудаче отказ выносит код");
        r3.Deferred.Should().Be(0);
        var pkg = Read(id);
        pkg.Operations.Should().OnlyContain(o => o.JudgeDecision == WikiPackage.HumanRejected
            && o.JudgeReason!.Contains("3 попытки"));
        pkg.Status.Should().Be(WikiPackage.StatusRejected, "все спорные отклонены — статус ведёт вердикт");
        llm.Calls.Should().HaveCount(3, "каждый проход звал модель заново: потолка вызовов за ночь нет");
    }

    // Ответ модели есть, вердикта по операции нет — попытка +1, пакет ждёт.
    [Fact]
    public async Task ОтветБезВердиктаПоОперации_ПопыткаПлюсОдин_ПакетЖдётСледующегоПрохода()
    {
        var id = NewPackage([Supersede(0, "Первый план: перенести сборку на Linux")]);
        var llm = new FakeJudge(VerdictsJson((7, true, "вердикт не из этого пакета")));

        var res = await Sut(llm).RunAsync(User, Root());

        res.Calls.Should().Be(1);
        res.Deferred.Should().Be(1);
        res.Applied.Should().Be(0);
        var pkg = Read(id);
        pkg.Operations[0].JudgeAttempts.Should().Be(1);
        pkg.Operations[0].JudgeDecision.Should().BeNull();
        pkg.Status.Should().Be(WikiPackage.StatusProposed);
    }

    // ─── c4: замок по корню; решение человека во время вызова сохраняется ──────────────

    [Fact]
    public async Task ДваПроходаОдновременно_ОдинВызовНаПакет_РешениеЧеловекаВоВремяВызоваСохранено()
    {
        var id = NewPackage([
            Supersede(0, "Первый план: перенести сборку на Linux"),
            Supersede(1, "Второй план: собрать на второй машине"),
        ]);
        var llm = new FakeJudge(VerdictsJson((0, true, "судья счёл применимым"), (1, true, "и это применимо")))
        {
            Enter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var sut = Sut(llm);

        var first = sut.RunAsync(User, Root());
        await llm.Enter.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = sut.RunAsync(User, Root());

        // Пока модель думала, человек решил первую операцию своим путём (REST S4a).
        WikiPackageStore.DecideOperation(Root(), id, 0, true, "я решил сам").Ok.Should().BeTrue();

        llm.Release.SetResult();
        var r1 = await first;
        var r2 = await second;

        r1.Calls.Should().Be(1);
        r2.Calls.Should().Be(0, "второй проход того же владельца ждал первого и увидел пакет решённым");
        llm.Calls.Should().ContainSingle().Which.ActionKey.Should().Be(LocalActionCatalog.WikiJudge,
            "без замка по корню второй проход позвал бы модель по той же группе сам");
        r1.Rejected.Should().Be(0, "вердикт по операции, которую решил человек, не записался");

        var pkg = Read(id);
        pkg.Operations[0].HumanDecision.Should().Be(WikiPackage.HumanAccepted, "решение человека осталось");
        pkg.Operations[0].JudgeDecision.Should().BeNull("И1: у спорной операции ровно одно решение");
        pkg.Operations[1].JudgeDecision.Should().Be(WikiPackage.HumanAccepted);
    }

    // Пакет человека и пакет не-проposed — не кандидаты: проход их не трогает.
    [Fact]
    public async Task ПакетЧеловекаИAcceptedПакет_НеКандидаты_НиОдногоВызова()
    {
        var humanId = Guid.NewGuid().ToString("N");
        WikiPackageStore.CreateHuman(Root(), new WikiPackage
        {
            Id = humanId,
            Author = WikiPackage.AuthorHuman,
            Action = WikiPackage.ActionEdit,
            Section = Section,
            Sources = [new WikiPackage.SourceRef("human", "human:u1")],
            Operations = [new WikiPackage.Operation
            {
                Op = WikiPackage.OpEdit,
                Page = PagePath,
                Text = "моя правка",
                Target = OldClaim,
                Class = WikiPackage.ClassKey("human", WikiPackage.OpEdit),
                Decision = WikiPackage.DecisionAuto,
            }],
            Status = WikiPackage.StatusAccepted,
        }).Ok.Should().BeTrue();

        var aiId = NewPackage([Supersede(0, "Первый план: перенести сборку на Linux")]);
        WikiPackageStore.Transition(Root(), aiId, WikiPackage.StatusAccepted).Ok.Should().BeTrue();

        var llm = new FakeJudge(VerdictsJson());
        var res = await Sut(llm).RunAsync(User, Root());

        res.Calls.Should().Be(0);
        Read(humanId).Operations[0].JudgeDecision.Should().BeNull();
    }

    // Чужой корень — отказ на входе без единого вызова: проход работает только по своей вики.
    [Fact]
    public async Task ЧужойКорень_ОтказНаВходе_БезВызоваМодели()
    {
        var id = NewPackage([Supersede(0, "Первый план: перенести сборку на Linux")]);
        var llm = new FakeJudge(VerdictsJson((0, true, "не должен быть позван")));

        var res = await Sut(llm).RunAsync(User, Path.Combine(_dir, "wiki", "u2"));

        res.Calls.Should().Be(0);
        res.FirstFailure.Should().Contain("не корень этого владельца");
        Read(id).Operations[0].JudgeDecision.Should().BeNull();
    }

    // ─── c5: группа «пакет + целевая страница»: разные страницы — свой вызов на группу ─

    [Fact]
    public async Task ОперацииНаДвухСтраницах_ДваВызова_ВКаждом_ТекстИClaimТолькоСвоейСтраницы()
    {
        await WriteViaKeeper(Page2Path, BasePage2);
        // Порядок в пакете перемешан: группа — не подряд идущие операции, а все операции
        // одной страницы (GroupBy по Page).
        var id = NewPackage([
            Supersede(0, "Первый план: перенести сборку на Linux"),
            SupersedeOnPage2(1, "Второй план: перенести синтез на локальную модель"),
            Supersede(2, "Третий план: собрать в контейнере"),
        ]);
        var llm = new FakeJudge(VerdictsJson(
            (0, true, "цитаты подтверждают"), (1, true, "подтверждено"), (2, true, "подтверждено")));

        var res = await Sut(llm).RunAsync(User, Root());

        res.Calls.Should().Be(2, "одна страница — один вызов группы: две страницы — два");
        llm.Calls.Should().HaveCount(2);
        llm.Calls.Should().OnlyContain(c => c.ActionKey == LocalActionCatalog.WikiJudge);

        // Первая группа — «Сущность»: её операции и текст ЕЁ страницы, ничего из «Сущность-2».
        var first = llm.Calls[0].Prompt;
        first.Should().Contain("page: " + PagePath);
        first.Should().Contain("Первый план: перенести сборку на Linux");
        first.Should().Contain("Третий план: собрать в контейнере");
        first.Should().Contain(OldClaim, "вход группы — текст её целевой страницы");
        first.Should().NotContain("page: " + Page2Path);
        first.Should().NotContain("Второй план: перенести синтез на локальную модель",
            "claim чужой группы в этот вызов не идёт");
        first.Should().NotContain(Page2OldClaim, "текст чужой страницы в этот вызов не идёт");

        // Вторая группа — «Сущность-2»: зеркально.
        var second = llm.Calls[1].Prompt;
        second.Should().Contain("page: " + Page2Path);
        second.Should().Contain("Второй план: перенести синтез на локальную модель");
        second.Should().Contain(Page2OldClaim, "вход группы — текст её целевой страницы");
        second.Should().NotContain("page: " + PagePath);
        second.Should().NotContain("Первый план: перенести сборку на Linux");
        second.Should().NotContain("Третий план: собрать в контейнере");
        second.Should().NotContain(OldClaim, "текст чужой страницы в этот вызов не идёт");

        var pkg = Read(id);
        pkg.Operations[0].JudgeDecision.Should().Be(WikiPackage.HumanAccepted);
        pkg.Operations[1].JudgeDecision.Should().Be(WikiPackage.HumanAccepted);
        pkg.Operations[2].JudgeDecision.Should().Be(WikiPackage.HumanAccepted);
        res.Applied.Should().Be(3, "вердикты обеих групп записаны");
        res.Rejected.Should().Be(0);
        res.Deferred.Should().Be(0);
        res.FirstFailure.Should().BeNull();
    }

    // ─── Фикстура ─────────────────────────────────────────────────────────────────────

    private string Root() => WikiPaths.Root(_dir, User);

    private WikiJudgeSweep Sut(ICheapTextRunner llm) => new(llm, _apply, _config);

    private string PackageFile(string id) => SafePath.JoinBelowRoot(Root(), $"keeper/packages/{id}.json");

    private async Task WriteViaKeeper(string pageKey, string content)
    {
        var r = await _keeper.WritePageAsync(User, Section, pageKey, content);
        r.Status.Should().Be(WikiOpStatus.Applied, r.Reason);
    }

    // Спорная операция на общей странице с допущенным кодом (target_disputed):
    // индекс в Operations — тот, что отдаёт проход (позиция в пакете).
    private static WikiPackage.Operation Supersede(int n, string claim) =>
        OpWithCode(WikiPackage.OpSupersede, WikiApplyPolicy.CodeTargetDisputed,
            $"целевая страница в статусе «disputed»: там уже нужен человек {n}", claim, OldClaim);

    // Та же спорная операция, но о второй целевой странице: цель — прежний план СВОЕЙ
    // страницы (проход группирует по Page, и цель должна принадлежать своей странице).
    private static WikiPackage.Operation SupersedeOnPage2(int n, string claim) =>
        OpWithCode(WikiPackage.OpSupersede, WikiApplyPolicy.CodeTargetDisputed,
            $"целевая страница в статусе «disputed»: там уже нужен человек {n}", claim,
            Page2OldClaim, Page2Path);

    private static WikiPackage.Operation External(string claim) =>
        OpWithCode(WikiPackage.OpAppend, WikiApplyPolicy.CodeOriginUrl,
            "внешний текст (origin url): промпт-инъекция извне", claim);

    private static WikiPackage.Operation OpWithCode(string op, string? reasonCode, string reason,
        string claim, string? target = null, string? page = null) => new()
    {
        Op = op,
        Page = page ?? PagePath,
        Text = claim,
        Target = target,
        Class = WikiPackage.ClassKey("session", op),
        Decision = WikiPackage.DecisionInbox,
        Reason = reason,
        ReasonCode = reasonCode,
    };

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

    /// <summary>Ответ судьи по контракту промпта: verdicts[{index,decision,why}].</summary>
    private static string VerdictsJson(params (int Index, bool Apply, string Why)[] verdicts) =>
        JsonSerializer.Serialize(new
        {
            verdicts = verdicts.Select(v => new
            {
                index = v.Index,
                decision = v.Apply ? "apply" : "reject",
                why = v.Why,
            }).ToList(),
        });

    /// <summary>Фейк шва: пишет каждый вызов (ключ места, метод, prompt). Answer=null —
    /// пустой ответ; Throw — бросает. Enter/Release — блокирующий вызов для теста гонки:
    /// Enter сигнализирует, что проход вошёл в модель, Release отпускает ответ.</summary>
    private sealed class FakeJudge : ICheapTextRunner
    {
        public List<(string ActionKey, string Method, string Prompt)> Calls { get; } = [];
        public Exception? Throw { get; init; }
        public TaskCompletionSource? Enter { get; init; }
        public TaskCompletionSource? Release { get; init; }

        private readonly string? _answer;

        public FakeJudge(string? answer) => _answer = answer;

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Record(actionKey, "RunAsync", prompt);
            return Task.FromResult(_answer ?? "");
        }

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            Record(actionKey, "RunLocalOrChosen", prompt);
            return Task.FromResult<string?>(_answer);
        }

        public async Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Record(actionKey, "RunDetailed", prompt);
            if (Enter is not null)
            {
                Enter.SetResult();
                if (Release is not null)
                    await Release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            if (Throw is not null)
                throw Throw;
            return new OneShotResult(_answer ?? "", new OneShotUsage(10, 0, 0, 5, 0.012, "fake-judge"), 1);
        }

        private void Record(string actionKey, string method, string prompt)
        {
            lock (Calls) Calls.Add((actionKey, method, prompt));
        }
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
