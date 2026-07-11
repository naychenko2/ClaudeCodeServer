using ClaudeHomeServer.Services.Prompts;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Тексты и детекторы OmO: магическое слово ultrawork и протокол цикла «до готово»
public class OmoPromptsTests
{
    [Theory]
    [InlineData("ultrawork: почини сборку", true)]
    [InlineData("сделай это ulw", true)]
    [InlineData("нужно ультра качественно", true)]
    [InlineData("УЛЬТРАВОРК режим", true)]
    [InlineData("ультразвук в датчике", false)] // «ультра» — только отдельным словом
    [InlineData("формула воды", false)]
    [InlineData("schulwahl", false)]            // ulw внутри слова не считается
    [InlineData("", false)]
    public void УльтраДетектор_ТолькоОтдельныеСлова(string text, bool expected) =>
        OmoPrompts.ContainsUltraworkKeyword(text).Should().Be(expected);

    [Fact]
    public void УльтраБлок_НепустойИПереведён()
    {
        OmoPrompts.Ultrawork.Should().NotBeNullOrWhiteSpace();
        OmoPrompts.Ultrawork.Should().Contain("РЕЖИМ ULTRAWORK ВКЛЮЧЁН!");
    }

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
