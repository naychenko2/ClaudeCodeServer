using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Парс ответа командного autolearn: формат {items}, маппинг типов, legacy-массив, мусор, salience
public class TeamMemoryAutolearnParseTests
{
    [Fact]
    public void НовыйФормат_Items_СТипами()
    {
        var raw = """
            {"items":[
              {"type":"decision","text":"Выбрали PostgreSQL","salience":0.9},
              {"type":"convention","text":"Коммиты на русском","salience":0.7},
              {"type":"fact","text":"Прод на naychenko.me","salience":0.8},
              {"type":"glossary","text":"Персона — олицетворённый агент"}
            ]}
            """;

        var items = TeamMemoryAutolearnService.Parse(raw);

        items.Should().HaveCount(4);
        items[0].Type.Should().Be(TeamMemoryType.Decision);
        items[0].Text.Should().Be("Выбрали PostgreSQL");
        items[0].Salience.Should().Be(0.9);
        items[1].Type.Should().Be(TeamMemoryType.Convention);
        items[2].Type.Should().Be(TeamMemoryType.Fact);
        items[3].Type.Should().Be(TeamMemoryType.Glossary);
        items[3].Salience.Should().Be(1.0);   // salience отсутствует → дефолт
    }

    [Fact]
    public void НеизвестныйТип_ПадаетВFact()
    {
        var raw = """{"items":[{"type":"whatever","text":"Нечто"}]}""";

        TeamMemoryAutolearnService.Parse(raw).Should().ContainSingle()
            .Which.Type.Should().Be(TeamMemoryType.Fact);
    }

    [Fact]
    public void Преамбула_Fence_Парсится()
    {
        var raw = "Вот результат:\n```json\n{\"items\":[{\"type\":\"fact\",\"text\":\"Стек .NET 9\"}]}\n```";

        TeamMemoryAutolearnService.Parse(raw).Should().ContainSingle()
            .Which.Text.Should().Be("Стек .NET 9");
    }

    [Fact]
    public void ЛегасиМассив_Парсится()
    {
        var raw = """[{"type":"decision","text":"Решили X"},{"type":"fact","text":"Факт Y"}]""";

        var items = TeamMemoryAutolearnService.Parse(raw);

        items.Should().HaveCount(2);
        items[0].Type.Should().Be(TeamMemoryType.Decision);
        items[0].Salience.Should().Be(1.0);   // в legacy нет salience → дефолт
    }

    [Theory]
    [InlineData("")]
    [InlineData("нечего запоминать")]
    [InlineData("{сломанный json")]
    [InlineData("{\"foo\":\"bar\"}")]
    public void Мусор_ПустойРезультат(string raw)
    {
        TeamMemoryAutolearnService.Parse(raw).Should().BeEmpty();
    }

    [Fact]
    public void Salience_ДефолтЕдиница_ИКлампВДиапазон()
    {
        var raw = """
            {"items":[
              {"type":"fact","text":"Без важности"},
              {"type":"fact","text":"Завышенная","salience":7},
              {"type":"fact","text":"Заниженная","salience":0.001}
            ]}
            """;

        var items = TeamMemoryAutolearnService.Parse(raw);

        items[0].Salience.Should().Be(1.0);
        items[1].Salience.Should().Be(1.0);
        items[2].Salience.Should().Be(0.05);
    }

    [Fact]
    public void ПустойТекст_Пропускается()
    {
        var raw = """{"items":[{"type":"fact","text":"  "},{"type":"fact","text":"Норм"}]}""";

        TeamMemoryAutolearnService.Parse(raw).Should().ContainSingle()
            .Which.Text.Should().Be("Норм");
    }
}
