using ClaudeHomeServer.Services.Notes.Wiki;

namespace ClaudeHomeServer.Tests.Services;

// B3b (ADR-038): форма пакета автора human и его вход в хранилище. Применение, сверка baseHash,
// журнал и «Вернуть мою» — WikiControllerPagesTests (через REST, как их видит человек).
public class WikiApplyHumanTests : IDisposable
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Page = "Общее/X.md";
    private readonly string _root;

    public WikiApplyHumanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wiki_apply_human_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(WikiPackage.OpEdit)]
    [InlineData(WikiPackage.OpNotes)]
    [InlineData(WikiPackage.OpHeader)]
    public void ПакетИИ_СОперациейЧеловека_ОшибкаФормы(string op)
    {
        var pkg = AiPackage(Op(op, "session"));
        WikiPackage.ShapeErrors(pkg).Should().Contain(e => e.Contains($"операция «{op}» не из перечня"));
    }

    [Fact]
    public void ПакетHuman_БезДействия_ИлиСЧужим_ОшибкаФормы()
    {
        WikiPackage.ShapeErrors(HumanPackage(WikiPackage.ActionEdit, Op(WikiPackage.OpEdit)) with { Action = null })
            .Should().Contain(e => e.Contains("действие"));
        WikiPackage.ShapeErrors(HumanPackage("rollback", Op(WikiPackage.OpEdit)))
            .Should().Contain(e => e.Contains("действие"));
        WikiPackage.ShapeErrors(AiPackage(Op(WikiPackage.OpAppend, "session")) with { Action = WikiPackage.ActionEdit })
            .Should().Contain(e => e.Contains("только у пакета human"));
    }

    [Fact]
    public void ПакетHuman_Допустимые_Операции_ИSupersedeБезТекста()
    {
        var removal = Op(WikiPackage.OpSupersede) with { Text = null, Reason = "снято человеком" };
        WikiPackage.ShapeErrors(HumanPackage(WikiPackage.ActionEdit, Op(WikiPackage.OpEdit), Op(WikiPackage.OpNotes),
            Op(WikiPackage.OpHeader), removal)).Should().BeEmpty();
        // Для ai supersede без текста по-прежнему ошибка: «убрать» — только у человека.
        WikiPackage.ShapeErrors(AiPackage(Op(WikiPackage.OpSupersede, "session") with { Text = null }))
            .Should().Contain(e => e.Contains("без содержания"));
    }

    [Fact]
    public void CreateHuman_РождаетсяВAccepted_ЛимитОткрытыхЕгоНеБлокирует()
    {
        // 20 открытых пакетов ИИ — «Входящие» забиты; правка человека всё равно проходит.
        for (var i = 0; i < WikiPackage.MaxOpenPackages; i++)
            WikiPackageStore.Create(_root, AiPackage(Op(WikiPackage.OpAppend, "session"))).Ok.Should().BeTrue();
        WikiPackageStore.Create(_root, AiPackage(Op(WikiPackage.OpAppend, "session"))).Code
            .Should().Be(WikiPackageStore.CodeLimit, "для ИИ лимит держится");

        var res = WikiPackageStore.CreateHuman(_root, HumanPackage(WikiPackage.ActionEdit, Op(WikiPackage.OpEdit)));
        res.Ok.Should().BeTrue(res.Reason);
        res.Package!.Status.Should().Be(WikiPackage.StatusAccepted);
        WikiPackageStore.TryRead(_root, res.Package.Id).Package!.Action.Should().Be("edit");
    }

    [Fact]
    public void CreateHuman_ПакетИИ_Отказ_ИНеВAccepted_Отказ()
    {
        WikiPackageStore.CreateHuman(_root, AiPackage(Op(WikiPackage.OpAppend, "session")) with { Status = WikiPackage.StatusAccepted })
            .Code.Should().Be(WikiPackageStore.CodeShape);
        WikiPackageStore.CreateHuman(_root, HumanPackage(WikiPackage.ActionEdit, Op(WikiPackage.OpEdit)) with { Status = WikiPackage.StatusChecking })
            .Code.Should().Be(WikiPackageStore.CodeShape);
    }

    private static WikiPackage AiPackage(WikiPackage.Operation op) => new()
    {
        Id = WikiPackageStore.NewId(),
        Section = "common",
        Sources = [new WikiPackage.SourceRef("session", "chat-1")],
        Operations = [op],
        Status = WikiPackage.StatusChecking,
    };

    private static WikiPackage HumanPackage(string action, params WikiPackage.Operation[] ops) => new()
    {
        Id = WikiPackageStore.NewId(),
        Author = WikiPackage.AuthorHuman,
        Action = action,
        Section = "common",
        Sources = [new WikiPackage.SourceRef("human", "human:p1")],
        Operations = ops,
        Status = WikiPackage.StatusAccepted,
    };

    private static WikiPackage.Operation Op(string op, string origin = "human") => new()
    {
        Op = op,
        Page = Page,
        PageSection = "Сводка",
        Text = "утверждение человека",
        Target = "прежнее утверждение",
        Class = WikiPackage.ClassKey(origin, op),
        Decision = WikiPackage.DecisionAuto,
        BaseHashes = new Dictionary<string, string> { [Page] = Hash },
    };
}
