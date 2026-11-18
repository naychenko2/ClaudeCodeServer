using System.Text;
using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// S4a (ADR-035, решения 4 и 12г): пакет — намерения в пределах одного раздела; хранится
// файлом keeper/packages/{id}.json; статусы живут только по таблице переходов; открытый
// пакет занимает лимит 20 на всю вики; битый файл — «невалиден», не исключение и не пустой
// пакет; человек решает только операции с решением политики `inbox`.
public class WikiPackageStoreTests : IDisposable
{
    // Форма хэша та же, что в манифесте: 64 hex нижнего регистра.
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Page = "Общее/X.md";

    private readonly string _root;

    public WikiPackageStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wiki_pkg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ─── 1. Создание и чтение ──────────────────────────────────────────────

    [Fact]
    public void Создание_РождаетсяВChecking_ФайлПодИд_ЧитаетсяВалидным()
    {
        var pkg = Pkg(AppendOp());
        var res = WikiPackageStore.Create(_root, pkg);
        res.Ok.Should().BeTrue(res.Reason);
        res.Package!.Status.Should().Be(WikiPackage.StatusChecking);
        res.Package.CreatedAt.Should().NotBe(default);

        var file = PackageFile(pkg.Id);
        File.Exists(file).Should().BeTrue();
        // Класс и русский текст остаются читаемыми в файле (их читает человек в «Входящих»).
        File.ReadAllText(file, new UTF8Encoding(false)).Should().Contain("session×append");

        var read = WikiPackageStore.TryRead(_root, pkg.Id);
        read.State.Should().Be(WikiPackageFileState.Valid);
        read.Package!.Operations[0].Class.Should().Be("session×append");
        read.Package.Operations[0].BaseHashes[Page].Should().Be(Hash);
    }

    [Fact]
    public void НовыйПакет_НеВChecking_ИЧужойId_Отказ()
    {
        // Пакет не может «родиться» уже принятым: начальное состояние держит код.
        var late = Pkg(AppendOp()) with { Status = WikiPackage.StatusAccepted };
        var res = WikiPackageStore.Create(_root, late);
        res.Ok.Should().BeFalse();
        res.Code.Should().Be(WikiPackageStore.CodeShape);

        // Id не GUID — не имя файла: обхода пути нет даже с «/../» в id.
        var traversal = Pkg(AppendOp()) with { Id = "../outside" };
        WikiPackageStore.Create(_root, traversal).Code.Should().Be(WikiPackageStore.CodeShape);
        File.Exists(Path.Combine(_root, "outside.json")).Should().BeFalse();
    }

    // ─── 2. Переходы статусов ──────────────────────────────────────────────

    [Theory]
    // Вся таблица допустимых переходов (ADR-035, решение 4).
    [InlineData(WikiPackage.StatusChecking, WikiPackage.StatusProposed)]
    [InlineData(WikiPackage.StatusChecking, WikiPackage.StatusRejectedByChecks)]
    [InlineData(WikiPackage.StatusChecking, WikiPackage.StatusStale)] // заменён новым разбором окна (дефект 03a50f53)
    [InlineData(WikiPackage.StatusProposed, WikiPackage.StatusAccepted)]
    [InlineData(WikiPackage.StatusProposed, WikiPackage.StatusPartiallyAccepted)]
    [InlineData(WikiPackage.StatusProposed, WikiPackage.StatusRejected)]
    [InlineData(WikiPackage.StatusProposed, WikiPackage.StatusApplying)] // неспорные применяет код сам
    [InlineData(WikiPackage.StatusProposed, WikiPackage.StatusStale)]
    [InlineData(WikiPackage.StatusAccepted, WikiPackage.StatusApplying)]
    [InlineData(WikiPackage.StatusAccepted, WikiPackage.StatusStale)]
    [InlineData(WikiPackage.StatusPartiallyAccepted, WikiPackage.StatusApplying)]
    [InlineData(WikiPackage.StatusPartiallyAccepted, WikiPackage.StatusStale)]
    [InlineData(WikiPackage.StatusApplying, WikiPackage.StatusApplied)]
    [InlineData(WikiPackage.StatusApplying, WikiPackage.StatusApplyFailed)]
    [InlineData(WikiPackage.StatusApplying, WikiPackage.StatusStale)]
    public void ДопустимыйПереход_Проходит_ИСостояниеНаДиске(string from, string to)
    {
        var id = At(from);
        var res = WikiPackageStore.Transition(_root, id, to, "проверено");
        res.Ok.Should().BeTrue(res.Reason);
        res.Package!.Status.Should().Be(to);
        res.Package.StatusReason.Should().Be("проверено");
        ReadStatus(id).Should().Be(to, "переход живёт на диске, а не в возврате");
    }

    [Theory]
    // Недопустимые: назад по конвейеру, через фазу, из замкнутого состояния.
    [InlineData(WikiPackage.StatusChecking, WikiPackage.StatusAccepted)]
    [InlineData(WikiPackage.StatusChecking, WikiPackage.StatusApplying)]
    [InlineData(WikiPackage.StatusChecking, WikiPackage.StatusApplied)]
    [InlineData(WikiPackage.StatusProposed, WikiPackage.StatusChecking)]
    [InlineData(WikiPackage.StatusProposed, WikiPackage.StatusApplied)]
    [InlineData(WikiPackage.StatusAccepted, WikiPackage.StatusProposed)]
    [InlineData(WikiPackage.StatusApplying, WikiPackage.StatusProposed)]
    [InlineData(WikiPackage.StatusApplied, WikiPackage.StatusApplying)]
    [InlineData(WikiPackage.StatusApplied, WikiPackage.StatusStale)]
    [InlineData(WikiPackage.StatusRejected, WikiPackage.StatusApplying)]
    [InlineData(WikiPackage.StatusRejectedByChecks, WikiPackage.StatusAccepted)] // отказ кодов человек не пересматривает
    [InlineData(WikiPackage.StatusApplyFailed, WikiPackage.StatusApplying)]
    [InlineData(WikiPackage.StatusStale, WikiPackage.StatusProposed)] // stale без авто-rebase
    public void НедопустимыйПереход_Отказ_ИСтатусНеМеняется(string from, string to)
    {
        var id = At(from);
        var res = WikiPackageStore.Transition(_root, id, to);
        res.Ok.Should().BeFalse();
        res.Code.Should().Be(WikiPackageStore.CodeTransition);
        ReadStatus(id).Should().Be(from);

        // Пакета нет — отдельный исход для контроллера (404), а не «невалиден».
        WikiPackageStore.Transition(_root, WikiPackageStore.NewId(), to).Code.Should().Be(WikiPackageStore.CodeNotFound);
    }

