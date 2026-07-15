using ClaudeHomeServer.Services.Prompts;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Тексты OmO: протокол цикла «до готово» и категории делегирования
// (магслово ultrawork удалено — его ловит keyword-detector плагина oh-my-claudecode)
public class OmoPromptsTests
{
    [Fact]
    public void ПротоколЦикла_СодержитМаркерЗавершения()
    {
        var turn = OmoPrompts.WorkLoopTurn("ГОТОВО");

        turn.Should().Contain("<promise>ГОТОВО</promise>");
        turn.Should().Contain("ЦИКЛ «ДО ГОТОВО»");
    }

    [Fact]
    public void ПродолжениеЦикла_НомерИтерацииИМаркер()
    {
        var text = OmoPrompts.WorkLoopContinuation("ГОТОВО", 3, 20);

        text.Should().Contain("3/20");
        text.Should().Contain("<promise>ГОТОВО</promise>");
    }

    [Fact]
    public void ВерификацияЦикла_ТребуетСвидетельств()
    {
        OmoPrompts.WorkLoopVerification.Should().Contain("ВЕРИФИКАЦИЯ");
        OmoPrompts.WorkLoopVerification.Should().Contain("свидетельства");
    }

    [Fact]
    public void СправочникКатегорий_НепустойИСодержитКатегории()
    {
        OmoPrompts.DelegationCategories.Should().Contain("ultrabrain");
        OmoPrompts.DelegationCategories.Should().Contain("visual-engineering");
    }
}
