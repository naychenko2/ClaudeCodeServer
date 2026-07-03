using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Tests;

public class TaskDueCalculatorTests
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    private static TaskItem Task(string? dueDate, string? dueTime = null, int? reminderMinutes = null) =>
        new() { Title = "t", DueDate = dueDate, DueTime = dueTime, ReminderMinutes = reminderMinutes };

    [Fact]
    public void DueMoment_переводит_локальный_срок_в_UTC()
    {
        // Москва UTC+3: 14:00 локальных = 11:00 UTC
        var due = TaskDueCalculator.DueMomentUtc(Task("2026-07-03", "14:00"), Moscow);
        Assert.Equal(new DateTime(2026, 7, 3, 11, 0, 0, DateTimeKind.Utc), due);
    }

    [Fact]
    public void DueMoment_без_времени_берёт_09_00_локальных()
    {
        var due = TaskDueCalculator.DueMomentUtc(Task("2026-07-03"), Moscow);
        Assert.Equal(new DateTime(2026, 7, 3, 6, 0, 0, DateTimeKind.Utc), due);
    }

    [Fact]
    public void DueMoment_null_когда_срока_нет_или_он_кривой()
    {
        Assert.Null(TaskDueCalculator.DueMomentUtc(Task(null), Moscow));
        Assert.Null(TaskDueCalculator.DueMomentUtc(Task("03.07.2026"), Moscow));
        Assert.Null(TaskDueCalculator.DueMomentUtc(Task("2026-07-03", "25:99"), Moscow));
    }

    [Fact]
    public void ReminderMoment_вычитает_офсет_из_срока()
    {
        var remind = TaskDueCalculator.ReminderMomentUtc(Task("2026-07-03", "14:00", reminderMinutes: 60), Moscow);
        Assert.Equal(new DateTime(2026, 7, 3, 10, 0, 0, DateTimeKind.Utc), remind);
    }

    [Fact]
    public void ReminderMoment_ноль_означает_в_момент_срока()
    {
        var remind = TaskDueCalculator.ReminderMomentUtc(Task("2026-07-03", "14:00", reminderMinutes: 0), Moscow);
        Assert.Equal(new DateTime(2026, 7, 3, 11, 0, 0, DateTimeKind.Utc), remind);
    }

    [Fact]
    public void ReminderMoment_null_без_настроенного_напоминания()
    {
        Assert.Null(TaskDueCalculator.ReminderMomentUtc(Task("2026-07-03", "14:00"), Moscow));
    }

    [Fact]
    public void ResolveTimeZone_фолбэк_на_UTC_для_мусора()
    {
        Assert.Equal(TimeZoneInfo.Utc, TaskDueCalculator.ResolveTimeZone(null));
        Assert.Equal(TimeZoneInfo.Utc, TaskDueCalculator.ResolveTimeZone("Mars/Olympus_Mons"));
        Assert.Equal("Europe/Moscow", TaskDueCalculator.ResolveTimeZone("Europe/Moscow").Id);
    }
}
