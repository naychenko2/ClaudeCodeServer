using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Разбор JSON-ответа перевода описаний (id → перевод), терпимость к markdown/прозе.
public class SkillTranslationServiceTests
{
    [Fact]
    public void ParseTranslations_PlainObject()
    {
        const string answer = """{"anthropics/skills@pdf":"Работа с PDF","anthropics/skills@xlsx":"Таблицы Excel"}""";
        var map = SkillTranslationService.ParseTranslations(answer);
        map.Should().HaveCount(2);
        map["anthropics/skills@pdf"].Should().Be("Работа с PDF");
        map["anthropics/skills@xlsx"].Should().Be("Таблицы Excel");
    }

    [Fact]
    public void ParseTranslations_ToleratesMarkdownFence()
    {
        const string answer = """
            Вот переводы:
            ```json
            {"a@b":"Перевод"}
            ```
            """;
        var map = SkillTranslationService.ParseTranslations(answer);
        map.Should().ContainSingle();
        map["a@b"].Should().Be("Перевод");
    }

    [Fact]
    public void ParseTranslations_NoJson_ReturnsEmpty()
    {
        SkillTranslationService.ParseTranslations("нет данных").Should().BeEmpty();
    }
}
