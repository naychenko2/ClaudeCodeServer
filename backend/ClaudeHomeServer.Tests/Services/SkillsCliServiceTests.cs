using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Чистый парсинг вывода CLI «npx skills»: результаты find, листинг репозитория (add -l),
// разбор счётчика установок и JSON-подбора LLM.
public class SkillsCliServiceTests
{
    [Fact]
    public void ParseFind_ExtractsSourceSkillAndInstalls()
    {
        const string output = """
            Install with npx skills add <owner/repo@skill>

            vercel-labs/json-render@react-pdf 1.6K installs
            └ https://skills.sh/vercel-labs/json-render/react-pdf

            vm0-ai/vm0-skills@pdf4me 135 installs
            └ https://skills.sh/vm0-ai/vm0-skills/pdf4me
            """;

        var result = SkillsCliService.ParseFind(output);

        result.Should().HaveCount(2);
        result[0].Source.Should().Be("vercel-labs/json-render");
        result[0].Skill.Should().Be("react-pdf");
        result[0].Installs.Should().Be(1600);
        result[1].Source.Should().Be("vm0-ai/vm0-skills");
        result[1].Skill.Should().Be("pdf4me");
        result[1].Installs.Should().Be(135);
    }

    [Fact]
    public void ParseFind_Deduplicates()
    {
        const string output = """
            owner/repo@skill-a 10 installs
            owner/repo@skill-a 10 installs
            """;
        SkillsCliService.ParseFind(output).Should().HaveCount(1);
    }

    [Fact]
    public void ParseFind_IgnoresNonMatchingLines()
    {
        SkillsCliService.ParseFind("just some header text\nno skills here").Should().BeEmpty();
    }

    [Fact]
    public void ParseList_PairsSlugWithDescription_SkippingCategoryHeaders()
    {
        // Формат «add -l» после стриппинга ANSI: рамка │, заголовки категорий, пары slug/описание
        const string output = """
            ◇  Available Skills
            Document Skills
            │
            │    docx
            │
            │      Use this skill for Word documents.
            │
            │    pdf
            │
            │      Use this skill for PDF files.
            """;

        var result = SkillsCliService.ParseList(output, "anthropics/skills");

        result.Should().HaveCount(2);
        result[0].Skill.Should().Be("docx");
        result[0].Description.Should().Be("Use this skill for Word documents.");
        result[0].Source.Should().Be("anthropics/skills");
        result[1].Skill.Should().Be("pdf");
        result[1].Description.Should().Be("Use this skill for PDF files.");
    }

    [Fact]
    public void ParseList_JoinsMultilineDescription()
    {
        const string output = """
            │    xlsx
            │      Spreadsheets and CSV.
            │      Also charts and formulas.
            """;
        var result = SkillsCliService.ParseList(output, "anthropics/skills");
        result.Should().HaveCount(1);
        result[0].Description.Should().Be("Spreadsheets and CSV. Also charts and formulas.");
    }

    [Theory]
    [InlineData("135", 135)]
    [InlineData("1.6K", 1600)]
    [InlineData("2.3M", 2_300_000)]
    [InlineData("24,531", 24531)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    public void ParseInstalls_HandlesSuffixes(string input, int? expected)
    {
        SkillsCliService.ParseInstalls(input).Should().Be(expected);
    }
}
