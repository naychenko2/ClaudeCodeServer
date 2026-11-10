using ClaudeHomeServer.Services.Notes.Wiki;

namespace ClaudeHomeServer.Tests.Services;

// Виды страниц rule/term/state и поле as_of (ADR-039, решение 3). Чистый разбор —
// юнит без окружения. Legacy overview/topic/entity/concept читаются, но в список
// создаваемых операций не входят; as_of — ГГГГ-ММ-ДД или 'unknown', иное — ошибка.
public class WikiFrontmatterKindsTests
{
    private static string Fm(string type, string extra = "") =>
        "---\ntype: " + type + "\nsummary: тестовая страница\n" + extra + "status: active\n---";

    // —— Новые виды читаются, поле as_of необязательно ——

    [Theory]
    [InlineData("rule")]
    [InlineData("term")]
    [InlineData("state")]
    public void Шапка_новыйВид_разбирается(string type)
    {
        var r = WikiFrontmatter.Parse(Fm(type));
        r.Errors.Should().BeEmpty();
        r.Model!.Type.Should().Be(type);
        r.Model.AsOf.Should().BeNull();
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("topic")]
    [InlineData("entity")]
    [InlineData("concept")]
    public void Шапка_legacyВид_читается(string type)
    {
        var r = WikiFrontmatter.Parse(Fm(type));
        r.Errors.Should().BeEmpty();
        r.Model!.Type.Should().Be(type);
    }

    [Fact]
    public void Шапка_неизвестныйВид_отбит()
    {
        var r = WikiFrontmatter.Parse(Fm("foo"));
        r.Model.Should().BeNull();
        r.Errors.Should().Contain(e => e.Contains("type 'foo'"));
    }

    [Fact]
    public void CreatableTypeValues_содержит_только_создаваемые_виды()
    {
        WikiFrontmatter.CreatableTypeValues.Should()
            .Equal(new[] { "decision", "rule", "term", "state", "source", "chronicle" });
        WikiFrontmatter.CreatableTypeValues.Should()
            .NotContain("overview").And.NotContain("topic").And.NotContain("entity").And.NotContain("concept");
    }

    // —— as_of: дата или unknown; иное — ошибка с именем поля ——

    [Theory]
    [InlineData("2026-11-10")]
    [InlineData("unknown")]
    public void Шапка_as_of_допустимоеЗначение_разбирается(string value)
    {
        var r = WikiFrontmatter.Parse(Fm("decision", "as_of: " + value + "\n"));
        r.Errors.Should().BeEmpty();
        r.Model!.AsOf.Should().Be(value);
    }

    [Theory]
    [InlineData("10.11.2026")]
    [InlineData("2026-11")]
    [InlineData("2026-13-01")]
    [InlineData("вчера")]
    public void Шапка_as_of_не_дата_отбита(string value)
    {
        var r = WikiFrontmatter.Parse(Fm("decision", "as_of: " + value + "\n"));
        r.Model.Should().BeNull();
        r.Errors.Should().Contain(e => e.Contains("as_of"));
    }

    [Fact]
    public void Шапка_as_of_пустое_значение_отбито()
    {
        var r = WikiFrontmatter.Parse(Fm("decision", "as_of: \n"));
        r.Model.Should().BeNull();
        r.Errors.Should().Contain(e => e.Contains("as_of"));
    }

    // —— Рендер: as_of сразу после type, пустой не пишется, круговой разбор равен ——

    [Fact]
    public void Рендер_as_of_после_type_и_круговойРазборДаётРавнуюМодель()
    {
        var first = WikiFrontmatter.Parse(Fm("decision", "as_of: 2026-11-10\n"));
        first.Errors.Should().BeEmpty();
        var rendered = first.Model!.Render();
        rendered.Should().Contain("type: decision\nas_of: 2026-11-10\n");
        var second = WikiFrontmatter.Parse(rendered);
        second.Errors.Should().BeEmpty();
        second.Model.Should().Be(first.Model);
    }

    [Fact]
    public void Рендер_as_of_пустой_не_пишется()
    {
        var first = WikiFrontmatter.Parse(Fm("decision"));
        first.Errors.Should().BeEmpty();
        first.Model!.Render().Should().NotContain("as_of");
    }

    // —— Равенство и хеш по as_of: две модели, различающиеся только as_of, не равны ——

    [Fact]
    public void Равенство_и_хеш_модели_различающиеся_только_as_of_не_равны()
    {
        var dated = WikiFrontmatter.Parse(Fm("decision", "as_of: 2026-11-10\n")).Model!;
        var unknown = WikiFrontmatter.Parse(Fm("decision", "as_of: unknown\n")).Model!;
        dated.Should().NotBe(unknown);
        dated.GetHashCode().Should().NotBe(unknown.GetHashCode());
    }
}
