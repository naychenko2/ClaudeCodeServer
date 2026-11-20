using System.Text;
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

// C5: судья решает merge (операция 13, ADR-039 решение 13). Что здесь проверяется:
// - c1: код merge_proposal допущен судье и есть в полном перечне кодов (форма пакета);
// - c2: вход merge-группы — тексты ОБЕИХ страниц: A (Page, она же страница группы) раньше
//   B (Target); часть B во входе не длиннее MaxPageChars (потолок входа merge — 2 × MaxPageChars);
//   вердикт по merge — только apply/reject: «переписать» судье не дано (Parse его не берёт);
// - c3: решение по merge-пакету не идёт в обучение классов: счётчики WikiClassHealth до и
//   после одинаковы, класса merge нет ни в статистике, ни в автовыключении.
//
// Фейк модели и пакеты через стор — тот же приём, что в WikiJudgeSweepTests; форма
// merge-операции — по контракту C2 (Page=A, Target=B, Text=null). Пакеты для оценки
// классов пишутся на диск напрямую (тот же приём, что в WikiClassHealthTests).
public class WikiJudgeMergeTests : IDisposable
{
    private const string User = "u1";
    private const string Section = "common";
    private const string CardTitle = "Карточка чата";
    private const string CardPath = "Общее/Карточка-чата.md";
    private const string PageAPath = "Общее/Сущность-A.md";
    private const string PageBPath = "Общее/Сущность-B.md";
    private const string MarkerA = "МЕРИТЕЛЬ-А-ТЕКСТ";
    private const string MarkerB = "ЦЕЛЬ-B-ТЕКСТ";
    private const string SourceRef = "session:chat-A";
    private const string MergeClass = "session×merge";
    private const string AppendClass = "session×append";

    private static readonly string CardFile = """
        ---
        type: source
        origin: session
        ref: session:chat-A#m1-9
        hash: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
        captured: 2026-11-20
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Карточка чата
        """;

    private static readonly string PageAFile = $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Сущность-A

        ## Сводка
        {{MarkerA}}: первая страница, которую предлагают слить.

        ## Хронология

        ## Противоречия

        ## История
        """;

    private static readonly string PageBFile = $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Сущность-B

        ## Сводка
        {{MarkerB}}: вторая страница, куда сливают.

        ## Хронология

        ## Противоречия

        ## История
        """;

    private static readonly JsonSerializerOptions PkgJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly WikiApplyService _apply;
    private readonly FlagGate _flags = new();
    private readonly Notifier _notifier = new();
    private readonly WikiClassHealth _classHealth;