    // ─── 3. Лимит открытых пакетов на всю вики ─────────────────────────────

    [Fact]
    public void ДвадцатьПервыйОткрытый_Отказ_ЗакрытыйОсвобождаетМесто()
    {
        var ids = new List<string>();
        for (var i = 0; i < WikiPackage.MaxOpenPackages; i++)
        {
            var pkg = Pkg(AppendOp());
            WikiPackageStore.Create(_root, pkg).Ok.Should().BeTrue($"пакет {i}");
            ids.Add(pkg.Id);
        }
        WikiPackageStore.CountOpen(_root).Should().Be(WikiPackage.MaxOpenPackages);

        var over = Pkg(AppendOp());
        var res = WikiPackageStore.Create(_root, over);
        res.Ok.Should().BeFalse();
        res.Code.Should().Be(WikiPackageStore.CodeLimit);
        File.Exists(PackageFile(over.Id)).Should().BeFalse("отказанный лимитом пакет не оставляется на диске");

        // Закрыть один (отказ человека) — место свободно: лимит считает открытые, не все файлы.
        WikiPackageStore.Transition(_root, ids[0], WikiPackage.StatusProposed).Ok.Should().BeTrue();
        WikiPackageStore.Transition(_root, ids[0], WikiPackage.StatusRejected, "не нужно").Ok.Should().BeTrue();
        WikiPackageStore.Create(_root, over).Ok.Should().BeTrue();
    }

    [Fact]
    public void НечитаемыйФайл_ЗанимаетМестоВЛимите_FailClosed()
    {
        for (var i = 0; i < WikiPackage.MaxOpenPackages - 1; i++)
            WikiPackageStore.Create(_root, Pkg(AppendOp())).Ok.Should().BeTrue();
        File.WriteAllText(PackageFile(WikiPackageStore.NewId()), "{ не json");
        WikiPackageStore.CountOpen(_root).Should().Be(WikiPackage.MaxOpenPackages,
            "содержимое нечитаемого файла неизвестно — назвать его закрытым нельзя");
        WikiPackageStore.Create(_root, Pkg(AppendOp())).Code.Should().Be(WikiPackageStore.CodeLimit);
    }

    // ─── 4. Форма пакета ───────────────────────────────────────────────────

    [Fact]
    public void ОперацииВДвухРазделах_Отказ_ПакетОдногоРаздела()
    {
        var res = WikiPackageStore.Create(_root, Pkg(AppendOp(), AppendOp("Личное/Y.md")));
        res.Ok.Should().BeFalse();
        res.Code.Should().Be(WikiPackageStore.CodeShape);
        res.Reason.Should().Contain("не раздела");
    }

    [Fact]
    public void ПустойПакет_БезИсточника_БезПричиныУInbox_ЧужойКласс_Удаление_Отказ()
    {
        WikiPackageStore.Create(_root, Pkg()).Code.Should().Be(WikiPackageStore.CodeShape);
        WikiPackageStore.Create(_root, Pkg(AppendOp()) with { Sources = [] }).Code.Should().Be(WikiPackageStore.CodeShape);

        // «Входящие» приходят к человеку с причиной.
        var noReason = Pkg(AppendOp(decision: WikiPackage.DecisionInbox) with { Reason = null });
        WikiPackageStore.Create(_root, noReason).Code.Should().Be(WikiPackageStore.CodeShape);

        // Класс ставит код из источника пакета и op: своя строка невозможна.
        var foreignClass = Pkg(AppendOp() with { Class = "url×append" });
        WikiPackageStore.Create(_root, foreignClass).Code.Should().Be(WikiPackageStore.CodeShape);

        // Удаления среди операций нет (решение 4): снятие знания — откат, не операция.
        WikiPackageStore.Create(_root, Pkg(OpWith("delete"))).Code.Should().Be(WikiPackageStore.CodeShape);

        // Раздел сферы резервирован за следующей фичей (ADR-036, решение 7).
        WikiPackageStore.Create(_root, Pkg(AppendOp("Сферы/s1/Y.md"))).Ok.Should().BeFalse();
    }

    [Fact]
    public void BaseHashes_ТойЖеФормыЧтоМанифест_ЧужогоРаздела_Отказ()
    {
        var bad = Pkg(AppendOp() with { BaseHashes = new Dictionary<string, string> { [Page] = "ZZ" } });
        WikiPackageStore.Create(_root, bad).Code.Should().Be(WikiPackageStore.CodeShape);
        var other = Pkg(AppendOp() with { BaseHashes = new Dictionary<string, string> { ["Личное/Y.md"] = Hash } });
        WikiPackageStore.Create(_root, other).Code.Should().Be(WikiPackageStore.CodeShape);
    }

    // ─── 5. Битый файл: «невалиден», не исключение и не пустой пакет ───────

