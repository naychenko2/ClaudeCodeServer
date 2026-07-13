using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.TriggerSources;

namespace ClaudeHomeServer.Tests.Services;

// Чистая логика PersonaAutomationService: тихие часы (вкл. переход через полночь) и
// парсинг ответа one-shot гейта персоны (YES/NO + сообщение).
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

    [Theory]
    [InlineData("YES\nПривет, проверь почту", true)]
    [InlineData("yes", true)]
    [InlineData("  YES  ", true)]
    [InlineData("NO", false)]
    [InlineData("", false)]
    [InlineData("Скорее всего не стоит", false)]
    public void ParseGateYes_распознаёт_первую_строку(string answer, bool expected) =>
        Assert.Equal(expected, PersonaAutomationService.ParseGateYes(answer));

    [Fact]
    public void ExtractGateMessage_убирает_строку_YES()
    {
        Assert.Equal("Привет", PersonaAutomationService.ExtractGateMessage("YES\nПривет"));
        Assert.Equal("две\nстроки", PersonaAutomationService.ExtractGateMessage("YES\n\nдве\nстроки"));
        Assert.Equal("", PersonaAutomationService.ExtractGateMessage("YES"));
    }

    [Fact]
    public void BuildGatePrompt_содержит_событие_и_инструкцию()
    {
        var rule = new PersonaAutomationRule
        {
            Name = "Релизы",
            Action = new AutomationAction { Instruction = "Сделай выжимку" },
        };
        var ev = new TriggerEvent("r1", AutomationTriggerType.GitCommit, "Новый коммит");
        var prompt = PersonaAutomationService.BuildGatePrompt(rule, ev);
        Assert.Contains("Релизы", prompt);
        Assert.Contains("Новый коммит", prompt);
        Assert.Contains("Сделай выжимку", prompt);
        Assert.Contains("YES", prompt);
    }
}
