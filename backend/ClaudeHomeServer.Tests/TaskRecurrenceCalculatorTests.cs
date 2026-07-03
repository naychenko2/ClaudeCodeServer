using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Tests;

public class TaskRecurrenceCalculatorTests
{
    private static TaskRecurrence Rule(
        TaskRecurrenceType type, int interval = 1, List<int>? weekdays = null, string? until = null) =>
        new() { Type = type, Interval = interval, Weekdays = weekdays, Until = until };

    [Fact]
    public void Daily_прибавляет_интервал_дней()
    {
        Assert.Equal("2026-07-04", TaskRecurrenceCalculator.NextDueDate("2026-07-03", Rule(TaskRecurrenceType.Daily)));
        Assert.Equal("2026-07-06", TaskRecurrenceCalculator.NextDueDate("2026-07-03", Rule(TaskRecurrenceType.Daily, 3)));
    }

    [Fact]
    public void Weekly_без_дней_повторяет_день_текущего_срока()
    {
        // 2026-07-03 — пятница; следующая пятница
        Assert.Equal("2026-07-10", TaskRecurrenceCalculator.NextDueDate("2026-07-03", Rule(TaskRecurrenceType.Weekly)));
    }

    [Fact]
    public void Weekly_по_дням_берёт_ближайший_из_набора()
    {
        // 2026-07-03 — пятница; дни Пн(1) и Ср(3) → ближайший Пн 06.07
        var rule = Rule(TaskRecurrenceType.Weekly, weekdays: [1, 3]);
        Assert.Equal("2026-07-06", TaskRecurrenceCalculator.NextDueDate("2026-07-03", rule));
        // от Пн 06.07 → Ср 08.07 той же недели
        Assert.Equal("2026-07-08", TaskRecurrenceCalculator.NextDueDate("2026-07-06", rule));
    }

    [Fact]
    public void Weekly_каждые_две_недели_пропускает_неделю()
    {
        // 2026-07-03 — пятница; каждые 2 недели по пятницам → 17.07 (неделя 06.07 пропущена)
        var rule = Rule(TaskRecurrenceType.Weekly, 2, [5]);
        Assert.Equal("2026-07-17", TaskRecurrenceCalculator.NextDueDate("2026-07-03", rule));
    }

    [Fact]
    public void Monthly_клипует_31е_на_конец_короткого_месяца()
    {
        Assert.Equal("2026-08-31", TaskRecurrenceCalculator.NextDueDate("2026-07-31", Rule(TaskRecurrenceType.Monthly)));
        Assert.Equal("2026-09-30", TaskRecurrenceCalculator.NextDueDate("2026-08-31", Rule(TaskRecurrenceType.Monthly)));
    }

    [Fact]
    public void Yearly_клипует_29_февраля()
    {
        Assert.Equal("2029-02-28", TaskRecurrenceCalculator.NextDueDate("2028-02-29", Rule(TaskRecurrenceType.Yearly)));
    }

    [Fact]
    public void Until_обрывает_серию()
    {
        var rule = Rule(TaskRecurrenceType.Daily, until: "2026-07-04");
        Assert.Equal("2026-07-04", TaskRecurrenceCalculator.NextDueDate("2026-07-03", rule));
        Assert.Null(TaskRecurrenceCalculator.NextDueDate("2026-07-04", rule));
    }

    [Fact]
    public void Кривой_срок_или_None_дают_null()
    {
        Assert.Null(TaskRecurrenceCalculator.NextDueDate("03.07.2026", Rule(TaskRecurrenceType.Daily)));
        Assert.Null(TaskRecurrenceCalculator.NextDueDate("2026-07-03", Rule(TaskRecurrenceType.None)));
    }
}