    [Fact]
    public void БитыйФайлПакета_СостояниеНевалиден_СписокНеПадает_ИНеПритворяетсяПакетом()
    {
        var good = Pkg(AppendOp());
        WikiPackageStore.Create(_root, good).Ok.Should().BeTrue("каталог пакетов создаётся записью");

        var brokenId = WikiPackageStore.NewId();
        File.WriteAllText(PackageFile(brokenId), "{ не json");
        var emptyId = WikiPackageStore.NewId();
        File.WriteAllText(PackageFile(emptyId), "   ");
        // Валидный JSON, но статус не из перечня: это тоже невалидный файл, а не пустой пакет.
        var badShape = Pkg(AppendOp()) with { Status = "banana" };
        WikiFileWriter.Write(_root, "keeper/packages/" + badShape.Id + ".json",
            "{\"id\":\"" + badShape.Id + "\",\"v\":1,\"section\":\"common\",\"status\":\"banana\"," +
            "\"sources\":[],\"operations\":[]}");
        var notAnId = Path.Combine(_root, "keeper", "packages", "мусор.json");
        File.WriteAllText(notAnId, "{}");

        var read = WikiPackageStore.TryRead(_root, brokenId);
        read.State.Should().Be(WikiPackageFileState.Invalid);
        read.Package.Should().BeNull("невалидный файл не притворяется пустым пакетом");

        var list = WikiPackageStore.List(_root);
        list.Packages.Should().ContainSingle(p => p.Id == good.Id);
        list.InvalidFiles.Should().BeEquivalentTo([
            brokenId + ".json", emptyId + ".json", badShape.Id + ".json", "мусор.json"]);

        // Операция над невалидным — отказ кодом, а не исключение.
        WikiPackageStore.Transition(_root, brokenId, WikiPackage.StatusProposed).Code
            .Should().Be(WikiPackageStore.CodeInvalid);
    }

    // ─── 6. Решения человека по спорным операциям ──────────────────────────

    [Fact]
    public void РешенияПоInbox_СтатусТолькоПослеВсех_СмешаноPartially_ОтказБезПричины()
    {
        var id = At(WikiPackage.StatusProposed,
            AppendOp(), // неспорная: её человек не решает
            AppendOp(decision: WikiPackage.DecisionInbox),
            AppendOp("Общее/Z.md", WikiPackage.DecisionInbox));

        // Отклонить можно только с причиной.
        WikiPackageStore.DecideOperation(_root, id, 1, false).Code
            .Should().Be(WikiPackageStore.CodeReasonRequired);
        // Неспорную операцию человек не решает (409 у контроллера).
        WikiPackageStore.DecideOperation(_root, id, 0, true).Code.Should().Be(WikiPackageStore.CodeAuto);
        // Индекс вне списка.
        WikiPackageStore.DecideOperation(_root, id, 9, true).Code.Should().Be(WikiPackageStore.CodeOpIndex);

        // Одна из двух решена — вердикта ещё нет: пакет ждёт.
        var one = WikiPackageStore.DecideOperation(_root, id, 1, true);
        one.Ok.Should().BeTrue(one.Reason);
        one.Package!.Status.Should().Be(WikiPackage.StatusProposed);
        one.Package.Operations[1].HumanDecision.Should().Be(WikiPackage.HumanAccepted);

        // Все решены, исходы разные → partially_accepted.
        var two = WikiPackageStore.DecideOperation(_root, id, 2, false, "это не противоречие, а уточнение");
        two.Ok.Should().BeTrue(two.Reason);
        two.Package!.Status.Should().Be(WikiPackage.StatusPartiallyAccepted);
        two.Package.Operations[2].HumanReason.Should().Be("это не противоречие, а уточнение");
        ReadStatus(id).Should().Be(WikiPackage.StatusPartiallyAccepted);
    }

    [Fact]
    public void ВсеОтклонены_Rejected_ПринятьВсёСпорное_Accepted_БезСпорных_ИВнеProposed_Отказ()
    {
        var id = At(WikiPackage.StatusProposed, AppendOp(decision: WikiPackage.DecisionInbox));
        WikiPackageStore.DecideOperation(_root, id, 0, false, "не подтверждено источником").Ok.Should().BeTrue();
        ReadStatus(id).Should().Be(WikiPackage.StatusRejected);

        var other = At(WikiPackage.StatusProposed,
            AppendOp(decision: WikiPackage.DecisionInbox), AppendOp("Общее/Z.md", WikiPackage.DecisionInbox));
        var all = WikiPackageStore.AcceptAllContested(_root, other);
        all.Ok.Should().BeTrue(all.Reason);
        all.Package!.Status.Should().Be(WikiPackage.StatusAccepted);
        all.Package.Operations.Where(o => o.Decision == WikiPackage.DecisionInbox)
            .All(o => o.HumanDecision == WikiPackage.HumanAccepted).Should().BeTrue();

        // Пакета без спорных операций это не касается: решать нечего.
        var noContested = At(WikiPackage.StatusProposed, AppendOp());
        WikiPackageStore.AcceptAllContested(_root, noContested).Code
            .Should().Be(WikiPackageStore.CodeNothingToDecide);

        // Решают по пакету, который ещё ждёт: accepted — дальше применяет код, не человек.
        var decided = At(WikiPackage.StatusAccepted, AppendOp(decision: WikiPackage.DecisionInbox));
        WikiPackageStore.DecideOperation(_root, decided, 0, true).Code
            .Should().Be(WikiPackageStore.CodeNotDecidable);
    }

    // ─── 6b. D0: отказ по спорным не выбрасывает авто-операции ───────────────
    //
    // Правило StatusAfterDecisions на всех трёх путях решения (человек :289, «принять всё»
    // :316, судья :384): есть нерешённая спорная → proposed; все спорные приняты → accepted;
    // ни одна спорная не принята И авто-операций нет → rejected; иначе → partially_accepted.
    // rejected больше не снимает авто-операции: при них пакет partially_accepted и их
    // применяет обычный путь применения.

