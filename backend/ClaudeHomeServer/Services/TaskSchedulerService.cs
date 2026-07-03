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
    ILogger<TaskSchedulerService> log) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

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
            if (!remindersOn) continue;

            var tz = TaskDueCalculator.ResolveTimeZone(user.TimeZone);
            foreach (var task in tasks.GetByOwner(user.Id))
            {
                if (task.Status == TaskItemStatus.Done) continue;
                await ProcessReminderAsync(task, tz, nowUtc);
            }
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
