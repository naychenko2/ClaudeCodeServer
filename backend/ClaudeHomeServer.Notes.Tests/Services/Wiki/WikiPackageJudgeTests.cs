using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;

namespace ClaudeHomeServer.Tests.Services;

// J-J2a (И1): у спорной операции ровно одно решение — человека (ручной режим) или облачного
// судьи. Вердикт модели живёт в отдельных полях `Judge*` операции: на HumanDecision стоят
// обучение классов автоприменению (WikiClassHealth.WasApplied) и журнал «правка человека»,
// поэтому решение модели туда не пишется — иначе классы переводились бы в auto по одобрению
// модели, а журнал врал бы про автора. Здесь: запись вердикта в стор, его место в статусе
// пакета, отказ повторного решения и отсутствие вердикта в статистике класса.
public class WikiPackageJudgeTests : IDisposable
{
    // Форма хэша та же, что в манифесте: 64 hex нижнего регистра.
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string ClassKey = "session×append";
    private const string JudgeModel = "cloud-judge-1";
    // Для теста оценки классов: два владельца с одинаковой базой применённых операций.
    private const string User = "u1";
    private const string Other = "u2";

    private readonly string _root;
    private readonly string _dataDir;
    private readonly WikiClassHealth _health;

    public WikiPackageJudgeTests()
    {
        var stamp = Guid.NewGuid().ToString("N");
        _root = Path.Combine(Path.GetTempPath(), "wiki_pkg_judge_" + stamp);
        _dataDir = Path.Combine(Path.GetTempPath(), "wiki_judge_health_" + stamp);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_dataDir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dataDir, "projects.json") })
            .Build();
        // Диспетчер здесь не нужен: ни один класс этого теста не порождает выключение.
        _health = new WikiClassHealth(config, new FlagGate());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true);
    }

    // ─── c1: гонка человека и судьи по одной операции ───────────────────────

    [Fact]
    public async Task ГонкаЧеловекаИСудьи_РовноОдноРешение_50Итераций()
    {
        // И1 под гонкой: DecideOperation (человек) и JudgeOperations (судья) стартуют от одного
        // барьера по одной и той же операции. Без блока в JudgeOperations оба читают одно
        // базовое состояние, и последнее письмо молча теряет чужое решение; 50 повторов ловят
        // это не случайно. Пакет с 100 операциями: чтение и перезапись занимают миллисекунды,
        // окно гонки — не случайность планировщика (тот же приём, что в тесте параллельных
        // решений стора).
        for (var iter = 0; iter < 50; iter++)
        {
            // Две спорные операции: и человек, и судья целиются в индекс 0, вторая остаётся
            // нерешённой — пакет всё время «Входящий», и проигравший получает исход по самой
            // операции (CodeAlreadyDecided / молча пропущенная запись), а не по статусу.
            var ops = new List<WikiPackage.Operation> { InboxOp("Общее/X.md"), InboxOp("Общее/Y.md") };
            for (var i = 2; i < 100; i++) ops.Add(AutoOp($"Общее/A{i:D3}.md"));
            var id = AtProposed(ops.ToArray());

            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var human = Task.Run(async () => { await barrier.Task; return WikiPackageStore.DecideOperation(_root, id, 0, true); });
            var judge = Task.Run(async () =>
            {
                await barrier.Task;
                return WikiPackageStore.JudgeOperations(_root, id, [new WikiPackageStore.JudgeOutcome(0, true, null, JudgeModel, 0.001m)]);
            });
            barrier.SetResult();

            var r1 = await human.WaitAsync(TimeSpan.FromSeconds(30));
            var r2 = await judge.WaitAsync(TimeSpan.FromSeconds(30));

            var op = Read(id).Operations[0];
            var byHuman = op.HumanDecision is not null;
            var byJudge = op.JudgeDecision is not null;
            (byHuman ^ byJudge).Should().BeTrue(
                $"итерация {iter}: ровно одно решение — human=«{op.HumanDecision}», judge=«{op.JudgeDecision}»");

            if (byHuman)
            {
                // Проиграл судья: пакет ещё «Входящий», поэтому он не отказан — он просто
                // не пишет в уже решённую операцию.
                r2.Ok.Should().BeTrue(r2.Reason);
                op.JudgeDecision.Should().BeNull();
                op.JudgeAttempts.Should().Be(0);
            }
            else
            {
                // Человек пришёл к решённой судьёй операции: отказ, а не переворот решения.
                r1.Ok.Should().BeFalse();
                r1.Code.Should().Be(WikiPackageStore.CodeAlreadyDecided);
                op.HumanDecision.Should().BeNull();
            }

            // Вторая спорная не решена — статус не двигался ни в одну сторону.
            Read(id).Status.Should().Be(WikiPackage.StatusProposed);
            // Закрыть пакет (вторая спорная — за человеком): иначе следующая итерация
            // упрётся в лимит 20 открытых.
            WikiPackageStore.AcceptAllContested(_root, id).Ok.Should().BeTrue();
            Close(id);
        }
    }

    // ─── c2: вердикт судьи ведёт статус так же, как решение человека ────────

    [Fact]
    public void СудьяРешаетОбеСпорные_СтатусТотЧтоДалБыЧеловек()
    {
        WikiPackage.Operation[] ops = [InboxOp("Общее/A.md"), InboxOp("Общее/B.md")];
        var judgedId = AtProposed(ops);
        var res = WikiPackageStore.JudgeOperations(_root, judgedId,
        [
            new WikiPackageStore.JudgeOutcome(0, true, null, JudgeModel, 0.0025m),
            new WikiPackageStore.JudgeOutcome(1, false, "замещает без цитаты", JudgeModel, 0.0025m),
        ]);
        res.Ok.Should().BeTrue(res.Reason);

        // Контроль: тот же пакет и те же исходы, но решённые человеком.
        var humanId = AtProposed(ops);
        WikiPackageStore.DecideOperation(_root, humanId, 0, true).Ok.Should().BeTrue();
        WikiPackageStore.DecideOperation(_root, humanId, 1, false, "та же причина").Ok.Should().BeTrue();

        var judged = Read(judgedId);
        var human = Read(humanId);
        judged.Status.Should().Be(human.Status, "вердикт судьи ведёт статус ровно так же, как решение человека");
        judged.Status.Should().Be(WikiPackage.StatusPartiallyAccepted);

        // Вердикт — в полях судьи; поле человека остаётся человеческим (обучение классов и
        // журнал «правка человека» читают его).
        judged.Operations[0].JudgeDecision.Should().Be(WikiPackage.HumanAccepted);
        judged.Operations[0].HumanDecision.Should().BeNull();
        judged.Operations[0].JudgeModel.Should().Be(JudgeModel);
        judged.Operations[0].JudgeAt.Should().NotBeNull("дату решения ставит сервер");
        judged.Operations[0].JudgeCostUsd.Should().Be(0.0025m);
        judged.Operations[1].JudgeDecision.Should().Be(WikiPackage.HumanRejected);
        judged.Operations[1].JudgeReason.Should().Be("замещает без цитаты");
        judged.Operations[0].JudgeAttempts.Should().Be(0, "попытка с исходом — решение, а не попытка");

        // Все спорные решены → пакет вышел из «Входящих»: решение по его операциям вообще
        // не принимается (не только по решённой судьёй).
        var late = WikiPackageStore.DecideOperation(_root, judgedId, 0, false, "пересмотр");
        late.Code.Should().Be(WikiPackageStore.CodeNotDecidable);
        Read(judgedId).Status.Should().Be(WikiPackage.StatusPartiallyAccepted);
    }

    [Fact]
    public void ПовторноеРешениеПоРешённойСудьёйОперации_Отказ_ПакетОстаётсяВходящим()
    {
        // Пакет ещё в «Входящих» (вторая спорная не решена) — и всё равно отказ: вердикт
        // судьи решение не переворачивается и не дополняется решением человека (И1).
        var id = AtProposed([InboxOp("Общее/A.md"), InboxOp("Общее/B.md")]);
        WikiPackageStore.JudgeOperations(_root, id, [new WikiPackageStore.JudgeOutcome(0, true, null, JudgeModel, null)])
            .Ok.Should().BeTrue();
        Read(id).Status.Should().Be(WikiPackage.StatusProposed, "одна спорная ещё не решена");

        var again = WikiPackageStore.DecideOperation(_root, id, 0, false, "пересмотр");
        again.Code.Should().Be(WikiPackageStore.CodeAlreadyDecided);

        var after = Read(id);
        after.Operations[0].JudgeDecision.Should().Be(WikiPackage.HumanAccepted);
        after.Operations[0].HumanDecision.Should().BeNull();
        after.Status.Should().Be(WikiPackage.StatusProposed);
    }

    [Fact]
    public void ВердиктаНет_ТолькоСчётчикПопыток_ПакетОстаётсяВходящим()
    {
        var id = AtProposed(InboxOp("Общее/C.md"));
        WikiPackageStore.JudgeOperations(_root, id, [new WikiPackageStore.JudgeOutcome(0, null, null, JudgeModel, null)])
            .Ok.Should().BeTrue();

        var after = Read(id);
        after.Operations[0].JudgeAttempts.Should().Be(1);
        after.Operations[0].JudgeDecision.Should().BeNull();
        after.Operations[0].JudgeReason.Should().BeNull();
        after.Operations[0].JudgeAt.Should().BeNull();
        after.Status.Should().Be(WikiPackage.StatusProposed, "без вердикта пакет не решён");

        // Отказ без причины — не вердикт: форма отказа без причины не допускает (та же мера,
        // что CodeReasonRequired у человека). Такая попытка остаётся попыткой.
        WikiPackageStore.JudgeOperations(_root, id, [new WikiPackageStore.JudgeOutcome(0, false, "   ", JudgeModel, null)])
            .Ok.Should().BeTrue();
        after = Read(id);
        after.Operations[0].JudgeAttempts.Should().Be(2);
        after.Operations[0].JudgeDecision.Should().BeNull();
    }

    [Fact]
    public void AcceptAllПропускаетОперацииРешённыеСудьёй()
    {
        var id = AtProposed([InboxOp("Общее/A.md"), InboxOp("Общее/B.md")]);
        WikiPackageStore.JudgeOperations(_root, id, [new WikiPackageStore.JudgeOutcome(0, false, "нет цитаты", JudgeModel, null)])
            .Ok.Should().BeTrue();

        WikiPackageStore.AcceptAllContested(_root, id).Ok.Should().BeTrue();

        var pkg = Read(id);
        pkg.Operations[0].JudgeDecision.Should().Be(WikiPackage.HumanRejected, "вердикт судьи остаётся: у операции одно решение");
        pkg.Operations[0].HumanDecision.Should().BeNull();
        pkg.Operations[1].HumanDecision.Should().Be(WikiPackage.HumanAccepted);
        pkg.Status.Should().Be(WikiPackage.StatusPartiallyAccepted);
    }

    [Fact]
    public void УстаревшийОтветСудьи_ПропускаетсяМолча_ПакетВнеProposed_ОтказКодом()
    {
        // Пакет ушёл в stale за время облачного вызова: отказа нет, вердикта нет.
        var staleId = At(WikiPackage.StatusStale, InboxOp("Общее/A.md"));
        var stale = WikiPackageStore.JudgeOperations(_root, staleId, [new WikiPackageStore.JudgeOutcome(0, true, null, JudgeModel, null)]);
        stale.Ok.Should().BeFalse();
        stale.Code.Should().Be(WikiPackageStore.CodeNotDecidable);
        Read(staleId).Operations[0].JudgeDecision.Should().BeNull();
        Read(staleId).Operations[0].JudgeAttempts.Should().Be(0);

        // Человек уже решил эту операцию, пакет ещё «Входящий» по другой спорной: пропуск
        // без исключения, чужое решение цело, попыток не добавилось.
        var decidedId = AtProposed([InboxOp("Общее/B.md"), InboxOp("Общее/D.md")]);
        WikiPackageStore.DecideOperation(_root, decidedId, 0, true).Ok.Should().BeTrue();
        var late = WikiPackageStore.JudgeOperations(_root, decidedId, [new WikiPackageStore.JudgeOutcome(0, false, "поздно", JudgeModel, null)]);
        late.Ok.Should().BeTrue("устаревший ответ — не отказ: за время вызова человек мог решить что угодно");
        var after = Read(decidedId);
        after.Operations[0].JudgeDecision.Should().BeNull();
        after.Operations[0].JudgeAttempts.Should().Be(0);
        after.Operations[0].HumanDecision.Should().Be(WikiPackage.HumanAccepted);
        after.Operations[1].JudgeDecision.Should().BeNull();
        after.Status.Should().Be(WikiPackage.StatusProposed);

        // Неспорная операция и несуществующий индекс — тоже молча, файл не трогается.
        var autoId = AtProposed(AutoOp("Общее/C.md"));
        var before = File.ReadAllText(PackageFile(autoId));
        WikiPackageStore.JudgeOperations(_root, autoId,
            [new WikiPackageStore.JudgeOutcome(0, true, null, JudgeModel, null), new WikiPackageStore.JudgeOutcome(7, true, null, JudgeModel, null)])
            .Ok.Should().BeTrue();
        File.ReadAllText(PackageFile(autoId)).Should().Be(before, "нечего решать — нечего и писать");
    }

    // ─── c3: вердикт судьи не входит в статистику класса ────────────────────

    [Fact]
    public async Task РешённыеСудьёйОперацииНеМеняютСтатистикуКласса()
    {
        // Класс оценивается по тому, что применилось по решению человека (WasApplied):
        // пять judge-accepted операций класса не должны двигать ниApplied, ни долю откатов.
        // Сравнение — с владельцем без этих операций при одинаковой базе.
        foreach (var owner in new[] { User, Other })
            for (var i = 0; i < 10; i++)
                ApplyThrough(WikiPaths.Root(_dataDir, owner), AutoOp($"Общее/B{i:D2}.md"));

        var root = WikiPaths.Root(_dataDir, User);
        for (var i = 0; i < 5; i++)
        {
            var id = AtProposed(InboxOp($"Общее/J{i:D2}.md"), root);
            WikiPackageStore.JudgeOperations(root, id, [new WikiPackageStore.JudgeOutcome(0, true, null, JudgeModel, 0.001m)])
                .Ok.Should().BeTrue();
            Close(id, root);
        }

        var mine = (await _health.EvaluateAsync(User)).Stats.Single(s => s.Class == ClassKey);
        var control = (await _health.EvaluateAsync(Other)).Stats.Single(s => s.Class == ClassKey);

        mine.Applied7.Should().Be(control.Applied7, "операции, решённые судьёй, не «применились»: их нет в окне класса");
        mine.Applied30.Should().Be(control.Applied30);
        mine.Retracted7.Should().Be(control.Retracted7);
        mine.Retracted30.Should().Be(control.Retracted30);
        mine.Share7.Should().Be(control.Share7);
        mine.Share30.Should().Be(control.Share30);
        mine.Disabled.Should().Be(control.Disabled);
        mine.Should().Be(control, "вся строка класса идентична контрольной");

        // На диске пять judge-accepted применённых операций класса есть — пустой результат
        // был бы тривиальным: краснеет, если WasApplied начнёт считать и вердикт судьи.
        WikiPackageStore.List(root).Packages.Count(p => p.Status == WikiPackage.StatusApplied)
            .Should().Be(15, "10 базовых + 5 решённых судьёй");
    }

    // ─── c4: старая форма читается, новая проверяется ───────────────────────

    [Fact]
    public void СтарыйФайлБезПолейСудьи_Читается_ИПроходитФорму()
    {
        // Файл прежней формы (до J-J2a): ни одного judge*-поля.
        var id = WikiPackageStore.NewId();
        WriteRawFile(id, $$"""
        {
          "v":1,"id":"{{id}}","author":"ai","section":"common",
          "sources":[{"origin":"session","ref":"chat-1"}],
          "operations":[{
            "op":"append","page":"Общее/X.md","text":"утверждение из чата",
            "class":"session×append","decision":"inbox","reason":"замещает действующее утверждение",
            "baseHashes":{"Общее/X.md":"{{Hash}}"},"inputTokens":120,"outputTokens":40
          }],
          "status":"proposed","createdAt":"2026-11-01T10:00:00+00:00","updatedAt":"2026-11-01T10:00:00+00:00"
        }
        """);

        var read = WikiPackageStore.TryRead(_root, id);
        read.State.Should().Be(WikiPackageFileState.Valid, "отсутствующие поля — null и 0, а не битый файл");
        read.Package!.Operations[0].JudgeDecision.Should().BeNull();
        read.Package.Operations[0].JudgeAttempts.Should().Be(0);
        WikiPackage.ShapeErrors(read.Package).Should().BeEmpty();
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("auto")] // решение политики — не решение судьи
    [InlineData("")]
    public void РешениеСудьиВнеПеречня_ОшибкаФормы(string value)
    {
        var op = InboxOp("Общее/X.md") with { JudgeDecision = value, JudgeReason = "есть объяснение" };
        WikiPackage.ShapeErrors(Pkg(op)).Should().ContainSingle()
            .Which.Should().Contain("решение судьи");
    }

    [Fact]
    public void ОтказСудьиБезПричины_ОшибкаФормы_ПринятиеБезПричины_Нет()
    {
        // Принять без объяснения можно (та же мера, что у человека), отказать — нет.
        WikiPackage.ShapeErrors(Pkg(InboxOp("Общее/X.md") with { JudgeDecision = WikiPackage.HumanRejected })).Should()
            .ContainSingle().Which.Should().Contain("отказ судьи без причины");
        WikiPackage.ShapeErrors(Pkg(InboxOp("Общее/X.md") with
        {
            JudgeDecision = WikiPackage.HumanAccepted,
            JudgeModel = JudgeModel,
            JudgeAt = DateTimeOffset.UtcNow,
            JudgeCostUsd = 0.001m,
        })).Should().BeEmpty();
        // Метка «отклонено кодом, без модели» — валидное значение JudgeModel.
        WikiPackage.ShapeErrors(Pkg(InboxOp("Общее/X.md") with
        {
            JudgeDecision = WikiPackage.HumanRejected,
            JudgeReason = "отказ по коду проверки",
            JudgeModel = WikiPackage.JudgeModelCode,
        })).Should().BeEmpty();
    }

    // ─── Помощники ─────────────────────────────────────────────────────────

    // Пакет в «checking» — как его принимает Create; статус в проверке формы не участвует
    // (оба значения из перечня), поэтому тот же помощник годится и для ShapeErrors.
    private static WikiPackage Pkg(params WikiPackage.Operation[] ops) => new()
    {
        Id = WikiPackageStore.NewId(),
        Section = "common",
        Sources = [new WikiPackage.SourceRef("session", "chat-1")],
        Operations = ops,
        Status = WikiPackage.StatusChecking,
    };

    private static WikiPackage.Operation AutoOp(string page = "Общее/X.md") => Op(page, WikiPackage.DecisionAuto);

    private static WikiPackage.Operation InboxOp(string page = "Общее/X.md") => Op(page, WikiPackage.DecisionInbox);

    private static WikiPackage.Operation Op(string page, string decision) => new()
    {
        Op = WikiPackage.OpAppend,
        Page = page,
        Text = "утверждение из чата",
        Class = ClassKey,
        Decision = decision,
        Reason = decision == WikiPackage.DecisionInbox ? "замещает действующее утверждение" : null,
        BaseHashes = new Dictionary<string, string> { [page] = Hash },
        InputTokens = 120,
        OutputTokens = 40,
    };

    private string PackageFile(string id) => Path.Combine(_root, "keeper", "packages", id + ".json");

    // Файл прежней формы пишется мимо стора: каталог при этом не создаётся никем, поэтому
    // создаётся здесь — тест читает ровно то, что мог оставить бэкап.
    private void WriteRawFile(string id, string json)
    {
        var full = PackageFile(id);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, json, new UTF8Encoding(false));
    }

    private WikiPackage Read(string id)
    {
        var res = WikiPackageStore.TryRead(_root, id);
        res.State.Should().Be(WikiPackageFileState.Valid);
        return res.Package!;
    }

    /// <summary>Пакет в «Входящих»: рождается в checking и доводится до proposed разрешённым
    /// переходом — тест работает на живом файле стора, а не на воображаемом состоянии.</summary>
    private string AtProposed(WikiPackage.Operation op, string? root = null) => AtProposed([op], root);

    private string AtProposed(IReadOnlyList<WikiPackage.Operation> ops, string? root = null)
    {
        var where = root ?? _root;
        var pkg = Pkg(ops.ToArray());
        WikiPackageStore.Create(where, pkg).Ok.Should().BeTrue();
        WikiPackageStore.Transition(where, pkg.Id, WikiPackage.StatusProposed).Ok.Should().BeTrue();
        return pkg.Id;
    }

    // Довести пакет из «Входящих» дальше по таблице переходов: сам «proposed» уже сделан
    // AtProposed, поэтому здесь — только шаги ПОСЛЕ него.
    private string At(string status, WikiPackage.Operation op)
    {
        var id = AtProposed(op);
        foreach (var step in AfterProposed(status))
            WikiPackageStore.Transition(_root, id, step).Ok.Should().BeTrue($"{status}: {step}");
        return id;
    }

    private static IReadOnlyList<string> AfterProposed(string status) => status switch
    {
        WikiPackage.StatusProposed => [],
        WikiPackage.StatusStale => [WikiPackage.StatusStale],
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>Довести пакет до применённого решениями судьи: accepted → applying → applied.
    /// Пакет закрывается — открытые держат лимит 20 на всю вики.</summary>
    private void Close(string id, string? root = null)
    {
        var where = root ?? _root;
        WikiPackageStore.Transition(where, id, WikiPackage.StatusApplying).Ok.Should().BeTrue();
        WikiPackageStore.Transition(where, id, WikiPackage.StatusApplied).Ok.Should().BeTrue();
    }

    /// <summary>Применённый пакет с авто-операцией: тот, что читает оценка классов.</summary>
    private void ApplyThrough(string root, WikiPackage.Operation op)
    {
        var id = AtProposed(op, root);
        Close(id, root);
    }

    private sealed class FlagGate : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => true;
    }
}
