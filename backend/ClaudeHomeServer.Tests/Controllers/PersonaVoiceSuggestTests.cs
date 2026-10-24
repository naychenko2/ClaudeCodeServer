using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
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

    // --- Валидация голоса для записи (общая у PUT /voice и MCP personas_create/update) ---

    [Fact]
    public void Валидация_ПустойГолос_Годится_ЭтоСброс()
    {
        PersonasCrudService.VoiceError(null).Should().BeNull();
        PersonasCrudService.VoiceError(new PersonaVoice()).Should().BeNull();
    }

    [Fact]
    public void Валидация_ИзвестныйГолосИЕгоАмплуа_Годятся()
    {
        PersonasCrudService.VoiceError(new PersonaVoice { Voice = "masha", Role = "friendly" }).Should().BeNull();
        PersonasCrudService.VoiceError(new PersonaVoice { Voice = "madirus" }).Should().BeNull("алиас тоже голос");
    }

    [Fact]
    public void Валидация_НеизвестныйГолос_ОтказСоСпискомГолосов()
    {
        var error = PersonasCrudService.VoiceError(new PersonaVoice { Voice = "зорро" });
        error.Should().Contain("Неизвестный голос синтеза: зорро").And.Contain("masha");
    }

    [Fact]
    public void Валидация_ЧужоеАмплуа_ОтказСоСпискомДопустимых()
    {
        PersonasCrudService.VoiceError(new PersonaVoice { Voice = "masha", Role = "whisper" })
            .Should().Contain("не умеет «whisper»").And.Contain("friendly");
        PersonasCrudService.VoiceError(new PersonaVoice { Voice = "filipp", Role = "strict" })
            .Should().Contain("не поддерживает амплуа");
    }

    [Fact]
    public void Валидация_АмплуаБезГолоса_И_ТемпВнеДиапазона_Отказ()
    {
        PersonasCrudService.VoiceError(new PersonaVoice { Role = "strict" })
            .Should().Contain("вместе с голосом");
        PersonasCrudService.VoiceError(new PersonaVoice { Voice = "masha", Speed = 5 })
            .Should().Contain("0.1–3.0");
    }

    private static (string Voice, string? Role)? PersonaVoiceSuggest(string raw) =>
        PersonasCrudService.ParseVoiceAnswer(raw);
}
