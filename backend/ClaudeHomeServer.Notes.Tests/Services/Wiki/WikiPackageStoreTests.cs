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
}
