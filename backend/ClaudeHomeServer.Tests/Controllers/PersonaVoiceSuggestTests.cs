using ClaudeHomeServer.Controllers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Разбор ответа модели при подборе голоса. Модель отвечает текстом, поэтому весь риск здесь:
// выдуманное имя голоса или чужое амплуа дошли бы до SpeechKit и вернулись ошибкой 400.
public class PersonaVoiceSuggestTests
{
    [Fact]
    public void ГолосИАмплуа_РазбираютсяОба()
    {
        PersonaVoiceSuggest("masha friendly").Should().Be(("masha", "friendly"));
    }

    [Fact]
    public void ТолькоГолос_АмплуаПустое()
    {
        PersonaVoiceSuggest("kirill").Should().Be(("kirill", (string?)null));
    }

    [Fact]
    public void ПояснениеВокругОтвета_НеМешает()
    {
        // Модель любит добавить фразу вопреки «никаких пояснений»
        PersonaVoiceSuggest("Думаю, подойдёт julia strict — сдержанный тон.")
            .Should().Be(("julia", "strict"));
    }

    [Fact]
    public void Алиас_ПриводитсяККанону()
    {
        // Иначе форма не подсветит подобранный голос в списке
        PersonaVoiceSuggest("madirus").Should().Be(("madi_ru", (string?)null));
    }

    [Fact]
    public void АмплуаНеОтЭтогоГолоса_Отбрасывается()
    {
        // filipp не умеет амплуа вовсе: с ролью в hints SpeechKit ответил бы 400
        PersonaVoiceSuggest("filipp strict").Should().Be(("filipp", (string?)null));
    }

    [Fact]
    public void ВыдуманноеИмя_НеРаспознано()
    {
        // Наверх уйдёт 502 с человеческим текстом, а не молчание: кнопку нажал человек
        PersonaVoiceSuggest("зорро").Should().BeNull();
        PersonaVoiceSuggest("voice42").Should().BeNull();
        PersonaVoiceSuggest("").Should().BeNull();
    }

    [Fact]
    public void ОтказМодели_НеСчитаетсяГолосом()
    {
        // «none» обрабатывается отдельной веткой эндпоинта как честный ответ «не выбрала»
        PersonaVoiceSuggest("none").Should().BeNull();
    }

    private static (string Voice, string? Role)? PersonaVoiceSuggest(string raw) =>
        PersonasController.ParseVoiceAnswer(raw);
}
