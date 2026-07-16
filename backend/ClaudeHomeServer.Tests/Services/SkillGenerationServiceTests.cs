using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Разбор JSON-ответа LLM-генерации навыка и нормализация имени в безопасный слаг.
public class SkillGenerationServiceTests
{
    [Fact]
    public void Parse_PlainJsonObject()
    {
        const string answer = """{"name":"pdf-table-extract","description":"Извлекает таблицы из PDF","body":"# Инструкция\nШаги…"}""";
        var g = SkillGenerationService.Parse(answer);
        g.Should().NotBeNull();
        g!.Name.Should().Be("pdf-table-extract");
        g.Description.Should().Be("Извлекает таблицы из PDF");
        g.Body.Should().Contain("Шаги");
    }

    [Fact]
    public void Parse_ToleratesMarkdownAndProse()
    {
        const string answer = """
            Вот готовый навык:
            ```json
            {"name":"docx-writer","description":"Пишет DOCX","body":"тело"}
            ```
            """;
        var g = SkillGenerationService.Parse(answer);
        g.Should().NotBeNull();
        g!.Name.Should().Be("docx-writer");
    }

    [Fact]
    public void Parse_NoJson_ReturnsNull()
    {
        SkillGenerationService.Parse("не смог сгенерировать навык").Should().BeNull();
    }

    [Fact]
    public void Parse_MissingBody_ReturnsNull()
    {
        const string answer = """{"name":"x","description":"нет тела"}""";
        SkillGenerationService.Parse(answer).Should().BeNull();
    }

    [Fact]
    public void Parse_NormalizesNameToSlug()
    {
        const string answer = """{"name":"My Skill!","description":"d","body":"b"}""";
        var g = SkillGenerationService.Parse(answer);
        g.Should().NotBeNull();
        g!.Name.Should().Be("my-skill");
    }

    [Fact]
    public void Parse_UnsafeName_BecomesSafeSlug()
    {
        const string answer = """{"name":"../../etc/passwd","description":"d","body":"b"}""";
        var g = SkillGenerationService.Parse(answer);
        g.Should().NotBeNull();
        // Слаг не содержит разделителей путей и точек-переходов
        g!.Name.Should().NotContainAny("/", "\\", "..");
        g.Name.Should().MatchRegex("^[a-z0-9-]+$");
    }

    [Theory]
    [InlineData("Извлечение таблиц", "skill")]     // кириллица целиком отсекается → фолбэк
    [InlineData("PDF Tables", "pdf-tables")]
    [InlineData("  a--b  ", "a-b")]
    [InlineData("", "skill")]
    [InlineData("...", "skill")]
    [InlineData("../../x", "x")]
    public void Slugify_ProducesSafeSlug(string input, string expected)
    {
        SkillGenerationService.Slugify(input).Should().Be(expected);
    }
}
