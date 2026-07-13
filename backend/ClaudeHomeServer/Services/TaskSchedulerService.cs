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
    IHubContext<SessionHub> hub,
    PushService push,
    TaskExecutionService executor,
    DailyBriefingService briefing,
    PersonaAutomationService automation,
    NotificationService notif,
    ILogger<TaskSchedulerService> log) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    // Автозапуск только для сроков, наступивших недавно: защита от лавины сессий
    // по старым просроченным задачам при включении флага или долгом простое сервера
    internal static readonly TimeSpan AutoStartWindow = TimeSpan.FromHours(24);

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
            var tz = TaskDueCalculator.ResolveTimeZone(user.TimeZone);
            foreach (var task in tasks.GetByOwner(user.Id))
            {
                if (task.Status == TaskItemStatus.Done) continue;
                await ProcessReminderAsync(task, tz, nowUtc);
                await ProcessClaudeAutoStartAsync(task, tz, nowUtc);
            }
            // Утренний бриф раз в день в таймзоне юзера (быстрый выход, если не время/выключено)
            await briefing.MaybeRunScheduledAsync(user, tz, nowUtc);
            // Проактивность персон (collaborator): оценка правил автоматизаций этого пользователя
            await automation.MaybeRunAutomationsAsync(user, tz, nowUtc, CancellationToken.None);
        }
    }

    // Чистые предикаты выбора задач — извлечены из Process*-методов для юнит-тестов

    // Пора ли напоминать: напоминание настроено, ещё не отправлялось и момент наступил
    internal static bool ShouldRemind(TaskItem task, TimeZoneInfo tz, DateTime nowUtc)
    {
        if (task.ReminderSentAt is not null) return false;
        var remindAt = TaskDueCalculator.ReminderMomentUtc(task, tz);
        return remindAt is not null && remindAt <= nowUtc;
    }

    // Пора ли автозапускать исполнителя: assignee=Claude, ещё не запускалась,
    // срок наступил и не старше AutoStartWindow (защита от лавины по старым задачам)
    internal static bool ShouldAutoStart(TaskItem task, TimeZoneInfo tz, DateTime nowUtc)
    {
        if (task.Assignee != TaskItemAssignee.Claude) return false;
        if (task.Status != TaskItemStatus.Todo || task.ClaudeStartedAt is not null) return false;

        var dueUtc = TaskDueCalculator.DueMomentUtc(task, tz);
        return dueUtc is not null && dueUtc <= nowUtc && nowUtc - dueUtc <= AutoStartWindow;
    }

    // Автозапуск Claude-исполнителя в момент срока: assignee=Claude, ещё не запускалась
    private async Task ProcessClaudeAutoStartAsync(TaskItem task, TimeZoneInfo tz, DateTime nowUtc)
    {
        if (!ShouldAutoStart(task, tz, nowUtc)) return;

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
        if (!ShouldRemind(task, tz, nowUtc)) return;

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

    // Тост + сторадж + web push через единый NotificationService
    private async Task SendNotificationAsync(TaskItem task, NotificationMessage message)
    {
        await notif.SendNotificationMessageAsync(task.OwnerId!, message, sendPush: true);
    }

    // Hash-диплинк на задачу: проектная → детали в проекте, личная → модалка в календаре
    internal static string TaskUrl(TaskItem task) =>
        task.ProjectId is null
            ? $"/calendar/task/{task.Id}"
            : $"/project/{task.ProjectId}/task/{task.Id}";
}
