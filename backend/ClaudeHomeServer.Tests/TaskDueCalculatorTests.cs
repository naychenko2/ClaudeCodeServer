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

    // ─── Переходы на летнее/зимнее время (Europe/Berlin: CET +1 / CEST +2) ────
    // 2026: spring-forward 29.03 (02:00→03:00), fall-back 25.10 (03:00→02:00)

    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    [Fact]
    public void DueMoment_летом_и_зимой_даёт_разный_UTC_офсет()
    {
        // Лето: CEST +2 → 09:00 локальных = 07:00 UTC
        var summer = TaskDueCalculator.DueMomentUtc(Task("2026-07-01", "09:00"), Berlin);
        Assert.Equal(new DateTime(2026, 7, 1, 7, 0, 0, DateTimeKind.Utc), summer);

        // Зима: CET +1 → 09:00 локальных = 08:00 UTC
        var winter = TaskDueCalculator.DueMomentUtc(Task("2026-01-15", "09:00"), Berlin);
        Assert.Equal(new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc), winter);
    }

    [Fact]
    public void DueMoment_несуществующее_время_spring_forward_не_бросает()
    {
        // 29.03.2026 02:30 в Берлине не существует (часы прыгают 02:00→03:00).
        // GetUtcOffset для invalid time документированно берёт офсет стандартного времени (+1)
        var due = TaskDueCalculator.DueMomentUtc(Task("2026-03-29", "02:30"), Berlin);
        Assert.Equal(new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc), due);
    }

    [Fact]
    public void DueMoment_двойное_время_fall_back_берёт_стандартный_офсет()
    {
        // 25.10.2026 02:30 в Берлине случается дважды (CEST 00:30 UTC и CET 01:30 UTC).
        // Для ambiguous time GetUtcOffset берёт офсет стандартного времени (+1) → 01:30 UTC
        var due = TaskDueCalculator.DueMomentUtc(Task("2026-10-25", "02:30"), Berlin);
        Assert.Equal(new DateTime(2026, 10, 25, 1, 30, 0, DateTimeKind.Utc), due);
    }

    [Fact]
    public void ReminderMoment_через_границу_DST_вычитается_в_UTC()
    {
        // Срок 29.03.2026 12:00 CEST (+2) = 10:00 UTC; напоминание за 12 часов.
        // Офсет вычитается из UTC-момента срока: 28.03 22:00 UTC (локально это 23:00 CET,
        // между напоминанием и сроком проходит 12 реальных часов, а не 13 «настенных»)
        var remind = TaskDueCalculator.ReminderMomentUtc(
            Task("2026-03-29", "12:00", reminderMinutes: 12 * 60), Berlin);
        Assert.Equal(new DateTime(2026, 3, 28, 22, 0, 0, DateTimeKind.Utc), remind);
    }

    [Fact]
    public void ReminderMoment_fall_back_вычитается_в_UTC()
    {
        // Срок 25.10.2026 12:00 CET (+1) = 11:00 UTC; напоминание за 12 часов = 24.10 23:00 UTC
        var remind = TaskDueCalculator.ReminderMomentUtc(
            Task("2026-10-25", "12:00", reminderMinutes: 12 * 60), Berlin);
        Assert.Equal(new DateTime(2026, 10, 24, 23, 0, 0, DateTimeKind.Utc), remind);
    }
}
