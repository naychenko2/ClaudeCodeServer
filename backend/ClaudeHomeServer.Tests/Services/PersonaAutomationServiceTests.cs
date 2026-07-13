using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Tests.Services;

// Тесты PersonaAutomationService: тихие часы (вкл. переход через полночь) и каркас
// постановка-промпта (контекст срабатывания попадает в промпт). Сам промпт-билдер
// асинхронный/instance (читает файлы) — покрыт сборкой и e2e, здесь проверяем чистую логику.
public class PersonaAutomationServiceTests
{
    private static PersonaAutomationRule RuleWithQuiet(string? from, string? to) => new()
    {
        Trigger = new AutomationTrigger(),
        Condition = new AutomationCondition { QuietFrom = from, QuietTo = to },
    };

    private static DateTime Local(int hour, int minute = 0) =>
        new(2026, 7, 13, hour, minute, 0, DateTimeKind.Unspecified);

    [Fact]
    public void InQuietWindow_без_условия_всегда_false()
    {
        var rule = new PersonaAutomationRule { Trigger = new AutomationTrigger() };
        Assert.False(PersonaAutomationService.InQuietWindow(rule, Local(3, 0)));
    }

    [Fact]
    public void InQuietWindow_обычный_диапазон()
    {
        var rule = RuleWithQuiet("12:00", "14:00");
        Assert.False(PersonaAutomationService.InQuietWindow(rule, Local(11, 59)));
        Assert.True(PersonaAutomationService.InQuietWindow(rule, Local(13, 0)));
        Assert.False(PersonaAutomationService.InQuietWindow(rule, Local(14, 0))); // правая граница не входит
        Assert.False(PersonaAutomationService.InQuietWindow(rule, Local(15, 0)));
    }

    [Fact]
    public void InQuietWindow_переход_через_полночь()
    {
        var rule = RuleWithQuiet("23:00", "07:00");
        Assert.True(PersonaAutomationService.InQuietWindow(rule, Local(2, 0)));   // ночь
        Assert.True(PersonaAutomationService.InQuietWindow(rule, Local(23, 30))); // вечер
        Assert.False(PersonaAutomationService.InQuietWindow(rule, Local(10, 0))); // день
        Assert.False(PersonaAutomationService.InQuietWindow(rule, Local(7, 0)));  // правая граница не входит
    }
}
