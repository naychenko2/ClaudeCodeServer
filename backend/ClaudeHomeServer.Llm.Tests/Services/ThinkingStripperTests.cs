using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Tests.Services;

// Мутация: убрать ветку <|channel> из шаблона — первые два теста краснеют.
public class ThinkingStripperTests
{
    [Theory]
    [InlineData("<|channel>thought\n<channel|>Nginx Config & Tuning", "Nginx Config & Tuning")]
    [InlineData("<|channel>thought\nсначала подумаю<channel|>\n* Кошки спят", "* Кошки спят")]
    [InlineData("<think>хм</think>\nОтвет", "Ответ")]
    [InlineData("Обычный ответ без маркеров", "Обычный ответ без маркеров")]
    public void СрезаетБлокРазмышлений(string raw, string expected) =>
        Assert.Equal(expected, ThinkingStripper.Strip(raw));
}
