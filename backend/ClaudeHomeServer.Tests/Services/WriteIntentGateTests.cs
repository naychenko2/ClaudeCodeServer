using ClaudeHomeServer.Services.Prompts;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Гейт подъёма write-схем MCP по тексту хода: важен и позитив, и негатив —
// ложный подъём грузит тяжёлые схемы зря, ложный пропуск = ход без write-инструментов.
public class WriteIntentGateTests
{
    [Theory]
    [InlineData("создай персону-ревьюера")]
    [InlineData("измени роль у агента")]
    [InlineData("удали привязку персоны")]
    [InlineData("настрой проактивность команде")]
    [InlineData("create a persona for reviews")]   // англ. — раньше не срабатывало
    [InlineData("rename this agent")]
    public void PersonaManagement_ДействиеИОбъект_True(string text)
    {
        WriteIntentGate.PersonaManagement(text).Should().BeTrue();
    }

    [Theory]
    [InlineData("расскажи про эту персону")]        // объект без действия
    [InlineData("создай функцию сортировки")]        // действие без объекта-команды
    [InlineData("what does this persona think?")]    // вопрос, не управление
    [InlineData("обычное сообщение")]
    [InlineData("")]
    [InlineData(null)]
    public void PersonaManagement_НетПарыДействиеОбъект_False(string? text)
    {
        WriteIntentGate.PersonaManagement(text).Should().BeFalse();
    }

    [Theory]
    [InlineData("создай проект для клиента")]
    [InlineData("переименуй чат в архиве")]
    [InlineData("проиндексируй базу знаний")]
    [InlineData("create a project skeleton")]        // англ.
    [InlineData("rename the chat")]
    public void WorkspaceWrite_ДействиеИОбъект_True(string text)
    {
        WriteIntentGate.WorkspaceWrite(text).Should().BeTrue();
    }

    [Theory]
    [InlineData("покажи мои проекты")]               // объект без пишущего действия
    [InlineData("сохрани этот файл")]                // голый «файл» намеренно не объект
    [InlineData("save this file")]
    [InlineData("просто вопрос")]
    [InlineData("")]
    [InlineData(null)]
    public void WorkspaceWrite_НетПарыДействиеОбъект_False(string? text)
    {
        WriteIntentGate.WorkspaceWrite(text).Should().BeFalse();
    }
}