    public WikiJudgeMergeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_judge_merge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _notes = new NotesService(projects, _config, NullLogger<NotesService>.Instance, null, flags: _flags);
        _keeper = new WikiKeeper(_notes, _config);
        _apply = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config);
        _classHealth = new WikiClassHealth(_config, _flags, _notifier);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
        WriteViaKeeper(CardPath, CardFile).GetAwaiter().GetResult();
        WriteViaKeeper(PageAPath, PageAFile).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── c1: код допущен судье и в полном перечне ─────────────────────────────

    [Fact]
    public void MergeProposal_ДопущенСудье_ИЕСТЬВПолномПеречнеКодов()
    {
        WikiApplyPolicy.JudgeAdmitted(WikiApplyPolicy.CodeMergeProposal).Should().BeTrue(
            "merge решает судья: код допускает операцию к вердикту");
        WikiApplyPolicy.ReasonCodes.Should().Contain(WikiApplyPolicy.CodeMergeProposal,
            "форма пакета сверяет код причины по перечню");
        WikiApplyPolicy.CodeRejected(WikiApplyPolicy.CodeMergeProposal).Should().BeFalse(
            "merge — не отказ без модели: он идёт вердиктом судьи");
    }

    // ─── c2: вход merge-группы — обе страницы, A раньше B, B ≤ MaxPageChars ───

    [Fact]
    public void BuildGroupInput_Merge_BothPages()
    {
        var textA = MarkerA + ": текст страницы A — целевой страницы группы.";
        // B длиннее потолка втрое. Единица «B000000 » ровно 8 символов и метки уникальны:
        // граница обрезки — единица MaxPageChars/8 = 1000, её метка «B001000» во входе не появляется.
        const int unitLen = 8;
        var lastIncluded = WikiJudgeSweep.MaxPageChars / unitLen - 1;
        var textB = string.Concat(Enumerable.Range(0, 3 * WikiJudgeSweep.MaxPageChars / unitLen)
            .Select(i => $"B{i:D6} "));

        var input = WikiJudgeSweep.BuildGroupInput([(MergeOp(), 0)], textA, [textB]);

        input.Should().Contain(WikiJudgeSweep.MergeTargetLabel, "вход называет вторую страницу целью слияния");
        input.Should().Contain(textA, "текст A (Page) — входе");
        input.Should().Contain("B000000", "текст B (Target) начинается во входе");
        input.Should().Contain($"B{lastIncluded:D6}", "первые MaxPageChars символов B — входе целиком");
        input.Should().NotContain($"B{lastIncluded + 1:D6}", "часть B во входе не длиннее MaxPageChars");
        input.IndexOf(textA, StringComparison.Ordinal)
            .Should().BeLessThan(input.IndexOf("B000000", StringComparison.Ordinal),
                "A раньше B во входе судьи (контракт порядка C5)");
    }

    [Fact]
    public async Task MergeПолныйПуть_ОдинВызов_ВходеОбеСтраницы_AРаньшеB_ВердикЗаписан()
    {
        await WriteViaKeeper(PageBPath, PageBFile);
        var id = NewPackage([MergeOp()]);
        var llm = new FakeJudge(VerdictsJson((0, false, "страницы не дубли: объединять нечего")));

        var res = await Sut(llm).RunAsync(User, Root());

        res.Calls.Should().Be(1, "merge-группа — один вызов");
        var prompt = llm.Calls[0].Prompt;
        prompt.Should().Contain("op: merge");
        prompt.Should().Contain(MarkerA, "текст страницы A — входе merge-группы");
        prompt.Should().Contain(MarkerB, "текст страницы B (Target) — входе merge-группы");
        prompt.IndexOf(MarkerA, StringComparison.Ordinal)
            .Should().BeLessThan(prompt.IndexOf(MarkerB, StringComparison.Ordinal),
                "A раньше B и в живом проходе");
        res.Rejected.Should().Be(1);
        res.FirstFailure.Should().BeNull();

        var pkg = Read(id);
        pkg.Operations[0].JudgeDecision.Should().Be(WikiPackage.HumanRejected);
        pkg.Status.Should().Be(WikiPackage.StatusRejected, "единственная спорная отклонена судьёй");
    }

    [Fact]
    public async Task MergeОтветRewrite_ВердиктаНет_ПопыткаПлюсОдин_ПакетЖдёт()
    {
        await WriteViaKeeper(PageBPath, PageBFile);
        var id = NewPackage([MergeOp()]);
        // «Переписать» судье не дано: промпт этого не предлагает, разбор такого не берёт.
        var llm = new FakeJudge("""{"verdicts":[{"index":0,"decision":"rewrite","why":"слиять с правкой"}]}""");

        var res = await Sut(llm).RunAsync(User, Root());

        res.Calls.Should().Be(1);
        res.Deferred.Should().Be(1, "вердикта по операции нет — засчитана попытка");
        res.Applied.Should().Be(0);
        res.Rejected.Should().Be(0);
        var pkg = Read(id);
        pkg.Operations[0].JudgeDecision.Should().BeNull();
        pkg.Operations[0].JudgeAttempts.Should().Be(1);
        pkg.Status.Should().Be(WikiPackage.StatusProposed);
    }

    // ─── c3: merge не идёт в обучение классов ─────────────────────────────────

    [Fact]
    public async Task MergeНеИдётВОбучениеКлассов_СчётчикиДоИПослеОдинаковы()
    {
        var now = DateTimeOffset.UtcNow;
        var recent = now - TimeSpan.FromDays(1);

        for (var i = 0; i < 2; i++)
            WriteAppliedPackage(AppendApplied("a" + i), recent);

        var before = await _classHealth.EvaluateAsync(User, now);
        before.State.Should().Be(WikiClassHealth.RunState.Ran);
        before.Stats.Should().ContainSingle(s => s.Class == AppendClass && s.Applied7 == 2);

        // Три применённых и откатанных merge за 7 дней: если бы merge считался классом,
        // это был бы ровно порог автовыключения (3 отката за 7 дней) и строка метрик
        // нового класса. По решению C5 — ни того, ни другого.
        for (var i = 0; i < 3; i++)
            WriteAppliedPackage(MergeApplied("m" + i), recent, retractedAt: recent);

        var after = await _classHealth.EvaluateAsync(User, now);

        after.Stats.Should().Equal(before.Stats, "счётчики класса не изменились: merge в них не вошёл");
        after.Stats.Should().NotContain(s => s.Class == MergeClass, "класса merge в обучении нет");
        ReadSettings().IsClassDisabled(MergeClass).Should().BeFalse("автовыключение merge не касается");
        _notifier.Sent.Should().BeEmpty("перехода «включён → выключен» нет: уведомления нет");
    }

    // ─── Фикстура ─────────────────────────────────────────────────────────────

    private string Root() => WikiPaths.Root(_dir, User);

    private WikiJudgeSweep Sut(ICheapTextRunner llm) => new(llm, _apply, _config);

    private async Task WriteViaKeeper(string pageKey, string content)
    {
        var r = await _keeper.WritePageAsync(User, Section, pageKey, content);
        r.Status.Should().Be(WikiOpStatus.Applied, r.Reason);
    }

    /// <summary>merge-операция по контракту C2: Page=A, Target=B, Text=null. Строится
    /// напрямую — без валидации пакета (её расширяет C2).</summary>
    private static WikiPackage.Operation MergeOp() => new()
    {
        Op = WikiPackage.OpMerge,
        Page = PageAPath,
        Target = PageBPath,
        Class = WikiPackage.ClassKey("session", WikiPackage.OpMerge),
        Decision = WikiPackage.DecisionInbox,
        Reason = "предложение слить страницы: A и B говорят об одном",
        ReasonCode = WikiApplyPolicy.CodeMergeProposal,
    };

    private string NewPackage(WikiPackage.Operation[] ops)
    {
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = Section,
            Sources = [new WikiPackage.SourceRef("session", "session:chat-A#m1-9")],
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

    private static WikiPackage.Operation AppendApplied(string id) => new()
    {
        Op = WikiPackage.OpAppend,
        Page = "Общее/P-" + id + ".md",
        Text = "утверждение из чата",
        Class = AppendClass,
        Decision = WikiPackage.DecisionAuto,
    };

    /// <summary>Применённый и принятый merge (форма «применился» по правилу WasApplied):
    /// inbox + решение человека/судьи accepted.</summary>
    private static WikiPackage.Operation MergeApplied(string id) => new()
    {
        Op = WikiPackage.OpMerge,
        Page = "Общее/A-" + id + ".md",
        Target = "Общее/B-" + id + ".md",
        Class = MergeClass,
        Decision = WikiPackage.DecisionInbox,
        Reason = "предложение слить страницы",
        ReasonCode = WikiApplyPolicy.CodeMergeProposal,
        HumanDecision = WikiPackage.HumanAccepted,
    };

    /// <summary>Пакет на диске: состояние, которое читает оценка классов (тот же приём,
    /// что в WikiClassHealthTests). retractedAt != null — статус «reverted» с временем
    /// отката в UpdatedAt.</summary>
    private string WriteAppliedPackage(WikiPackage.Operation op, DateTimeOffset createdAt,
        DateTimeOffset? retractedAt = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var pkg = new WikiPackage
        {
            V = WikiPackage.ModelVersion,
            Id = id,
            Author = WikiPackage.AuthorAi,
            Section = Section,
            Sources = [new WikiPackage.SourceRef("session", SourceRef)],
            Operations = [op],
            Status = retractedAt is null ? WikiPackage.StatusApplied : WikiPackage.StatusReverted,
            CreatedAt = createdAt,
            UpdatedAt = retractedAt ?? createdAt,
        };
        var dir = Path.Combine(WikiPaths.Keeper(_dir, User), "packages");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".json"), JsonSerializer.Serialize(pkg, PkgJsonOpts), new UTF8Encoding(false));
        return id;
    }

    private WikiSettingsStore.Settings ReadSettings() => WikiSettingsStore.Read(Root()).Settings;

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

    /// <summary>Фейк шва: пишет каждый вызов (ключ места, метод, prompt).</summary>
    private sealed class FakeJudge : ICheapTextRunner
    {
        public List<(string ActionKey, string Method, string Prompt)> Calls { get; } = [];

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

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Record(actionKey, "RunDetailed", prompt);
            return Task.FromResult(new OneShotResult(_answer ?? "", new OneShotUsage(10, 0, 0, 5, 0.012, "fake-judge"), 1));
        }

        private void Record(string actionKey, string method, string prompt)
        {
            lock (Calls) Calls.Add((actionKey, method, prompt));
        }
    }

    private sealed class FlagGate : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }

    private sealed class Notifier : INotificationSender
    {
        public List<CreateNotificationRequest> Sent { get; } = [];

        public Task<string> SendAsync(string userId, CreateNotificationRequest req, bool sendPush = false)
        {
            Sent.Add(req);
            return Task.FromResult("n-" + Sent.Count);
        }
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
