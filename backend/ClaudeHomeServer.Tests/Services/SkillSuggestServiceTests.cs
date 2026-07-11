using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Разбор JSON-ответа LLM-подбора навыков (терпимость к markdown-обрамлению и мусору).
public class SkillSuggestServiceTests
{
    [Fact]
    public void ParsePicks_PlainJsonArray()
    {
        const string answer = """[{"skill":"pdf","reason":"работа с PDF"},{"skill":"xlsx","reason":"таблицы"}]""";
        var result = SkillSuggestService.ParsePicks(answer);
        result.Should().HaveCount(2);
        result[0].Skill.Should().Be("pdf");
        result[0].Reason.Should().Be("работа с PDF");
        result[1].Skill.Should().Be("xlsx");
    }

    [Fact]
    public void ParsePicks_ToleratesMarkdownAndProse()
    {
        const string answer = """
            Вот подходящие навыки:
            ```json
            [{"skill":"docx","reason":"документы Word"}]
            ```
            """;
        var result = SkillSuggestService.ParsePicks(answer);
        result.Should().ContainSingle();
        result[0].Skill.Should().Be("docx");
    }

    [Fact]
    public void ParsePicks_EmptyArray()
    {
        SkillSuggestService.ParsePicks("[]").Should().BeEmpty();
    }

    [Fact]
    public void ParsePicks_NoJson_ReturnsEmpty()
    {
        SkillSuggestService.ParsePicks("нет подходящих навыков").Should().BeEmpty();
    }

    [Fact]
    public void ParsePicks_SkipsEntriesWithoutSkill()
    {
        const string answer = """[{"reason":"нет имени"},{"skill":"pdf","reason":"ок"}]""";
        var result = SkillSuggestService.ParsePicks(answer);
        result.Should().ContainSingle();
        result[0].Skill.Should().Be("pdf");
    }
}