    [Fact]
    public void ОтказВсеСпорныхПриАвто_PartiallyAccepted_АвтоНеТронуто_БезАвто_Rejected()
    {
        // Путь решения человека (DecideOperation). Мутация «вернуть старое условие
        // rejected» (без проверки авто-операций): первое Should краснеет — пакет стал бы
        // rejected и его авто-операции не применились бы никогда.
        var withAuto = At(WikiPackage.StatusProposed,
            AppendOp(), // неспорная: её никто не решает и отказ по спорным её не выбрасывает
            AppendOp("Общее/Z.md", WikiPackage.DecisionInbox));
        var res = WikiPackageStore.DecideOperation(_root, withAuto, 1, false, "не подтверждено источником");
        res.Ok.Should().BeTrue(res.Reason);
        res.Package!.Status.Should().Be(WikiPackage.StatusPartiallyAccepted,
            "отказ по спорным не выбрасывает авто-операции (D0)");
        res.Package.Operations[0].Decision.Should().Be(WikiPackage.DecisionAuto);
        res.Package.Operations[0].HumanDecision.Should().BeNull("авто-операцию человек не решает");
        ReadStatus(withAuto).Should().Be(WikiPackage.StatusPartiallyAccepted);

        // Граница правила: ни одной принятой спорной и авто-операций нет — прежний rejected.
        var noAuto = At(WikiPackage.StatusProposed, AppendOp("Общее/Z.md", WikiPackage.DecisionInbox));
        WikiPackageStore.DecideOperation(_root, noAuto, 0, false, "не подтверждено источником").Ok.Should().BeTrue();
        ReadStatus(noAuto).Should().Be(WikiPackage.StatusRejected);
    }

    [Fact]
    public void СудьяОтклоняетВсеСпорныеПриАвто_PartiallyAccepted_АвтоНеТронуто()
    {
        // Путь судьи (JudgeOperations): вердикт ведёт статус ровно так же, как решение
        // человека, — и по тому же новому правилу. Мутация старого условия краснеет здесь же.
        var id = At(WikiPackage.StatusProposed,
            AppendOp(), AppendOp("Общее/Z.md", WikiPackage.DecisionInbox));
        var res = WikiPackageStore.JudgeOperations(_root, id,
            [new WikiPackageStore.JudgeOutcome(1, false, "нет цитаты", "test-judge", null)]);
        res.Ok.Should().BeTrue(res.Reason);
        res.Package!.Status.Should().Be(WikiPackage.StatusPartiallyAccepted);
        res.Package.Operations[1].JudgeDecision.Should().Be(WikiPackage.HumanRejected);
        res.Package.Operations[0].Decision.Should().Be(WikiPackage.DecisionAuto);
        res.Package.Operations[0].HumanDecision.Should().BeNull();
        ReadStatus(id).Should().Be(WikiPackage.StatusPartiallyAccepted);
    }

    [Fact]
    public void ПринятьВсёПослеОтказаСудьи_PartiallyAccepted_ВердиктОстаётся_АвтоНеТронута()
    {
        // Путь «принять всё» (AcceptAllContested): вердикт судьи по одной спорной остаётся
        // (И1), нерешённые принимаются, итог — partially_accepted по общему правилу;
        // авто-операция не получает никакого решения.
        var id = At(WikiPackage.StatusProposed,
            AppendOp(),
            AppendOp("Общее/A.md", WikiPackage.DecisionInbox),
            AppendOp("Общее/Z.md", WikiPackage.DecisionInbox));
        WikiPackageStore.JudgeOperations(_root, id,
            [new WikiPackageStore.JudgeOutcome(1, false, "нет цитаты", "test-judge", null)]).Ok.Should().BeTrue();
        ReadStatus(id).Should().Be(WikiPackage.StatusProposed, "вторая спорная ещё не решена");

        var res = WikiPackageStore.AcceptAllContested(_root, id);
        res.Ok.Should().BeTrue(res.Reason);
        var pkg = res.Package!;
        pkg.Operations[1].JudgeDecision.Should().Be(WikiPackage.HumanRejected, "вердикт судьи остаётся: у операции одно решение");
        pkg.Operations[1].HumanDecision.Should().BeNull();
        pkg.Operations[2].HumanDecision.Should().Be(WikiPackage.HumanAccepted);
        pkg.Operations[0].Decision.Should().Be(WikiPackage.DecisionAuto);
        pkg.Operations[0].HumanDecision.Should().BeNull();
        pkg.Status.Should().Be(WikiPackage.StatusPartiallyAccepted);
        ReadStatus(id).Should().Be(WikiPackage.StatusPartiallyAccepted);
    }

    // ─── 7. Гонка записи: чтение → пересчёт → перезапись атомарны (MAJOR 1, ревью S4a) ───

    [Fact]
    public async Task ДваПараллельныхРешенияНаОдномПакете_ОбаНаМесте_СтатусОдин()
    {
        // Без блокировки оба вызова читают одно базовое состояние и каждый пишет свой вариант
        // файла: решение человека молча теряется. Оба стартаются от одного барьера (без
        // Task.Delay); пакет большой — чтение и перезапись занимают миллисекунды, и два
        // вызова пересекаются не случайно.
        var ops = new List<WikiPackage.Operation>
        {
            AppendOp(decision: WikiPackage.DecisionInbox),
            AppendOp("Общее/Z.md", WikiPackage.DecisionInbox),
        };
        for (var i = 0; i < 600; i++) ops.Add(AppendOp($"Общее/A{i:D3}.md"));
        var id = At(WikiPackage.StatusProposed, ops.ToArray());

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () => { await barrier.Task; return WikiPackageStore.DecideOperation(_root, id, 0, true); });
        var second = Task.Run(async () => { await barrier.Task; return WikiPackageStore.DecideOperation(_root, id, 1, false, "это уточнение, а не противоречие"); });
        barrier.SetResult();

        var r1 = await first.WaitAsync(TimeSpan.FromSeconds(30));
        var r2 = await second.WaitAsync(TimeSpan.FromSeconds(30));
        r1.Ok.Should().BeTrue(r1.Reason);
        r2.Ok.Should().BeTrue(r2.Reason);

