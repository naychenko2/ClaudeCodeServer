using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Парс ответа autolearn: новый формат {items, focus}, legacy-массив, мусор, salience
public class PersonaMemoryAutolearnParseTests
{
    [Fact]
    public void НовыйФормат_ItemsИFocus()
    {
        var raw = """
            {"items":[
              {"type":"semantic","text":"Пользователя зовут Андрей","salience":0.9},
              {"type":"procedural","text":"Отвечать кратко","salience":0.6},
              {"type":"episodic","text":"Обсудили план релиза","salience":0.4}
            ],
            "focus":{"what":"Подготовка релиза","status":"в процессе","nextStep":"собрать changelog"}}
            """;

        var result = PersonaMemoryAutolearnService.Parse(raw);

        result.Items.Should().HaveCount(3);
        result.Items[0].Should().BeEquivalentTo(
            new PersonaMemoryAutolearnService.AutolearnItem(PersonaMemoryType.Semantic, "Пользователя зовут Андрей", 0.9));
        result.Items[1].Type.Should().Be(PersonaMemoryType.Procedural);
        result.Items[2].Type.Should().Be(PersonaMemoryType.Episodic);

        result.Focus.Should().NotBeNull();
        result.Focus!.What.Should().Be("Подготовка релиза");
        result.Focus.Status.Should().Be("в процессе");
        result.Focus.NextStep.Should().Be("собрать changelog");
    }

    [Fact]
    public void НовыйФормат_СПреамбулойИFence_Парсится()
    {
        var raw = "Вот результат:\n```json\n{\"items\":[{\"type\":\"semantic\",\"text\":\"Факт\"}],\"focus\":null}\n```";

        var result = PersonaMemoryAutolearnService.Parse(raw);

        result.Items.Should().ContainSingle().Which.Text.Should().Be("Факт");
        result.Focus.Should().BeNull();
    }

    [Fact]
    public void ЛегасиМассив_ПарситсяБезФокуса()
    {
        var raw = """[{"type":"semantic","text":"Имя — Андрей"},{"type":"episodic","text":"Итог разговора"}]""";

        var result = PersonaMemoryAutolearnService.Parse(raw);

        result.Items.Should().HaveCount(2);
        result.Items[0].Salience.Should().Be(1.0);   // в legacy-формате salience нет → дефолт
        result.Focus.Should().BeNull();
    }

    [Fact]
    public void FocusNull_ФокусНеСтавится()
    {
        var raw = """{"items":[{"type":"semantic","text":"Факт"}],"focus":null}""";

        PersonaMemoryAutolearnService.Parse(raw).Focus.Should().BeNull();
    }

    [Fact]
    public void FocusБезWhat_Игнорируется()
    {
        var raw = """{"items":[],"focus":{"what":"  ","status":"идёт"}}""";

        PersonaMemoryAutolearnService.Parse(raw).Focus.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("нечего запоминать")]
    [InlineData("{сломанный json")]
    [InlineData("{\"foo\":\"bar\"}")]
    public void Мусор_ПустойРезультат(string raw)
    {
        var result = PersonaMemoryAutolearnService.Parse(raw);

        result.Items.Should().BeEmpty();
        result.Focus.Should().BeNull();
    }

    [Fact]
    public void Salience_ДефолтЕдиница_ИКлампВДиапазон()
    {
        var raw = """
            {"items":[
              {"type":"semantic","text":"Без важности"},
              {"type":"semantic","text":"Завышенная","salience":7},
              {"type":"semantic","text":"Заниженная","salience":0.001}
            ],"focus":null}
            """;

        var result = PersonaMemoryAutolearnService.Parse(raw);

        result.Items[0].Salience.Should().Be(1.0);
        result.Items[1].Salience.Should().Be(1.0);
        result.Items[2].Salience.Should().Be(0.05);
    }

    [Fact]
    public void ПустойТекст_Пропускается()
    {
        var raw = """{"items":[{"type":"semantic","text":"  "},{"type":"semantic","text":"Норм"}],"focus":null}""";

        var result = PersonaMemoryAutolearnService.Parse(raw);

        result.Items.Should().ContainSingle().Which.Text.Should().Be("Норм");
    }
}
