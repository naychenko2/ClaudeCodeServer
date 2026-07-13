using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Тесты извлечённых предикатов планировщика (ShouldRemind / ShouldAutoStart)
public class TaskSchedulerServiceTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    // «Сейчас»: 2026-07-03 12:00 UTC
    private static readonly DateTime Now = new(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);

    private static TaskItem Task(
        string? dueDate = "2026-07-03", string? dueTime = "12:00",
        int? reminderMinutes = null, DateTime? reminderSentAt = null,
        TaskItemAssignee? assignee = null, TaskItemStatus status = TaskItemStatus.Todo,
        DateTime? claudeStartedAt = null) => new()
    {
        Title = "t",
        DueDate = dueDate,
        DueTime = dueTime,
        ReminderMinutes = reminderMinutes,
        ReminderSentAt = reminderSentAt,
        Assignee = assignee,
        Status = status,
        ClaudeStartedAt = claudeStartedAt,
    };

    // ─── ShouldRemind ────────────────────────────────────────────────────────

    [Fact]
    public void ShouldRemind_МоментНаступил_True()
    {
        // Срок 12:30, напоминание за 30 мин → момент 12:00 == Now
        var task = Task(dueTime: "12:30", reminderMinutes: 30);
        TaskSchedulerService.ShouldRemind(task, Utc, Now).Should().BeTrue();
    }

    [Fact]
    public void ShouldRemind_МоментЕщёНеНаступил_False()
    {
        // Срок 14:00, напоминание за 30 мин → момент 13:30 > Now
        var task = Task(dueTime: "14:00", reminderMinutes: 30);
        TaskSchedulerService.ShouldRemind(task, Utc, Now).Should().BeFalse();
    }

    [Fact]
    public void ShouldRemind_УжеОтправлено_False_недублирование()
    {
        var task = Task(dueTime: "12:30", reminderMinutes: 30,
            reminderSentAt: Now.AddMinutes(-5));
        TaskSchedulerService.ShouldRemind(task, Utc, Now).Should().BeFalse();
    }

    [Fact]
    public void ShouldRemind_НапоминаниеНеНастроено_False()
    {
        TaskSchedulerService.ShouldRemind(Task(), Utc, Now).Should().BeFalse();
    }

    [Fact]
    public void ShouldRemind_СрокаНет_False()
    {
        var task = Task(dueDate: null, reminderMinutes: 30);
        TaskSchedulerService.ShouldRemind(task, Utc, Now).Should().BeFalse();
    }

    // ─── ShouldAutoStart ─────────────────────────────────────────────────────

    [Fact]
    public void ShouldAutoStart_СрокНаступил_True()
    {
        var task = Task(dueTime: "11:00", assignee: TaskItemAssignee.Claude);
        TaskSchedulerService.ShouldAutoStart(task, Utc, Now).Should().BeTrue();
    }

    [Fact]
    public void ShouldAutoStart_СрокВБудущем_False()
    {
        var task = Task(dueTime: "13:00", assignee: TaskItemAssignee.Claude);
        TaskSchedulerService.ShouldAutoStart(task, Utc, Now).Should().BeFalse();
    }

    [Fact]
    public void ShouldAutoStart_ОкноАвтозапуска24Часа()
    {
        // Ровно 24 часа назад — ещё в окне
        var edge = Task(dueDate: "2026-07-02", dueTime: "12:00", assignee: TaskItemAssignee.Claude);
        TaskSchedulerService.ShouldAutoStart(edge, Utc, Now).Should().BeTrue();

        // Старше 24 часов — окно закрыто (защита от лавины по старым задачам)
        var stale = Task(dueDate: "2026-07-02", dueTime: "11:59", assignee: TaskItemAssignee.Claude);
        TaskSchedulerService.ShouldAutoStart(stale, Utc, Now).Should().BeFalse();
    }

    [Fact]
    public void ShouldAutoStart_ИсполнительНеClaude_False()
    {
        TaskSchedulerService.ShouldAutoStart(
            Task(dueTime: "11:00", assignee: TaskItemAssignee.Me), Utc, Now).Should().BeFalse();
        TaskSchedulerService.ShouldAutoStart(
            Task(dueTime: "11:00", assignee: null), Utc, Now).Should().BeFalse();
    }

    [Fact]
    public void ShouldAutoStart_УжеЗапускалась_False_недублирование()
    {
        var task = Task(dueTime: "11:00", assignee: TaskItemAssignee.Claude,
            claudeStartedAt: Now.AddMinutes(-10));
        TaskSchedulerService.ShouldAutoStart(task, Utc, Now).Should().BeFalse();
    }

    [Fact]
    public void ShouldAutoStart_СтатусНеTodo_False()
    {
        var inProgress = Task(dueTime: "11:00", assignee: TaskItemAssignee.Claude,
            status: TaskItemStatus.InProgress);
        TaskSchedulerService.ShouldAutoStart(inProgress, Utc, Now).Should().BeFalse();

        var done = Task(dueTime: "11:00", assignee: TaskItemAssignee.Claude,
            status: TaskItemStatus.Done);
        TaskSchedulerService.ShouldAutoStart(done, Utc, Now).Should().BeFalse();
    }

    // ─── TaskUrl ─────────────────────────────────────────────────────────────

    [Fact]
    public void TaskUrl_ПроектнаяИЛичная_РазныеДиплинки()
    {
        var personal = new TaskItem { Title = "t" };
        TaskSchedulerService.TaskUrl(personal).Should().Be($"/calendar/task/{personal.Id}");

        var project = new TaskItem { Title = "t", ProjectId = "p1" };
        TaskSchedulerService.TaskUrl(project).Should().Be($"/project/p1/task/{project.Id}");
    }
}
