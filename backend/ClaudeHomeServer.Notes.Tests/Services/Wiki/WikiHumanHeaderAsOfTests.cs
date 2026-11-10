using System.Text;
using ClaudeHomeServer.Services.Notes.Wiki;

namespace ClaudeHomeServer.Tests.Services;

// S2-1h (ревью 3d5ba58f, находка 2): as_of — поле кода, как type и kind. Правка других
// полей шапки человеком не стирает as_of существующей страницы (SetHeader), а сама правка
// as_of не должна молча пропадать — она отвергается (HeaderChanges).
public class WikiHumanHeaderAsOfTests
{
    private const string Path = "Общее/Состояние.md";
    private const string Title = "Состояние";
    private const string AsOf = "2026-11-01";

    // Страница type: state с as_of: шапка по fm (по умолчанию — с as_of), содержание фиксированное.
    private static string Page(string? fm = null)
    {
        var sb = new StringBuilder();
        sb.Append("---\ntype: state\n");
        sb.Append(fm ?? $"as_of: {AsOf}\nsummary: Кратко\naliases: []\nowns: []\nstatus: active\n");
        sb.Append("---\n\n");
        sb.Append("# ").Append(Title).Append('\n');
        foreach (var name in new[] { "Сводка", "Хронология", "Противоречия", "История" })
            sb.Append("\n## ").Append(name).Append('\n');
        return sb.ToString();
    }

    private static string Fm(string asOfLine) =>
        asOfLine + "summary: Кратко\naliases: []\nowns: []\nstatus: active\n";

    // —— Рендер: правка summary не стирает as_of ——

    [Fact]
    public void Правка_summary_сохраняет_as_of_и_применяет_новое_значение()
    {
        var state = new List<WikiGate.Draft> { new(Path, Page()) };
        var package = new WikiPackage
        {
            Id = "as-of-render-01",
            Author = WikiPackage.AuthorHuman,
            Action = WikiPackage.ActionEdit,
            Section = WikiSections.Common,
            Sources = [new WikiPackage.SourceRef("human", "as-of-render")],
            Operations = [new WikiPackage.Operation
            {
                Op = WikiPackage.OpHeader, Page = Path, Target = "summary", Text = "Новое",
                Class = WikiPackage.ClassKey("human", WikiPackage.OpHeader),
                Decision = WikiPackage.DecisionAuto,
            }],
            Status = WikiPackage.StatusChecking,
        };

        var res = WikiHumanRender.Render(state, package);

        res.Error.Should().BeNull();
        var content = res.Drafts.Single(d => d.Path == Path).Content;
        content.Should().Contain("as_of: " + AsOf);
        content.Should().Contain("summary: Новое");
        // Круговой разбор: после рендера as_of и новый summary — в модели, не только в тексте.
        var fm = WikiPage.Parse(content).Model!.Frontmatter;
        fm.AsOf.Should().Be(AsOf);
        fm.Summary.Should().Be("Новое");
    }

    // —— Планировщик: правка as_of человеком — отказ с именем поля ——

    [Fact]
    public void Смена_as_of_даёт_отказ()
    {
        var plan = WikiHumanEditPlanner.Build(Path, Page(), Page(Fm("as_of: 2026-11-05\n")));

        plan.Ok.Should().BeFalse();
        plan.Reason.Should().Contain("as_of");
        plan.Operations.Should().BeEmpty();
    }

    [Fact]
    public void Удаление_as_of_человеком_даёт_отказ()
    {
        var plan = WikiHumanEditPlanner.Build(Path, Page(), Page(Fm("")));

        plan.Ok.Should().BeFalse();
        plan.Reason.Should().Contain("as_of");
        plan.Operations.Should().BeEmpty();
    }

    [Fact]
    public void Новая_страница_с_as_of_даёт_отказ()
    {
        // as_of ставит код при применении (S2-7): человек его не задаёт даже при создании.
        var plan = WikiHumanEditPlanner.Build(Path, null, Page());

        plan.Ok.Should().BeFalse();
        plan.Reason.Should().Contain("as_of");
        plan.Operations.Should().BeEmpty();
    }
}
