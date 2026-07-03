using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

// Фоновый планировщик задач: напоминания к сроку и автозапуск Claude-исполнителя.
// Один на приложение, тик каждые 30 с. Идемпотентность — отметки на самой задаче
// (ReminderSentAt, ClaudeStartedAt), переживают рестарт сервера.
public class TaskSchedulerService(
    TaskManager tasks,
    UserStore users,
    FeatureFlagService flags,
    IHubContext<SessionHub> hub,
    PushService push,
    TaskExecutionService executor,
    ILogger<TaskSchedulerService> log) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    // Автозапуск только для сроков, наступивших недавно: защита от лавины сессий
    // по старым просроченным задачам при включении флага или долгом простое сервера
    private static readonly TimeSpan AutoStartWindow = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await TickAsync(DateTime.UtcNow); }
                catch (Exception ex) { log.LogError(ex, "Ошибка тика планировщика задач"); }
            }
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
    }

    // Публичный для юнит-тестов: один проход по всем пользователям
    public async Task TickAsync(DateTime nowUtc)
    {
        foreach (var user in users.GetAll())
        {
            var effective = flags.GetEffective(user.Id);
            var remindersOn = effective.GetValueOrDefault("task-reminders");
            var execOn = effective.GetValueOrDefault("task-claude-exec");
            if (!remindersOn && !execOn) continue;

            var tz = TaskDueCalculator.ResolveTimeZone(user.TimeZone);
            foreach (var task in tasks.GetByOwner(user.Id))
            {
                if (task.Status == TaskItemStatus.Done) continue;
                if (remindersOn) await ProcessReminderAsync(task, tz, nowUtc);
                if (execOn) await ProcessClaudeAutoStartAsync(task, tz, nowUtc);
            }
        }
    }

    // Автозапуск Claude-исполнителя в момент срока: assignee=Claude, ещё не запускалась
    private async Task ProcessClaudeAutoStartAsync(TaskItem task, TimeZoneInfo tz, DateTime nowUtc)
    {
        if (task.Assignee != TaskItemAssignee.Claude) return;
        if (task.Status != TaskItemStatus.Todo || task.ClaudeStartedAt is not null) return;

        var dueUtc = TaskDueCalculator.DueMomentUtc(task, tz);
        if (dueUtc is null || dueUtc > nowUtc || nowUtc - dueUtc > AutoStartWindow) return;

        try
        {
            await executor.ExecuteAsync(task, auto: true);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Автозапуск Claude-исполнителя не удался: задача {TaskId} «{Title}»",
                task.Id, task.Title);
        }
    }

    private async Task ProcessReminderAsync(TaskItem task, TimeZoneInfo tz, DateTime nowUtc)
    {
        if (task.ReminderSentAt is not null) return;
        var remindAt = TaskDueCalculator.ReminderMomentUtc(task, tz);
        if (remindAt is null || remindAt > nowUtc) return;

        var updated = tasks.MarkReminderSent(task.Id, nowUtc);
        if (updated is null) return; // задача удалена между чтением и отметкой

        var dueText = task.DueTime is null ? task.DueDate : $"{task.DueDate} {task.DueTime}";
        await SendNotificationAsync(updated, new NotificationMessage(
            Title: "Напоминание о задаче",
            Body: $"{updated.Title} — срок {dueText}",
            Url: TaskUrl(updated),
            Kind: "reminder"));
        // Синхронизируем сторы клиентов (ReminderSentAt изменился)
        await hub.BroadcastTaskChangedAsync(updated.OwnerId!, "updated", updated);

        log.LogInformation("Напоминание отправлено: задача {TaskId} «{Title}»", updated.Id, updated.Title);
    }

    // Тост в открытом приложении (SignalR) + web push на подписанные устройства
    private async Task SendNotificationAsync(TaskItem task, NotificationMessage message)
    {
        await hub.Clients.Group("user_" + task.OwnerId).SendAsync("message", message);
        await push.SendToUserAsync(task.OwnerId!, message);
    }

    // Hash-диплинк на задачу: проектная → детали в проекте, личная → модалка в календаре
    internal static string TaskUrl(TaskItem task) =>
        task.ProjectId is null
            ? $"/#/calendar/task/{task.Id}"
            : $"/#/project/{task.ProjectId}/task/{task.Id}";
}
