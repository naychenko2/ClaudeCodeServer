using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.Extensions.Configuration;

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

    // --- Потолок реакций в час: HasHourlyBudget (peek) не потребляет квоту ---

    private static AutomationStateStore MkStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "autostate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(dir, "projects.json") })
            .Build();
        return new AutomationStateStore(config);
    }

    [Fact]
    public void HasHourlyBudget_НеПотребляетКвоту()
    {
        var store = MkStore();
        var now = DateTime.UtcNow;

        // Peek много раз — квота не тратится
        for (var i = 0; i < 10; i++)
            Assert.True(store.HasHourlyBudget("p1", cap: 2, now));

        // Потребляем ровно cap
        Assert.True(store.TryConsumeHourly("p1", 2, now));
        Assert.True(store.TryConsumeHourly("p1", 2, now));

        // Квота исчерпана: peek и consume согласованы
        Assert.False(store.HasHourlyBudget("p1", 2, now));
        Assert.False(store.TryConsumeHourly("p1", 2, now));
    }

    [Fact]
    public void HasHourlyBudget_НовоеОкно_КвотаВосстанавливается()
    {
        var store = MkStore();
        var now = DateTime.UtcNow;
        Assert.True(store.TryConsumeHourly("p1", 1, now));
        Assert.False(store.HasHourlyBudget("p1", 1, now));

        // Через час окно истекло — peek снова даёт бюджет
        Assert.True(store.HasHourlyBudget("p1", 1, now.AddHours(1)));
        Assert.True(store.TryConsumeHourly("p1", 1, now.AddHours(1)));
    }
}