        // Оба решения на диске и статус один: второе принято по первому, а не по тому же
        // базовому чтению.
        var disk = WikiPackageStore.TryRead(_root, id);
        disk.State.Should().Be(WikiPackageFileState.Valid);
        disk.Package!.Operations[0].HumanDecision.Should().Be(WikiPackage.HumanAccepted, "первое решение не затёрто вторым");
        disk.Package.Operations[1].HumanDecision.Should().Be(WikiPackage.HumanRejected);
        disk.Package.Operations[1].HumanReason.Should().Be("это уточнение, а не противоречие");
        disk.Package.Status.Should().Be(WikiPackage.StatusPartiallyAccepted);
    }

    [Fact]
    public async Task СудьяИЧеловекРешаютРазныеОперацииОдновременно_СтатусПоОбимРешениям()
    {
        // Мутация «снять замок стора» (WithPackageLock в DecideOperation/JudgeOperations):
        // последний писатель кладёт файл по своему чтению — одно из решений теряется,
        // остаётся нерешённая операция, статус proposed и краснеют последние Should.
        // Пакет большой (600 операций), как в тесте двух человеческих решений: окно гонки —
        // миллисекунды чтения и перезаписи, пересечение двух путей не случайно.
        var ops = new List<WikiPackage.Operation>
        {
            AppendOp("Общее/A.md", WikiPackage.DecisionInbox),
            AppendOp("Общее/Z.md", WikiPackage.DecisionInbox),
            AppendOp(), // авто: ни одно из решений её не касается
        };
        for (var i = 0; i < 600; i++) ops.Add(AppendOp($"Общее/A{i:D3}.md"));
        var id = At(WikiPackage.StatusProposed, ops.ToArray());

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var human = Task.Run(async () => { await barrier.Task; return WikiPackageStore.DecideOperation(_root, id, 0, true); });
        var judge = Task.Run(async () =>
        {
            await barrier.Task;
            return WikiPackageStore.JudgeOperations(_root, id,
                [new WikiPackageStore.JudgeOutcome(1, false, "нет цитаты", "test-judge", null)]);
        });
        barrier.SetResult();

        var r1 = await human.WaitAsync(TimeSpan.FromSeconds(30));
        var r2 = await judge.WaitAsync(TimeSpan.FromSeconds(30));
        r1.Ok.Should().BeTrue(r1.Reason);
        r2.Ok.Should().BeTrue(r2.Reason);

        var disk = WikiPackageStore.TryRead(_root, id);
        disk.State.Should().Be(WikiPackageFileState.Valid);
        disk.Package!.Operations[0].HumanDecision.Should().Be(WikiPackage.HumanAccepted, "решение человека не затёрто вердиктом");
        disk.Package.Operations[1].JudgeDecision.Should().Be(WikiPackage.HumanRejected, "вердикт судьи не затёрт решением человека");
        disk.Package.Operations[2].HumanDecision.Should().BeNull();
        disk.Package.Status.Should().Be(WikiPackage.StatusPartiallyAccepted, "итог учитывает оба решения");
    }

    [Fact]
    public async Task ДваПараллельныхСозданияПри19Открытых_РовноОдноУспешно_ОткрытыхРовно20()
    {
        // TOCTOU без блокировки: обе постановки считают 19 открытых до записи друг друга и
        // обе создают — 21, лимит 20 на всю вики пробит. Счёт и запись — в одной критической
        // секции: второй Create видит уже 20 и отказывает. Окно гонки — время CountOpen по
        // 19 открытым, поэтому каждый заполнен 600 операциями (как в тесте параллельных
        // решений): их чтение и пересчёт занимают десятки миллисекунд, и два Create,
        // стартовавшие от барьера, пересекаются не случайно (с маленькими пакетами окно —
        // микросекунды, и тест оставался зелёным даже без create-блокировки).
        for (var i = 0; i < WikiPackage.MaxOpenPackages - 1; i++)
        {
            var ops = new List<WikiPackage.Operation>(600);
            for (var k = 0; k < 600; k++) ops.Add(AppendOp($"Общее/A{k:D3}.md"));
            var open = Pkg(ops.ToArray());
            WikiPackageStore.Create(_root, open).Ok.Should().BeTrue($"пакет {i}");
        }

        var a = Pkg(AppendOp());
        var b = Pkg(AppendOp());
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () => { await barrier.Task; return WikiPackageStore.Create(_root, a); });
        var second = Task.Run(async () => { await barrier.Task; return WikiPackageStore.Create(_root, b); });
        barrier.SetResult();

        var r1 = await first.WaitAsync(TimeSpan.FromSeconds(30));
        var r2 = await second.WaitAsync(TimeSpan.FromSeconds(30));
        new[] { r1.Ok, r2.Ok }.Count(x => x).Should().Be(1, "ровно одна из двух параллельных постановок умещается в лимит");

        var loserId = r1.Ok ? b.Id : a.Id;
        (r1.Ok ? r2 : r1).Code.Should().Be(WikiPackageStore.CodeLimit);
        File.Exists(PackageFile(loserId)).Should().BeFalse("отказанный лимитом пакет не оставляется на диске");
        WikiPackageStore.CountOpen(_root).Should().Be(WikiPackage.MaxOpenPackages);
    }

    // ─── 8. Null-поля валидного JSON: «невалиден», не исключение (MAJOR 2, ревью S4a) ───

    [Fact]
    public void NullПолеВВалидномJson_СостояниеНевалиден_СписокЖивой_ИПоказываетНечитаемые()
    {
        // Файл из пробы ревью (стек: NullReferenceException в WikiSections.IsWritable) и ещё
        // три null-варианта: бэкап или ручная правка не должны ронять «Входящие» целиком —
        // это нечитаемый файл, тот же класс, что битый JSON.
        var good = Pkg(AppendOp());
        WikiPackageStore.Create(_root, good).Ok.Should().BeTrue("каталог пакетов создаётся записью");

        var nullSection = WikiPackageStore.NewId();
        WriteRawFile(nullSection, "{\"v\":1,\"id\":\"" + nullSection + "\",\"section\":null,\"status\":\"checking\",\"sources\":[],\"operations\":[]}");
        var nullSources = WikiPackageStore.NewId();
        WriteRawFile(nullSources, "{\"v\":1,\"id\":\"" + nullSources + "\",\"section\":\"common\",\"status\":\"checking\",\"sources\":null,\"operations\":[]}");
        var nullOperations = WikiPackageStore.NewId();
        WriteRawFile(nullOperations, "{\"v\":1,\"id\":\"" + nullOperations + "\",\"section\":\"common\",\"status\":\"checking\",\"sources\":[{\"origin\":\"session\",\"ref\":\"chat-1\"}],\"operations\":null}");
        var nullBaseHashes = WikiPackageStore.NewId();
        WriteRawFile(nullBaseHashes, "{\"v\":1,\"id\":\"" + nullBaseHashes + "\",\"section\":\"common\",\"status\":\"checking\",\"sources\":[{\"origin\":\"session\",\"ref\":\"chat-1\"}],\"operations\":[{\"op\":\"append\",\"page\":\"Общее/X.md\",\"text\":\"утверждение\",\"class\":\"session×append\",\"decision\":\"auto\",\"baseHashes\":null}]}");

        foreach (var bad in new[] { nullSection, nullSources, nullOperations, nullBaseHashes })
        {
            var read = WikiPackageStore.TryRead(_root, bad);
            read.State.Should().Be(WikiPackageFileState.Invalid, $"файл с null-полем нечитаем, а не пакет: {bad}");
            read.Package.Should().BeNull();
        }

        var list = WikiPackageStore.List(_root);
        list.Packages.Should().ContainSingle(p => p.Id == good.Id);
        list.InvalidFiles.Should().Contain(new[]
        {
            nullSection + ".json", nullSources + ".json", nullOperations + ".json", nullBaseHashes + ".json",
        });

        // Операция над таким файлом — отказ кодом, а не исключение (Get у контроллера: 409).
        WikiPackageStore.Transition(_root, nullSection, WikiPackage.StatusProposed).Code
            .Should().Be(WikiPackageStore.CodeInvalid);
    }

    // ─── B1 (ADR-038): автор пакета ──────────────────────────────────────────

    [Fact]
    public void Автор_СтарыйJsonБезAuthor_ЧитаетсяКакAi_ИНовыйПишетсяЯвно()
    {
        var fresh = Pkg(AppendOp());
        WikiPackageStore.Create(_root, fresh).Ok.Should().BeTrue("каталог пакетов создаётся записью");
        File.ReadAllText(PackageFile(fresh.Id), new UTF8Encoding(false)).Should().Contain("\"author\":\"ai\"");

        var id = WikiPackageStore.NewId();
        WriteRawFile(id, "{\"v\":1,\"id\":\"" + id + "\",\"section\":\"common\",\"status\":\"checking\",\"sources\":[{\"origin\":\"session\",\"ref\":\"chat-1\"}],\"operations\":[{\"op\":\"append\",\"page\":\"Общее/X.md\",\"text\":\"утверждение\",\"class\":\"session×append\",\"decision\":\"auto\",\"baseHashes\":{}}]}");
        var read = WikiPackageStore.TryRead(_root, id);
        read.State.Should().Be(WikiPackageFileState.Valid);
        read.Package!.Author.Should().Be(WikiPackage.AuthorAi);
    }

    [Fact]
    public void Автор_НеизвестныйИNull_ФормаОтбита()
    {
        WikiPackage.ShapeErrors(Pkg(AppendOp()) with { Author = "robot" }).Should().Contain(e => e.Contains("автор пакета"));
        WikiPackage.ShapeErrors(Pkg(AppendOp()) with { Author = null! }).Should().Contain(e => e.Contains("автор пакета"));
    }

    [Fact]
    public void Автор_ИсточникHumanУПакетаAi_ФормаПроходит_СоздатьКарточкуОтбиваетГейт()
    {
        // ADR-038, §2: ai может сослаться на уже существующую карточку human — форма пакета
        // это пропускает. Мутация «вернуть запрет формы» краснит первую проверку; мутация
        // «снять правило гейта» — вторую (она же в WikiGateTests).
        var humanSrc = new[] { new WikiPackage.SourceRef("human", "human:1") };
        var ai = Pkg(AppendOp() with { Class = "human×append" }) with { Sources = humanSrc };
        WikiPackage.ShapeErrors(ai).Should().BeEmpty();

        var card = new WikiGate.Draft("Проекты/p1/Источники/Моя.md",
            "---\ntype: source\norigin: human\nref: human:1\nhash: 0123456789012345678901234567890123456789012345678901234567890123\n" +
            "captured: 2026-11-02\naliases: []\nowns: []\nsources: []\nstatus: active\n---\n\n# Моя\n");
        WikiGate.Check([card], "projects/p1", [], null, WikiGate.VerbatimScope.All, WikiPackage.AuthorAi, baseState: [])
            .Should().Contain(e => e.Contains("origin: human") && e.Contains("автора human"));
        WikiGate.Check([card], "projects/p1", [], null, WikiGate.VerbatimScope.All, WikiPackage.AuthorAi, baseState: [card])
            .Should().NotContain(e => e.Contains("origin: human"));
    }

    // ─── 9. Повторное решение и дата создания (миноры 1 и 6, ревью S4a) ───

    [Fact]
    public void РешениеПоУжеРешённойОперации_Отказ_РешениеИПричинаНеМеняются()
    {
        // Решение не меняется и HumanReason не стирается; повтор того же решения — тоже
        // отказ: двойного эффекта нет. Отказан только уже решённый индекс, остальные решаются.
        var id = At(WikiPackage.StatusProposed,
            AppendOp(decision: WikiPackage.DecisionInbox), AppendOp("Общее/Z.md", WikiPackage.DecisionInbox));

        var first = WikiPackageStore.DecideOperation(_root, id, 0, false, "противоречит действующему");
        first.Ok.Should().BeTrue(first.Reason);
        first.Package!.Status.Should().Be(WikiPackage.StatusProposed, "вторая спорная ещё не решена");

        var repeat = WikiPackageStore.DecideOperation(_root, id, 0, true);
        repeat.Ok.Should().BeFalse();
        repeat.Code.Should().Be(WikiPackageStore.CodeAlreadyDecided);

        var disk = WikiPackageStore.TryRead(_root, id).Package!;
        disk.Operations[0].HumanDecision.Should().Be(WikiPackage.HumanRejected);
        disk.Operations[0].HumanReason.Should().Be("противоречит действующему");

        var second = WikiPackageStore.DecideOperation(_root, id, 1, true);
        second.Ok.Should().BeTrue(second.Reason);
        ReadStatus(id).Should().Be(WikiPackage.StatusPartiallyAccepted);
    }

    [Fact]
    public void ДатаСозданияСтавитСервер_ЗначениеИзВходаИгнорируется()
    {
        // Пакет не может быть создан с датой вызывающего: дата из будущего вечно сортировала
        // бы его первым в списке «Входящих».
        var future = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var start = DateTimeOffset.UtcNow;
        var res = WikiPackageStore.Create(_root, Pkg(AppendOp()) with { CreatedAt = future });
        res.Ok.Should().BeTrue(res.Reason);

        res.Package!.CreatedAt.Should().NotBe(future);
        res.Package.CreatedAt.Should().BeOnOrAfter(start).And.BeBefore(DateTimeOffset.UtcNow);

        var disk = WikiPackageStore.TryRead(_root, res.Package.Id).Package!;
        disk.CreatedAt.Should().NotBe(future).And.BeOnOrAfter(start);
    }

    // ─── 10. Гашение прежних попыток того же окна (дефект 03a50f53) ───

    [Fact]
    public void ТриРазбораОдногоОкна_ПрежниеНеприменённыеПогашены_НеотменённыйРовноОдин()
    {
        // Каждая попытка одного окна писала новый файл, а старые лежали рядом (QA-2: три
        // apply_failed на s1 m1-m110). Create нового пакета окна гасит прежние неприменённые
        // попытки переходом в «stale». Мутация «убрать вызов SupersedeUnappliedOfSameSources
        // из Create»: все три остаются открытыми — первая проверка краснеет.
        var first = AtWindow(WikiPackage.StatusProposed);
        var second = AtWindow(WikiPackage.StatusProposed);
        var third = AtWindow(WikiPackage.StatusProposed);

        ReadStatus(first).Should().Be(WikiPackage.StatusStale, "первая попытка заменена второй");
        WikiPackageStore.TryRead(_root, first).Package!.StatusReason
            .Should().Contain("заменён новым разбором", "причина гашения читается в карточке пакета");
        ReadStatus(second).Should().Be(WikiPackage.StatusStale, "вторая заменена третьей");
        ReadStatus(third).Should().Be(WikiPackage.StatusProposed);
        OpenWindowOf(WindowRef).Should().Equal(new[] { third }, "на окно — один неотменённый пакет");
    }

    [Fact]
    public void ПрименённыйРешённыйЧеловекомИТерминальный_НеГасятся_ДругоеОкноНеТронуто()
    {
        // Не трогать: применённый, решённый человеком (в том числе «proposed», где человек начал
        // решать), уже терминальный («apply_failed» — из него перехода нет, он остаётся историей)
        // и пакет другого окна. Мутация «расширить IsSupersedable на применённые/решённые/
        // терминальные» — соответствующие проверки краснеют; «снять фильтр оконной ссылки»
        // краснит лимитные тесты этого файла (20 открытых «chat-1» — не окно, они гашению
        // не подлежат).
        var applied = AtWindow(WikiPackage.StatusApplied);
        var decidedByHuman = AtWindow(WikiPackage.StatusProposed, AppendOp(decision: WikiPackage.DecisionInbox));
        WikiPackageStore.DecideOperation(_root, decidedByHuman, 0, true).Ok.Should().BeTrue();
        var applyFailed = AtWindow(WikiPackage.StatusApplyFailed);
        var halfDecided = AtWindow(WikiPackage.StatusProposed,
            AppendOp(decision: WikiPackage.DecisionInbox), AppendOp("Общее/Z.md", WikiPackage.DecisionInbox));
        WikiPackageStore.DecideOperation(_root, halfDecided, 0, true).Ok.Should().BeTrue();
        var otherWindow = AtWindowRef(WikiPackage.StatusProposed, WindowRef2);

        var fresh = WindowPkg();
        WikiPackageStore.Create(_root, fresh).Ok.Should().BeTrue();

        ReadStatus(applied).Should().Be(WikiPackage.StatusApplied, "применённый не гасится");
        ReadStatus(decidedByHuman).Should().Be(WikiPackage.StatusAccepted, "решённый человеком не тронут");
        ReadStatus(applyFailed).Should().Be(WikiPackage.StatusApplyFailed, "уже терминальный остаётся историей");
        ReadStatus(halfDecided).Should().Be(WikiPackage.StatusProposed, "начатый человеком не затёрт кодом");
        ReadStatus(otherWindow).Should().Be(WikiPackage.StatusProposed, "другое окно не тронуто");
        // Открытыми остаются ровно те, что гашение не тронуло: начатый человеком, решённый
        // человеком (его применение ещё впереди) и новый.
        OpenWindowOf(WindowRef).Should().BeEquivalentTo(new[] { halfDecided, decidedByHuman, fresh.Id },
            "гашение добирает только нерешённые попытки этого окна");
    }

    [Fact]
    public async Task ДваПараллельныхСозданияОдногоОкна_ОдноГаситДругое_НеотменённыйРовноОдин()
    {
        // Гашение и запись — в одной критической секции (блок корня Create): второй Create под
        // блоком видит первый пакет — тот ещё в «checking», и гасит его; поэтому «checking»
        // гасится наравне. Мутация «перенести SupersedeUnappliedOfSameSources за WithCreateLock»:
        // оба чтения происходят до обеих записей (первый пишет большой пакет — миллисекунды,
        // второй читает пустой каталог — микросекунды внутри этого времени), оба не видят друг друга —
        // открытых два, проверка краснеет. Оба старта от одного барьера (TaskCompletionSource,
        // без Task.Delay); пакеты большие — пересечение гонки не случайна (тот же приём, что у
        // параллельных решений).
        var a = BigWindowPkg();
        var b = BigWindowPkg();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () => { await barrier.Task; return WikiPackageStore.Create(_root, a); });
        var second = Task.Run(async () => { await barrier.Task; return WikiPackageStore.Create(_root, b); });
        barrier.SetResult();

        var r1 = await first.WaitAsync(TimeSpan.FromSeconds(30));
        var r2 = await second.WaitAsync(TimeSpan.FromSeconds(30));
        r1.Ok.Should().BeTrue(r1.Reason);
        r2.Ok.Should().BeTrue(r2.Reason);

        var open = OpenWindowOf(WindowRef);
        open.Should().HaveCount(1, "второе создание погасило первое: на окно — один неотменённый пакет");
        ReadStatus(open[0] == a.Id ? b.Id : a.Id).Should().Be(WikiPackage.StatusStale,
            "погашенный — тот, что был записан первым");
    }

    // ─── Помощники ─────────────────────────────────────────────────────────

    private static WikiPackage Pkg(params WikiPackage.Operation[] ops) => new()
    {
        Id = WikiPackageStore.NewId(),
        Section = "common",
        Sources = [new WikiPackage.SourceRef("session", "chat-1")],
        Operations = ops,
        Status = WikiPackage.StatusChecking,
    };

    private static WikiPackage.Operation AppendOp(
        string page = Page, string decision = WikiPackage.DecisionAuto) => OpWith(WikiPackage.OpAppend, page, decision);

    private static WikiPackage.Operation OpWith(
        string op, string page = Page, string decision = WikiPackage.DecisionAuto) => new()
        {
            Op = op,
            Page = page,
            Text = "утверждение из чата",
            Target = "прежнее утверждение",
            Class = WikiPackage.ClassKey("session", op),
            Decision = decision,
            Reason = decision == WikiPackage.DecisionInbox ? "замещает действующее утверждение" : null,
            BaseHashes = new Dictionary<string, string> { [page] = Hash },
            InputTokens = 120,
            OutputTokens = 40,
        };

    private string PackageFile(string id) => Path.Combine(_root, "keeper", "packages", id + ".json");

    // Файл пакета пишется дословно: тест проверяет чтение того, что мог оставить бэкап или
    // ручная правка, а не то, что умеет писать стора.
    private void WriteRawFile(string id, string json) => File.WriteAllText(PackageFile(id), json);

    // Довести пакет до статуса разрешёнными переходами: тест проверяет таблицу на живом
    // файле, а не на воображаемом состоянии.
    private string At(string status, params WikiPackage.Operation[] ops)
    {
        var pkg = Pkg(ops.Length == 0 ? [AppendOp()] : ops);
        WikiPackageStore.Create(_root, pkg).Ok.Should().BeTrue();
        foreach (var step in PathTo(status))
            WikiPackageStore.Transition(_root, pkg.Id, step).Ok.Should().BeTrue($"{status}: {step}");
        return pkg.Id;
    }

    private static IReadOnlyList<string> PathTo(string status) => status switch
    {
        WikiPackage.StatusChecking => [],
        WikiPackage.StatusProposed => [WikiPackage.StatusProposed],
        WikiPackage.StatusRejectedByChecks => [WikiPackage.StatusRejectedByChecks],
        WikiPackage.StatusAccepted => [WikiPackage.StatusProposed, WikiPackage.StatusAccepted],
        WikiPackage.StatusPartiallyAccepted => [WikiPackage.StatusProposed, WikiPackage.StatusPartiallyAccepted],
        WikiPackage.StatusRejected => [WikiPackage.StatusProposed, WikiPackage.StatusRejected],
        WikiPackage.StatusApplying => [WikiPackage.StatusProposed, WikiPackage.StatusApplying],
        WikiPackage.StatusApplied => [WikiPackage.StatusProposed, WikiPackage.StatusApplying, WikiPackage.StatusApplied],
        WikiPackage.StatusApplyFailed => [WikiPackage.StatusProposed, WikiPackage.StatusApplying, WikiPackage.StatusApplyFailed],
        WikiPackage.StatusStale => [WikiPackage.StatusProposed, WikiPackage.StatusStale],
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private string ReadStatus(string id)
    {
        var res = WikiPackageStore.TryRead(_root, id);
        res.State.Should().Be(WikiPackageFileState.Valid);
        return res.Package!.Status;
    }

    // ─── Помощники гашения (дефект 03a50f53) ───

    // Оконная ссылка того же формата, что ставит разбор чата и читает сноска
    // (WikiPage.TryParseSessionRef): гашение работает по ней.
    private const string WindowRef = "session:s1#m1-9";
    private const string WindowRef2 = "session:s2#m1-9";

    private static WikiPackage WindowPkg(string refPart = WindowRef, params WikiPackage.Operation[] ops) =>
        Pkg(ops.Length == 0 ? [AppendOp()] : ops) with { Sources = [new WikiPackage.SourceRef("session", refPart)] };

    private string AtWindow(string status, params WikiPackage.Operation[] ops) =>
        AtWindowRef(status, WindowRef, ops);

    private string AtWindowRef(string status, string refPart, params WikiPackage.Operation[] ops)
    {
        var pkg = WindowPkg(refPart, ops.Length == 0 ? [AppendOp()] : ops);
        WikiPackageStore.Create(_root, pkg).Ok.Should().BeTrue();
        foreach (var step in PathTo(status))
            WikiPackageStore.Transition(_root, pkg.Id, step).Ok.Should().BeTrue($"{status}: {step}");
        return pkg.Id;
    }

    // Большой пакет, как в тесте параллельных решений: его запись занимает миллисекунды, и два
    // Create, стартовавшие от барьера, пересекаются не случайно.
    private static WikiPackage BigWindowPkg()
    {
        var ops = new List<WikiPackage.Operation>(600);
        for (var k = 0; k < 600; k++) ops.Add(AppendOp($"Общее/A{k:D3}.md"));
        return WindowPkg(WindowRef, ops.ToArray());
    }

    // Неотменённые (открытые) пакеты одного окна.
    private List<string> OpenWindowOf(string refPart) => WikiPackageStore.List(_root).Packages
        .Where(p => p.Sources.Any(s => s.Origin == "session" && s.Ref == refPart) && WikiPackage.IsOpen(p.Status))
        .Select(p => p.Id)
        .ToList();
}
